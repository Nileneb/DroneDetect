using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class ShepherdGameManager : MonoBehaviour
{
    public static bool IsHost { get; private set; }

    [Header("Scene References")]
    public Transform[] wolfSpawnPoints;
    public Transform droneSpawnPoint;
    public SheepNPC[] sheep;
    public GameObject remoteWolfPrefab;
    public GameObject remoteDronePrefab;
    public WolfPlayer localWolfPrefab;
    public DronePlayer localDronePrefab;

    [Header("HUD")]
    public Text timerText;
    public Text scoreText;
    public GameObject endScreen;
    public Text endResultText;

    [Header("Game Settings")]
    public float roundDuration = 180f;
    public string apiBase = "https://app.linn.games/api/shepherd";

#if UNITY_EDITOR
    [Header("Editor Only")]
    public string editorRole = "wolf";
#endif

    public List<Transform> ActiveSheep { get; } = new();
    public float ElapsedTime => _elapsed;
    public int SheepCaught => _sheepCaught;

    string _sessionCode;
    string _jwt;
    int _localPlayerId;
    string _localRole;

    int _sheepCaught;
    int _sheepSaved;
    float _elapsed;
    bool _running;

    readonly List<EventBatch> _pendingEvents = new();
    float _batchTimer;
    int _tickCounter;

    static ShepherdGameManager _instance;

    void Awake()
    {
        _instance = this;
        if (endScreen) endScreen.SetActive(false);
    }

    void Start()
    {
        foreach (var s in sheep) ActiveSheep.Add(s.transform);

        // Read token + session from URL params via JS interop
        _sessionCode = GetUrlParam("session");
        _jwt = GetUrlParam("token");
        _localRole = GetUrlParam("role");
        bool isHostParam = GetUrlParam("host") == "1";
        IsHost = isHostParam;

        if (string.IsNullOrEmpty(_sessionCode) || string.IsNullOrEmpty(_jwt))
        {
#if UNITY_EDITOR
            _sessionCode = "EDITOR";
            _jwt = "dev-token";
            _localRole = editorRole;
            IsHost = true;
#else
            Debug.LogError("[ShepherdGM] Missing session/token URL params");
            return;
#endif
        }

        _localPlayerId = int.TryParse(GetUrlParam("uid"), out var uid) ? uid : UnityEngine.Random.Range(1000, 9999);

        for (int i = 0; i < sheep.Length; i++)
        {
            sheep[i].SheepId = i;
            sheep[i].gameObject.SetActive(true);
        }

        if (RevbClient.Instance != null)
        {
            RevbClient.Instance.OnEvent += HandleEvent;
            RevbClient.Instance.Connect(_sessionCode, _jwt);
        }

        if (_localRole == "wolf")
            SpawnLocalWolf();
        else
            SpawnLocalDrone();

        if (IsHost)
            StartCoroutine(WaitThenStartRound());
    }

    IEnumerator WaitThenStartRound()
    {
        yield return new WaitForSeconds(2f);
        StartRound();
    }

    public void StartRound()
    {
        _running = true;
        StartCoroutine(RoundTimer());
        StartCoroutine(BatchUpload());
        if (RevbClient.Instance != null && IsHost)
        {
            StartCoroutine(PostJson($"{apiBase}/sessions/{_sessionCode}/start", "{}"));
        }
    }

    IEnumerator RoundTimer()
    {
        _elapsed = 0f;
        int lastRecordedTick = -1;
        while (_elapsed < roundDuration && _running)
        {
            _elapsed += Time.deltaTime;
            _tickCounter = Mathf.FloorToInt(_elapsed * 20f);
            UpdateHUD();
            // Record position at 4Hz (every 5 ticks of 20Hz)
            if (_tickCounter != lastRecordedTick && _tickCounter % 5 == 0)
            {
                lastRecordedTick = _tickCounter;
                RecordEvent("move");
            }
            yield return null;
        }
        if (_running) EndRound();
    }

    void UpdateHUD()
    {
        float remaining = Mathf.Max(0, roundDuration - _elapsed);
        if (timerText) timerText.text = $"{Mathf.FloorToInt(remaining / 60):00}:{Mathf.FloorToInt(remaining % 60):00}";
        if (scoreText) scoreText.text = $"Caught: {_sheepCaught}  Safe: {ActiveSheep.Count}";
    }

    public void OnSheepCaught(SheepNPC s)
    {
        if (s == null || s.IsCaught) return;
        ActiveSheep.Remove(s.transform);
        s.OnCaught();
        _sheepCaught++;

        RecordEvent("catch_sheep");

        if (ActiveSheep.Count == 0) EndRound();
    }

    public void RecordEvent(string action)
    {
        var go = _localRole == "wolf"
            ? (Component)FindFirstObjectByType<WolfPlayer>()
            : FindFirstObjectByType<DronePlayer>();
        if (go == null) return;

        var p = go.transform.position;
        var r = go.transform.eulerAngles.y;

        _pendingEvents.Add(new EventBatch
        {
            at_tick = _tickCounter,
            role    = _localRole,
            pos_x   = p.x,
            pos_y   = p.y,
            pos_z   = p.z,
            rot_y   = r,
            action  = action
        });
    }

    IEnumerator BatchUpload()
    {
        while (_running)
        {
            yield return new WaitForSeconds(2f);
            if (_pendingEvents.Count == 0) continue;

            var batch = new List<EventBatch>(_pendingEvents);
            _pendingEvents.Clear();

            var wrapper = new EventBatchWrapper { events = batch };
            var json = JsonUtility.ToJson(wrapper);
            yield return PostJson($"{apiBase}/sessions/{_sessionCode}/events", json);
        }
    }

    void EndRound()
    {
        _running = false;
        _sheepSaved = ActiveSheep.Count;

        // Legacy direct-UI fallback (usually null when ShepherdHUD is active)
        if (endScreen) endScreen.SetActive(true);
        if (endResultText)
            endResultText.text = $"Sheep Saved: {_sheepSaved}\nSheep Caught: {_sheepCaught}";

        FindFirstObjectByType<ShepherdHUD>()?.ShowEndScreen(_sheepSaved, _sheepCaught, _elapsed);

        StartCoroutine(PostEnd());
    }

    IEnumerator PostEnd()
    {
        // Flush remaining events
        if (_pendingEvents.Count > 0)
        {
            var wrapper = new EventBatchWrapper { events = new List<EventBatch>(_pendingEvents) };
            _pendingEvents.Clear();
            yield return PostJson($"{apiBase}/sessions/{_sessionCode}/events", JsonUtility.ToJson(wrapper));
        }

        var endJson = $"{{\"sheep_saved\":{_sheepSaved},\"sheep_caught\":{_sheepCaught},\"duration_seconds\":{(int)_elapsed}}}";
        yield return PostJson($"{apiBase}/sessions/{_sessionCode}/end", endJson);
    }

    void HandleEvent(string eventName, string json)
    {
        if (eventName == "game.started" && !_running) StartRound();
        if (eventName == "game.ended") EndRound();
    }

    void SpawnLocalWolf()
    {
        if (localWolfPrefab == null) return;
        var spawn = wolfSpawnPoints.Length > 0 ? wolfSpawnPoints[0] : transform;
        var wolf = Instantiate(localWolfPrefab, spawn.position, spawn.rotation);
        wolf.PlayerId = _localPlayerId;
        wolf.IsLocal = true;
    }

    void SpawnLocalDrone()
    {
        if (localDronePrefab == null) return;
        var spawn = droneSpawnPoint != null ? droneSpawnPoint : transform;
        var drone = Instantiate(localDronePrefab, spawn.position + Vector3.up * 3f, spawn.rotation);
        drone.PlayerId = _localPlayerId;
        drone.IsLocal = true;
        if (drone.scarer != null)
            drone.scarer.SetPlayerId(_localPlayerId);
    }

    IEnumerator PostJson(string url, string json)
    {
        var req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Authorization", $"Bearer {_jwt}");
        req.SetRequestHeader("Accept", "application/json");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
            Debug.LogWarning($"[ShepherdGM] POST {url} failed: {req.error}");
    }

    string GetUrlParam(string key)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return GetUrlParameter(key);
#else
        return "";
#endif
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")]
    static extern string GetUrlParameter(string key);
#endif

    void OnDestroy()
    {
        if (RevbClient.Instance != null)
            RevbClient.Instance.OnEvent -= HandleEvent;
    }

    [Serializable]
    class EventBatch
    {
        public int at_tick;
        public string role;
        public float pos_x, pos_y, pos_z, rot_y;
        public string action;
    }

    [Serializable]
    class EventBatchWrapper { public List<EventBatch> events; }
}
