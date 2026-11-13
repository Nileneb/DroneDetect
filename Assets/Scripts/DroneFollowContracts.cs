using UnityEngine;

/// <summary>
/// Shared sensor/action contract for DroneFollow. A real hardware controller can
/// implement this alongside IDroneController without exposing simulator objects.
/// </summary>
public interface IDroneFollowController : IDroneController
{
    /// <summary>Angular rate in canonical body-frame XYZ, radians per second.</summary>
    Vector3 BodyAngularVelocityRadSec { get; }
}

/// <summary>Normalized detector output consumed by the DroneFollow policy.</summary>
public struct DroneTargetDetection
{
    public float CenterX;
    public float CenterY;
    public float Height;
    public bool Visible;
    public float CaptureTime;

    public DroneTargetDetection(float centerX, float centerY, float height, bool visible, float captureTime)
    {
        CenterX = centerX;
        CenterY = centerY;
        Height = height;
        Visible = visible;
        CaptureTime = captureTime;
    }

    public static DroneTargetDetection None(float captureTime = 0f)
    {
        return new DroneTargetDetection(0f, 0f, 0f, false, captureTime);
    }
}

/// <summary>Common interface for simulated and hardware target detectors.</summary>
public interface IDroneTargetDetectionSource
{
    DroneTargetDetection Current { get; }
    void ResetDetection();
}
