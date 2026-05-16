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

    [Tooltip("Für Agent1/Solo-Betrieb: immer lokal steuern (kein ShepherdGameManager nötig)")]
    public bool localPlayerOverride = false;

    CharacterController _cc;
    WolfFear _fear;
    readonly Dictionary<int, Transform> _remoteWolves = new();
    ShepherdGameManager _gm;

    void Awake() => _cc = GetComponent<CharacterController>();

    void Start()
    {
        if (localPlayerOverride) IsLocal = true;
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
        switch (eventName)
        {
            case "wolf.moved":
            {
                var d = JsonUtility.FromJson<WolfMoveData>(json);
                if (d.id == PlayerId) return;
                if (!_remoteWolves.TryGetValue(d.id, out var t))
                {
                    GameObject go;
                    if (_gm != null && _gm.remoteWolfPrefab != null)
                        go = Instantiate(_gm.remoteWolfPrefab);
                    else if (_gm != null && _gm.localWolfPrefab != null)
                        // Fallback: use the LOCAL prefab as visual template (strip controller logic)
                        go = Instantiate(_gm.localWolfPrefab.gameObject);
                    else
                        go = new GameObject($"RemoteWolf_{d.id}");

                    go.name = $"RemoteWolf_{d.id}";
                    // Disable any controller scripts on the clone — it's a pure visual ghost
                    var remoteWolf = go.GetComponent<WolfPlayer>();
                    if (remoteWolf != null) { remoteWolf.IsLocal = false; remoteWolf.enabled = false; }
                    var fear = go.GetComponent<WolfFear>();
                    if (fear != null) fear.enabled = false;
                    var cc = go.GetComponent<CharacterController>();
                    if (cc != null) cc.enabled = false;

                    t = go.transform;
                    _remoteWolves[d.id] = t;
                }
                t.position = Vector3.Lerp(t.position, new Vector3(d.x, d.y, d.z), 0.3f);
                t.rotation = Quaternion.Lerp(t.rotation, Quaternion.Euler(0, d.r, 0), 0.3f);
                break;
            }

            case "scarer.activated":
            {
                // Remote drone fired its scarer near our wolf — apply fear + push
                if (!IsLocal) return;
                var s = JsonUtility.FromJson<ScarerEventData>(json);
                if (_fear != null)
                {
                    // Scale fear gain by how close we already were (the drone broadcasts
                    // its radius; if the wolf is beyond it, the pulse missed us)
                    _fear.AddFear(s.duration * 0.4f);   // ramp toward panic over duration
                }
                break;
            }
        }
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

    [System.Serializable]
    struct ScarerEventData { public int id; public float radius; public float duration; }
}
