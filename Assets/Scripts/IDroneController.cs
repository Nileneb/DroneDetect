/// <summary>
/// Drone command/telemetry interface shared by the simulator and the AR.Drone 2.0
/// hardware adapter. Semantics follow the AR.Drone AT commands (AT*REF, AT*PCMD).
/// </summary>
public interface IDroneController
{
    DroneState State { get; }

    /// <summary>
    /// Telemetry in simulator conventions: vx forward, vy right, vz up (m/s);
    /// rotX roll (+ = right side down), rotY pitch (+ = nose down),
    /// rotZ yaw (+ = clockwise seen from above), all in degrees.
    /// </summary>
    NavdataPacket Navdata { get; }

    bool IsReady { get; }

    void Takeoff();
    void Land();
    void EmergencyReset();

    /// <summary>All inputs in [-1, 1]; turn &gt; 0 yaws clockwise (right) seen from above.</summary>
    void Move(float forward, float left, float up, float turn);

    void Hover();
}
