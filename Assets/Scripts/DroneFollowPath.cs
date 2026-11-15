using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A hand-authored closed walking loop for the follow target. Children of the "Waypoints" child are
/// the waypoints in order (index 0 sits next to the start/landing pad); children of "Gates" are the
/// trigger planes built by DroneDetect > DroneFollow > Rebuild Path Gates.
/// </summary>
public sealed class DroneFollowPath : MonoBehaviour
{
    public const string WaypointRootName = "Waypoints";
    public const string GateRootName = "Gates";

    [Tooltip("Curriculum level this path belongs to: 0 = flat and gentle ... 3 = hilly with tight turns.")]
    [Range(0, 3)] public int difficulty;

    readonly List<Vector3> _waypoints = new List<Vector3>();
    readonly List<DroneFollowGate> _gates = new List<DroneFollowGate>();

    public IReadOnlyList<Vector3> Waypoints
    {
        get
        {
            Refresh();
            return _waypoints;
        }
    }

    public IReadOnlyList<DroneFollowGate> Gates
    {
        get
        {
            Refresh();
            return _gates;
        }
    }

    /// <summary>Length of the closed loop in metres (last waypoint back to the first included).</summary>
    public float Length
    {
        get
        {
            IReadOnlyList<Vector3> points = Waypoints;
            float length = 0f;
            for (int i = 0; i < points.Count; i++)
                length += Vector3.Distance(points[i], points[(i + 1) % points.Count]);
            return length;
        }
    }

    /// <summary>Waypoint indices in walking order, starting and ending at waypoint 0.</summary>
    public int[] VisitOrder(bool reverse)
    {
        int count = Waypoints.Count;
        var order = new int[count + 1];
        for (int step = 0; step < count; step++)
            order[step] = reverse ? (count - step) % count : step;
        order[count] = 0;
        return order;
    }

    public void SetGatesActive(bool active)
    {
        foreach (DroneFollowGate gate in Gates)
            gate.gameObject.SetActive(active);
    }

    void Refresh()
    {
        _waypoints.Clear();
        _gates.Clear();
        Transform waypointRoot = transform.Find(WaypointRootName);
        if (waypointRoot != null)
            foreach (Transform waypoint in waypointRoot)
                _waypoints.Add(waypoint.position);

        Transform gateRoot = transform.Find(GateRootName);
        if (gateRoot != null)
            foreach (Transform gate in gateRoot)
            {
                var component = gate.GetComponent<DroneFollowGate>();
                if (component != null)
                    _gates.Add(component);
            }
    }

    void OnDrawGizmos()
    {
        IReadOnlyList<Vector3> points = Waypoints;
        Gizmos.color = Color.Lerp(Color.green, Color.red, difficulty / 3f);
        for (int i = 0; i < points.Count; i++)
        {
            Gizmos.DrawLine(points[i] + Vector3.up * 0.2f, points[(i + 1) % points.Count] + Vector3.up * 0.2f);
            Gizmos.DrawSphere(points[i] + Vector3.up * 0.2f, i == 0 ? 0.6f : 0.3f);
        }
    }
}
