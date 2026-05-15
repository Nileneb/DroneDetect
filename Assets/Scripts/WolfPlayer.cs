using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class WolfPlayer : MonoBehaviour
{
    [Header("Movement")]
    public float speed = 5f;
    public float turnSpeed = 180f;

    [Header("Catch")]
    public float catchRadius = 1.2f;

    public int PlayerId { get; set; }
    public bool IsLocal { get; set; }

    CharacterController _cc;
    WolfFear _fear;
    readonly Dictionary<int, Transform> _remoteWolves = new();
    ShepherdGameManager _gm;

    void Awake() => _cc = GetComponent<CharacterController>();

    void Start()
    {
        _gm = FindFirstObjectByType<ShepherdGameManager>();
        _fear = GetComponent<WolfFear>();
        if (IsLocal && RevbClient.Instance != null)
            RevbClient.Instance.OnEvent += HandleNetEvent;
    }

    void Update()
    {
        if (!IsLocal) return;
        if (_fear != null && _fear.IsPanicking) { SendPositionThrottled(); return; }

        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");

        transform.Rotate(Vector3.up, h * turnSpeed * Time.deltaTime);
        _cc.SimpleMove(transform.forward * v * speed);

        SendPositionThrottled();
        TryCaptureSheep();
    }

    void SendPositionThrottled()
    {
        if (RevbClient.Instance == null) return;
        var p = transform.position;
        var json = $"{{\"id\":{PlayerId},\"x\":{p.x:F2},\"y\":{p.y:F2},\"z\":{p.z:F2},\"r\":{transform.eulerAngles.y:F1}}}";
        RevbClient.Instance.SendThrottled("wolf.moved", json);
    }

    void TryCaptureSheep()
    {
        if (_gm == null) return;
        foreach (var sheep in _gm.ActiveSheep)
        {
            if (sheep == null) continue;
            if (Vector3.Distance(transform.position, sheep.position) < catchRadius)
            {
                _gm.OnSheepCaught(sheep.GetComponent<SheepNPC>());
                break;
            }
        }
    }

    void HandleNetEvent(string eventName, string json)
    {
        if (eventName != "wolf.moved") return;
        var d = JsonUtility.FromJson<WolfMoveData>(json);
        if (d.id == PlayerId) return;

        if (!_remoteWolves.TryGetValue(d.id, out var t))
        {
            var go = _gm != null
                ? Instantiate(_gm.remoteWolfPrefab)
                : new GameObject($"RemoteWolf_{d.id}");
            t = go.transform;
            _remoteWolves[d.id] = t;
        }

        t.position = Vector3.Lerp(t.position, new Vector3(d.x, d.y, d.z), 0.3f);
        t.rotation = Quaternion.Lerp(t.rotation, Quaternion.Euler(0, d.r, 0), 0.3f);
    }

    public void PushAway(Vector3 from, float force)
    {
        if (!IsLocal) return;
        var dir = (transform.position - from).normalized;
        _cc.Move(dir * force * Time.deltaTime * 20f);
    }

    void OnDestroy()
    {
        if (RevbClient.Instance != null)
            RevbClient.Instance.OnEvent -= HandleNetEvent;
    }

    [System.Serializable]
    struct WolfMoveData { public int id; public float x, y, z, r; }
}
