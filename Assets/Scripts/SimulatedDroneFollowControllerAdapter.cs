using UnityEngine;

/// <summary>
/// Bridges the existing simulated controller to the versioned DroneFollow
/// controller contract. The policy receives body-frame gyro data rather than
/// reading a Rigidbody directly.
/// </summary>
[RequireComponent(typeof(SimulatedDroneController), typeof(Rigidbody))]
public sealed class SimulatedDroneFollowControllerAdapter : MonoBehaviour, IDroneFollowController
{
    SimulatedDroneController _controller;
    Rigidbody _body;

    SimulatedDroneController Controller
    {
        get
        {
            if (_controller == null) _controller = GetComponent<SimulatedDroneController>();
            return _controller;
        }
    }

    Rigidbody Body
    {
        get
        {
            if (_body == null) _body = GetComponent<Rigidbody>();
            return _body;
        }
    }

    public DroneState State => Controller.State;
    public NavdataPacket Navdata => Controller.Navdata;
    public bool IsReady => Controller.IsReady;

    // Unity stores Rigidbody angular velocity in world-space rad/s. Convert it
    // once here so the observation contract always receives body-frame rates.
    public Vector3 BodyAngularVelocityRadSec =>
        transform.InverseTransformDirection(Body.angularVelocity);

    public void Takeoff() => Controller.Takeoff();
    public void Land() => Controller.Land();
    public void EmergencyReset() => Controller.EmergencyReset();
    public void Move(float forward, float left, float up, float turn) =>
        Controller.Move(forward, left, up, turn);
    public void Hover() => Controller.Hover();

    /// <summary>Training-only reset; a hardware controller must not expose this.</summary>
    public void ResetForTraining(Vector3 position, Quaternion rotation)
    {
        Controller.ResetAndTakeoff(position, rotation);
    }
}
