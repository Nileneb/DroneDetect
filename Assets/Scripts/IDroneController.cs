/// <summary>
/// IDroneController — Interface fuer den AR-Drone 2.0 Digital Twin.
///
/// Methoden-Semantik = ardrone_autonomy AT*-Kommandos:
///   Takeoff()  →  AT*REF (start)
///   Land()     →  AT*REF (land)
///   Move()     →  AT*PCMD (pitch, roll, gaz, yaw)
///   Hover()    →  AT*PCMD mit allen Achsen = 0
/// </summary>
public interface IDroneController
{
    DroneState State { get; }
    NavdataPacket Navdata { get; }
    bool IsReady { get; }

    void Takeoff();
    void Land();
    void EmergencyReset();
    void Move(float forward, float left, float up, float turn);
    void Hover();
}
