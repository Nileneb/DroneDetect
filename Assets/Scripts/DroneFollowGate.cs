using UnityEngine;

/// <summary>Invisible trigger plane at a path waypoint; reports the drone flying through it.</summary>
[RequireComponent(typeof(BoxCollider))]
public sealed class DroneFollowGate : MonoBehaviour
{
    public DroneFollowPath path;
    [Tooltip("Index of the waypoint this gate stands at.")]
    public int waypointIndex;

    void OnTriggerEnter(Collider other)
    {
        DroneFollowAgent agent = other.GetComponentInParent<DroneFollowAgent>();
        if (agent != null)
            agent.OnGateEntered(this);
    }
}
