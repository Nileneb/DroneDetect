using System.Collections.Generic;
using UnityEngine;

public class DronePlayer : MonoBehaviour
{
    [Header("Movement")]
    public float horizontalSpeed = 6f;
    public float verticalSpeed = 4f;
    public float rotSpeed = 90f;

    [Header("Components")]
    public DroneScarer scarer;

    public int PlayerId { get; set; }
    public bool IsLocal { get; set; }

    Rigidbody _rb;
    readonly Dictionary<int, Transform> _remoteDrones = new();
    ShepherdGameManager _gm;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        if (_rb) _rb.useGravity = false;
    }

    void Start()
    {
        _gm = FindObjectOfType<ShepherdGameManager>();
        if (IsLocal && RevbClient.Instance != null)
            RevbClient.Instance.OnEvent += HandleNetEvent;
    }

    void Update()
    {
        if (!IsLocal) return;

        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");
        float up = Input.GetKey(KeyCode.E) ? 1f : Input.GetKey(KeyCode.Q) ? -1f : 0f;
        float rot = 0f;

        if (_rb)
        {
            var move = (transform.forward * v + transform.right * h) * horizontalSpeed
                       + Vector3.up * up * verticalSpeed;
            _rb.linearVelocity = Vector3.Lerp(_rb.linearVelocity, move, 0.2f);
            transform.Rotate(Vector3.up, rot * rotSpeed * Time.deltaTime);
        }

        if (Input.GetKeyDown(KeyCode.F) && scarer != null)
            scarer.Activate();

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
