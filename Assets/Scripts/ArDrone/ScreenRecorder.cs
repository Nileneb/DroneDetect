using UnityEngine;

/// <summary>
/// Records the whole ground-station screen (video, boxes, telemetry) to Movies/DroneFollow on the phone.
/// Android only (MediaProjection via Assets/Plugins/Android/ScreenRecorder.java); no-op in the Editor.
/// </summary>
public static class ScreenRecorder
{
    const string JavaClass = "dronedetect.rec.ScreenRecorder";
    static float _startedAt;

    public static bool IsRecording
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try { using var recorder = new AndroidJavaClass(JavaClass); return recorder.CallStatic<bool>("isRecording"); }
            catch (System.Exception exception) { Debug.LogException(exception); }
#endif
            return false;
        }
    }

    public static float RecordedSeconds => IsRecording ? Time.realtimeSinceStartup - _startedAt : 0f;

    /// <summary>Starts recording; Android shows its screen-capture consent dialog first.</summary>
    public static void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var recorder = new AndroidJavaClass(JavaClass);
            recorder.CallStatic("start", activity);
            _startedAt = Time.realtimeSinceStartup;
        }
        catch (System.Exception exception) { Debug.LogException(exception); }
#else
        Debug.Log("[ScreenRecorder] Recording is only available on Android.");
#endif
    }

    public static void Stop()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var recorder = new AndroidJavaClass(JavaClass);
            recorder.CallStatic("stop", activity);
        }
        catch (System.Exception exception) { Debug.LogException(exception); }
#endif
    }
}
