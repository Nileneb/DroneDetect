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
    const int DemoStepLimit = 5000;
    const string DemoName = "DroneFollowDemo";
    static readonly string DemoDirectory = Path.GetFullPath(
        Path.Combine(Application.dataPath, "..", "results", "demonstrations"));

    static DroneFollowDemoTools()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    [MenuItem("DroneDetect/DroneFollow/Record Scripted Demonstration (5000 steps)")]
    static void RecordScriptedDemonstration()
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
        SessionState.SetBool(RestoreKey, true);

        behavior.BehaviorType = BehaviorType.HeuristicOnly;
        recorder.enabled = true;
        recorder.Record = true;
        recorder.NumStepsToRecord = DemoStepLimit;
        recorder.DemonstrationName = DemoName;
        recorder.DemonstrationDirectory = DemoDirectory;

        SessionState.SetBool(PendingKey, true);
        Debug.Log($"[DroneFollowDemo] Starting a temporary HeuristicOnly session. The scene settings will be restored after Play mode. Recording to '{DemoDirectory}'.");
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
        DroneFollowDemoRoute route = agent.followTarget.GetComponent<DroneFollowDemoRoute>();
        if (route == null)
            route = agent.followTarget.gameObject.AddComponent<DroneFollowDemoRoute>();

        if (recorder == null || !route.Begin(agent))
        {
            Debug.LogError("[DroneFollowDemo] Could not initialize the recorder and NavMesh target route.");
            EditorApplication.isPlaying = false;
            return;
        }

        recorder.enabled = true;
        recorder.Record = true;
        recorder.NumStepsToRecord = DemoStepLimit;
        recorder.DemonstrationName = DemoName;
        recorder.DemonstrationDirectory = DemoDirectory;
        Debug.Log($"[DroneFollowDemo] Recording {DemoStepLimit} decisions. PPO is stopped; the DroneFollow heuristic is controlling the drone.");
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

        SessionState.SetBool(RestoreKey, false);
        Debug.Log("[DroneFollowDemo] Restored the scene's saved PPO and recorder settings.");
    }
}
