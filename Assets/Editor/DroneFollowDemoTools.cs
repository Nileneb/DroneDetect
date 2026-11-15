using System.IO;
using UnityEditor;
using Unity.MLAgents;
using Unity.MLAgents.Demonstrations;
using Unity.MLAgents.Policies;
using UnityEngine;

[InitializeOnLoad]
public static class DroneFollowDemoTools
{
    const string PendingKey = "DroneFollowDemo.Pending";
    const string RestoreKey = "DroneFollowDemo.Restore";
    const string BehaviorTypeKey = "DroneFollowDemo.BehaviorType";
    const string RecorderEnabledKey = "DroneFollowDemo.RecorderEnabled";
    const string RecorderRecordKey = "DroneFollowDemo.RecorderRecord";
    const string RecorderStepsKey = "DroneFollowDemo.RecorderSteps";
    const string RecorderNameKey = "DroneFollowDemo.RecorderName";
    const string RecorderDirectoryKey = "DroneFollowDemo.RecorderDirectory";
    const string RecordingNameKey = "DroneFollowDemo.RecordingName";
    const string ExpertKey = "DroneFollowDemo.ScriptedExpert";
    const int ExpertDemoSteps = 30000;
    // Same pacing as config/DroneFollow.yaml (time_scale 4, ML-Agents' 60 captured frames per second),
    // so perception runs at the identical rate per simulated second as during training.
    const float ExpertTimeScale = 4f;
    const int ExpertCaptureFramerate = 60;
    const string DemoName = "DroneFollowDemo";
    static readonly string DemoDirectory = Path.GetFullPath(
        Path.Combine(Application.dataPath, "..", "results", "demonstrations"));
    // WHY(spec, demos): manual flights are recorded one path at a time and must accumulate; config
    // DroneFollow.yaml loads every .demo file in this folder.
    static readonly string ManualDemoDirectory = Path.Combine(DemoDirectory, "manual");

    static string RecordingDirectory(bool scriptedExpert) => scriptedExpert ? DemoDirectory : ManualDemoDirectory;

    static DroneFollowDemoTools()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    [MenuItem("DroneDetect/DroneFollow/Record Manual Demonstration")]
    static void RecordManualDemonstration() => StartRecording(false);

    [MenuItem("DroneDetect/DroneFollow/Record Scripted Expert Demonstration")]
    static void RecordExpertDemonstration() => StartRecording(true);

    static void StartRecording(bool scriptedExpert)
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[DroneFollowDemo] Stop Play mode before starting a demo recording.");
            return;
        }

        DroneFollowAgent agent = Object.FindAnyObjectByType<DroneFollowAgent>();
        if (agent == null)
        {
            Debug.LogError("[DroneFollowDemo] No active DroneFollowAgent was found in the open scene.");
            return;
        }

        BehaviorParameters behavior = agent.GetComponent<BehaviorParameters>();
        if (behavior == null || behavior.BehaviorName != DroneFollowContract.BehaviorName)
        {
            Debug.LogError($"[DroneFollowDemo] Expected BehaviorParameters named '{DroneFollowContract.BehaviorName}' on the agent.");
            return;
        }

        DemonstrationRecorder recorder = agent.GetComponent<DemonstrationRecorder>();
        if (recorder == null)
        {
            Debug.LogError("[DroneFollowDemo] DemonstrationRecorder is missing from the DroneFollow agent.");
            return;
        }

        SessionState.SetInt(BehaviorTypeKey, (int)behavior.BehaviorType);
        SessionState.SetBool(RecorderEnabledKey, recorder.enabled);
        SessionState.SetBool(RecorderRecordKey, recorder.Record);
        SessionState.SetInt(RecorderStepsKey, recorder.NumStepsToRecord);
        SessionState.SetString(RecorderNameKey, recorder.DemonstrationName);
        SessionState.SetString(RecorderDirectoryKey, recorder.DemonstrationDirectory);
        string recordingName;
        do
        {
            recordingName = "DF" + System.Guid.NewGuid().ToString("N").Substring(0, 8);
        }
        while (File.Exists(Path.Combine(RecordingDirectory(scriptedExpert), recordingName + ".demo")));
        Directory.CreateDirectory(RecordingDirectory(scriptedExpert));
        SessionState.SetString(RecordingNameKey, recordingName);
        SessionState.SetBool(RestoreKey, true);

        behavior.BehaviorType = BehaviorType.HeuristicOnly;
        recorder.enabled = true;
        recorder.Record = true;
        recorder.NumStepsToRecord = 0;
        recorder.DemonstrationName = recordingName;
        recorder.DemonstrationDirectory = RecordingDirectory(scriptedExpert);

        SessionState.SetBool(ExpertKey, scriptedExpert);
        SessionState.SetBool(PendingKey, true);
        Debug.Log(scriptedExpert
            ? $"[DroneFollowDemo] Starting scripted-expert recording. Stop Play mode to publish it as '{DemoName}.demo' in '{DemoDirectory}'."
            : $"[DroneFollowDemo] Starting manual recording. Gamepad (Mode 2): left stick = climb/yaw, right stick = forward/sideways; keyboard fallback WASD, Space/Shift, Q/E. The sim lands after the target is home and starts the next path. Stop Play mode to keep the recording in '{ManualDemoDirectory}'.");
        EditorApplication.isPlaying = true;
    }

    static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(PendingKey, false))
        {
            SessionState.SetBool(PendingKey, false);
            EditorApplication.delayCall += StartRuntimeDemo;
        }
        else if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(RestoreKey, false))
        {
            EditorApplication.delayCall += RestoreSceneSettings;
        }
    }

    static void StartRuntimeDemo()
    {
        DroneFollowAgent agent = Object.FindAnyObjectByType<DroneFollowAgent>();
        if (agent == null || agent.followTarget == null)
        {
            Debug.LogError("[DroneFollowDemo] DroneFollowAgent or followTarget was not found after entering Play mode.");
            EditorApplication.isPlaying = false;
            return;
        }

        if (Academy.Instance.IsCommunicatorOn)
        {
            Debug.LogError("[DroneFollowDemo] A trainer is connected. Stop the PPO trainer and start the demo again.");
            EditorApplication.isPlaying = false;
            return;
        }

        DemonstrationRecorder recorder = agent.GetComponent<DemonstrationRecorder>();
        BehaviorParameters behavior = agent.GetComponent<BehaviorParameters>();
        if (recorder == null || behavior == null)
        {
            Debug.LogError("[DroneFollowDemo] DemonstrationRecorder or BehaviorParameters is missing.");
            EditorApplication.isPlaying = false;
            return;
        }

        bool scriptedExpert = SessionState.GetBool(ExpertKey, false);
        behavior.BehaviorType = BehaviorType.HeuristicOnly;
        recorder.enabled = true;
        recorder.Record = true;
        recorder.NumStepsToRecord = scriptedExpert ? ExpertDemoSteps : 0;
        recorder.DemonstrationName = SessionState.GetString(RecordingNameKey, string.Empty);
        recorder.DemonstrationDirectory = RecordingDirectory(scriptedExpert);
        if (scriptedExpert)
        {
            agent.useScriptedExpert = true;
            Time.timeScale = ExpertTimeScale;
            Time.captureFramerate = ExpertCaptureFramerate;
            Debug.Log($"[DroneFollowDemo] Scripted expert is flying; {ExpertDemoSteps} steps are recorded, then stop Play mode to publish.");
        }
        else
        {
            Debug.Log("[DroneFollowDemo] Manual control is active. Recording continues until Play mode is stopped.");
        }
    }

    static void RestoreSceneSettings()
    {
        DroneFollowAgent agent = Object.FindAnyObjectByType<DroneFollowAgent>();
        DemonstrationRecorder recorder = agent != null ? agent.GetComponent<DemonstrationRecorder>() : null;
        BehaviorParameters behavior = agent != null ? agent.GetComponent<BehaviorParameters>() : null;
        if (recorder != null && behavior != null)
        {
            behavior.BehaviorType = (BehaviorType)SessionState.GetInt(BehaviorTypeKey, (int)BehaviorType.Default);
            recorder.enabled = SessionState.GetBool(RecorderEnabledKey, false);
            recorder.Record = SessionState.GetBool(RecorderRecordKey, false);
            recorder.NumStepsToRecord = SessionState.GetInt(RecorderStepsKey, 0);
            recorder.DemonstrationName = SessionState.GetString(RecorderNameKey, string.Empty);
            recorder.DemonstrationDirectory = SessionState.GetString(RecorderDirectoryKey, string.Empty);
        }

        string recordingName = SessionState.GetString(RecordingNameKey, string.Empty);
        if (!string.IsNullOrEmpty(recordingName))
        {
            if (SessionState.GetBool(ExpertKey, false))
                PublishRecording(recordingName);
            else
                ReportManualRecording(recordingName);
        }
        SessionState.SetString(RecordingNameKey, string.Empty);
        SessionState.SetBool(RestoreKey, false);
        Debug.Log("[DroneFollowDemo] Restored the scene's saved PPO and recorder settings.");
    }

    static void ReportManualRecording(string recordingName)
    {
        string[] files = Directory.Exists(ManualDemoDirectory)
            ? Directory.GetFiles(ManualDemoDirectory, recordingName + "*.demo")
            : new string[0];
        if (files.Length == 0 || new FileInfo(files[0]).Length == 0)
        {
            Debug.LogError($"[DroneFollowDemo] Manual recording '{recordingName}' was not written to '{ManualDemoDirectory}'.");
            return;
        }
        int total = Directory.GetFiles(ManualDemoDirectory, "*.demo").Length;
        Debug.Log($"[DroneFollowDemo] Kept manual recording '{files[0]}'; {total} manual demo file(s) are now used for training.");
    }

    static void PublishRecording(string recordingName)
    {
        string recordedPath = Path.Combine(DemoDirectory, recordingName + ".demo");
        string publishedPath = Path.Combine(DemoDirectory, DemoName + ".demo");
        string stagingPath = publishedPath + ".new";
        string backupPath = publishedPath + ".bak";
        if (!File.Exists(recordedPath))
        {
            Debug.LogError($"[DroneFollowDemo] Recording was not created at '{recordedPath}'. The previous training demo was left unchanged.");
            return;
        }

        try
        {
            long recordedLength = new FileInfo(recordedPath).Length;
            if (recordedLength == 0)
            {
                Debug.LogError($"[DroneFollowDemo] Recording at '{recordedPath}' is empty. The previous training demo was left unchanged.");
                return;
            }

            File.Copy(recordedPath, stagingPath, true);
            if (new FileInfo(stagingPath).Length != recordedLength)
                throw new IOException("The staged demonstration size did not match the recorded file.");

            if (File.Exists(publishedPath))
            {
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                File.Replace(stagingPath, publishedPath, backupPath);
            }
            else
                File.Move(stagingPath, publishedPath);
        }
        catch (IOException exception)
        {
            Debug.LogError($"[DroneFollowDemo] Could not replace the training demonstration. The new recording is preserved at '{recordedPath}'. Details: {exception.Message}");
            return;
        }
        catch (System.UnauthorizedAccessException exception)
        {
            Debug.LogError($"[DroneFollowDemo] Permission denied while publishing the training demonstration. The new recording is preserved at '{recordedPath}'. Details: {exception.Message}");
            return;
        }

        try
        {
            if (File.Exists(backupPath))
                File.Delete(backupPath);
            File.Delete(recordedPath);
        }
        catch (IOException exception)
        {
            Debug.LogWarning($"[DroneFollowDemo] Published the training demo, but temporary-file cleanup failed: {exception.Message}");
        }
        catch (System.UnauthorizedAccessException exception)
        {
            Debug.LogWarning($"[DroneFollowDemo] Published the training demo, but temporary-file cleanup was denied: {exception.Message}");
        }

        Debug.Log($"[DroneFollowDemo] Published the manual recording to '{publishedPath}'. Both BC and GAIL training configs use this path.");
    }
}
