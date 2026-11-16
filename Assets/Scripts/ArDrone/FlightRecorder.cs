using System;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Writes one CSV row per navdata update next to the video recording: what the drone reported, what the
/// policy wanted, and what actually went out as AT*PCMD. Files live in Application.persistentDataPath
/// (Android: /sdcard/Android/data/&lt;package&gt;/files, readable with adb pull, no permission needed).
/// </summary>
public sealed class FlightRecorder : IDisposable
{
    const string Header =
        "t,state_hex,state_flags,ctrl_state,battery,alt,roll,pitch,yaw,vx,vy,vz," +
        "policy_fwd,policy_left,policy_up,policy_turn,pcmd_roll,pcmd_pitch,pcmd_gaz,pcmd_yaw,ref," +
        "shadow,safety,target_visible,people,best_person_score,video_frames";

    readonly StreamWriter _writer;
    float _lastFlush;

    public string Path { get; }

    public FlightRecorder(string name)
    {
        string directory = Application.persistentDataPath;
        Path = System.IO.Path.Combine(directory, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        _writer = new StreamWriter(Path) { AutoFlush = false };
        _writer.WriteLine(Header);
        Debug.Log($"[FlightRecorder] Writing {Path}");
    }

    public void Write(float time, in ArDroneProtocol.Navdata nav,
        Vector4 policy, (float roll, float pitch, float gaz, float yaw, int refValue) pcmd,
        bool shadow, string safety, bool targetVisible, int people, float personScore, int videoFrames)
    {
        string row = string.Format(CultureInfo.InvariantCulture,
            "{0:F2},{1:X8},{2},{3},{4},{5:F3},{6:F1},{7:F1},{8:F1},{9:F2},{10:F2},{11:F2}," +
            "{12:F2},{13:F2},{14:F2},{15:F2},{16:F2},{17:F2},{18:F2},{19:F2},{20},{21},{22},{23},{24},{25:F2},{26}",
            time, nav.DroneState, ArDroneProtocol.DescribeState(nav.DroneState), nav.ControlState, nav.BatteryPercent,
            nav.AltitudeM, nav.RollDeg, nav.PitchDeg, nav.YawDeg, nav.VxMs, nav.VyMs, nav.VzMs,
            policy.x, policy.y, policy.z, policy.w, pcmd.roll, pcmd.pitch, pcmd.gaz, pcmd.yaw, pcmd.refValue,
            shadow ? 1 : 0, safety.Replace(',', ';'), targetVisible ? 1 : 0, people, personScore, videoFrames);
        _writer.WriteLine(row);
        if (Time.realtimeSinceStartup - _lastFlush > 1f)
        {
            _writer.Flush();
            _lastFlush = Time.realtimeSinceStartup;
        }
    }

    public void Dispose()
    {
        _writer.Flush();
        _writer.Dispose();
    }
}
