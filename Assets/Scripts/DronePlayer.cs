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

    [Header("Health")]
    [Tooltip("Hits from the wolf before the drone goes down. -1 = invulnerable.")]
    public int maxHealth = 3;
    [Tooltip("Seconds of i-frames after each hit so one swipe doesn't drain HP.")]
    public float invulnAfterHit = 1.0f;

    public int Health { get; private set; }
    float _lastHitTime = -999f;

    public int PlayerId { get; set; }
    public bool IsLocal { get; set; }

    SimulatedDroneController _sdc;
    readonly Dictionary<int, Transform> _remoteDrones = new();
    ShepherdGameManager _gm;

    void Awake()
    {
        _sdc = GetComponent<SimulatedDroneController>();
        Health = maxHealth;
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

        // Takeoff / Land
        if (_sdc.State == DroneState.Landed && Input.GetKeyDown(KeyCode.T))
            _sdc.Takeoff();
        else if ((_sdc.State == DroneState.Hovering || _sdc.State == DroneState.Flying) && Input.GetKeyDown(KeyCode.G))
            _sdc.Land();

        // WHY: direct input → controller. The ML-Agents Heuristic() pipeline only fires when
        // RequestDecision is called, and the prefab has no DecisionRequester. For human play
        // we bypass ML-Agents entirely and drive SimulatedDroneController.Move() ourselves.
        if (_sdc.State == DroneState.Hovering || _sdc.State == DroneState.Flying)
        {
            float fwd    = Input.GetAxis("Vertical");
            float strafe = Input.GetAxis("Horizontal");
            float vert   = Input.GetKey(KeyCode.Space) ? 1f
                          : Input.GetKey(KeyCode.LeftShift) ? -1f : 0f;
            float yaw    = Input.GetKey(KeyCode.Q) ? -1f
                          : Input.GetKey(KeyCode.E) ?  1f : 0f;
            _sdc.Move(fwd, -strafe, vert, yaw);
        }

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
            GameObject go;
            if (_gm != null && _gm.remoteDronePrefab != null)
                go = Instantiate(_gm.remoteDronePrefab);
            else if (_gm != null && _gm.localDronePrefab != null)
                go = Instantiate(_gm.localDronePrefab.gameObject);
            else
                go = new GameObject($"RemoteDrone_{d.id}");

            go.name = $"RemoteDrone_{d.id}";
            // Strip controller scripts so remote drone is a pure visual ghost
            var remoteDp = go.GetComponent<DronePlayer>();
            if (remoteDp != null) { remoteDp.IsLocal = false; remoteDp.enabled = false; }
            var agent = go.GetComponent<ShepherdDroneAgent>();
            if (agent != null) agent.enabled = false;
            var sdc = go.GetComponent<SimulatedDroneController>();
            if (sdc != null) sdc.enabled = false;
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.detectCollisions = false; }

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

    // ── Damage from wolf collision ────────────────────────────────────────

    void OnCollisionEnter(Collision c) => TryTakeDamageFrom(c.collider);
    void OnTriggerEnter(Collider c)    => TryTakeDamageFrom(c);

    void TryTakeDamageFrom(Collider other)
    {
        if (!IsLocal || other == null) return;
        if (maxHealth < 0) return;                                 // invulnerable
        if (Time.time - _lastHitTime < invulnAfterHit) return;     // i-frames

        var wolf = other.GetComponentInParent<WolfPlayer>();
        if (wolf == null) return;

        _lastHitTime = Time.time;
        Health = Mathf.Max(0, Health - 1);

        var hud = FindFirstObjectByType<DroneHUD>() ?? FindFirstObjectByType<ShepherdHUD>() as MonoBehaviour;
        if (hud != null)
        {
            // Try DroneHUD.ShowMessage first; both HUDs expose it.
            var droneHud = hud as DroneHUD;
            if (droneHud != null) droneHud.ShowMessage($"Treffer! HP {Health}/{maxHealth}", 2f);
        }

        Debug.Log($"[DronePlayer] Hit by wolf — HP={Health}/{maxHealth}");

        if (Health <= 0)
        {
            Debug.Log("[DronePlayer] Drone destroyed — landing");
            if (_sdc != null) _sdc.Land();
            // Round-end signal: tell ShepherdGameManager all sheep effectively lost
            // (the wolf 'won' by taking out the protector). Simpler: just end round.
            var gm = FindFirstObjectByType<ShepherdGameManager>();
            // Defer to GM via reflection-free path: it has no public EndRound,
            // but OnSheepCaught for every active sheep would end the round.
            // Cleaner: just stop player input here; round timer expiry handles end.
            enabled = false;
        }
    }

    [System.Serializable]
    struct DroneMoveData { public int id; public float x, y, z, r; public int s; }
}
