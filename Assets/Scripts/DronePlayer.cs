using System.Collections.Generic;
using UnityEngine;

// Handles networking + takeoff/land only.
// Movement + scarer are driven by ShepherdDroneAgent (heuristic or AI).
[RequireComponent(typeof(SimulatedDroneController))]
[RequireComponent(typeof(ShepherdDroneAgent))]
public class DronePlayer : MonoBehaviour
{
    [Header("Components")]
    public DroneScarer scarer;

    public int PlayerId { get; set; }
    public bool IsLocal { get; set; }

    SimulatedDroneController _sdc;
    readonly Dictionary<int, Transform> _remoteDrones = new();
    ShepherdGameManager _gm;

    void Awake()
    {
        _sdc = GetComponent<SimulatedDroneController>();
    }

    void Start()
    {
        _gm = FindFirstObjectByType<ShepherdGameManager>();
        if (IsLocal)
        {
            _sdc.Takeoff();
            if (RevbClient.Instance != null)
                RevbClient.Instance.OnEvent += HandleNetEvent;
        }
    }

    void Update()
    {
        if (!IsLocal) return;

        // Takeoff / Land only — movement is handled by ShepherdDroneAgent
        if (_sdc.State == DroneState.Landed && Input.GetKeyDown(KeyCode.T))
            _sdc.Takeoff();
        else if ((_sdc.State == DroneState.Hovering || _sdc.State == DroneState.Flying) && Input.GetKeyDown(KeyCode.G))
            _sdc.Land();

        SendPositionThrottled();
    }

    void SendPositionThrottled()
    {
        if (RevbClient.Instance == null) return;
        var p = transform.position;
        int scarerState = (scarer != null && scarer.IsActive) ? 1 : 0;
        var json = $"{{\"id\":{PlayerId},\"x\":{p.x:F2},\"y\":{p.y:F2},\"z\":{p.z:F2},\"r\":{transform.eulerAngles.y:F1},\"s\":{scarerState}}}";
        RevbClient.Instance.SendThrottled("drone.moved", json);
    }

    void HandleNetEvent(string eventName, string json)
    {
        if (eventName != "drone.moved") return;
        var d = JsonUtility.FromJson<DroneMoveData>(json);
        if (d.id == PlayerId) return;

        if (!_remoteDrones.TryGetValue(d.id, out var t))
        {
            var go = _gm != null
                ? Instantiate(_gm.remoteDronePrefab)
                : new GameObject($"RemoteDrone_{d.id}");
            t = go.transform;
            _remoteDrones[d.id] = t;
        }

        t.position = Vector3.Lerp(t.position, new Vector3(d.x, d.y, d.z), 0.3f);
        t.rotation = Quaternion.Lerp(t.rotation, Quaternion.Euler(0, d.r, 0), 0.3f);
    }

    void OnDestroy()
    {
        if (RevbClient.Instance != null)
            RevbClient.Instance.OnEvent -= HandleNetEvent;
    }

    [System.Serializable]
    struct DroneMoveData { public int id; public float x, y, z, r; public int s; }
}
