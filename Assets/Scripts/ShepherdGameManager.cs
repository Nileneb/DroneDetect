using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Unity.MLAgents.Policies;
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
    public float roundDuration = 300f;
    public string apiBase = "https://app.linn.games/api/shepherd";

    [Header("Phase-4 Reward Tuning")]
    [Tooltip("Bonus pro überlebendes Schaf am Episodenende.")]
    public float sheepSavedBonus = 5f;
    [Tooltip("Reward-Multiplikator wenn alle Schafe überleben (perfect-defense).")]
    public float perfectDefenseMultiplier = 1.5f;
    [Tooltip("Strafe pro getötetem Schaf (sofort, beim Catch).")]
    public float sheepCaughtPenalty = 3f;

#if UNITY_EDITOR
    [Header("Editor Only")]
    public string editorRole = "wolf";
#endif

    public List<Transform> ActiveSheep { get; } = new();
    public float ElapsedTime => _elapsed;
    public int SheepCaught => _sheepCaught;
    public int SheepSaved => _sheepSaved;
    public int InitialSheepCount { get; private set; }
    public float RoundDuration => roundDuration;
    public bool IsRoundActive => _running;

    public System.Action<int, int, float> OnRoundEnded;
    public System.Action<SheepNPC> OnSheepCaughtEvent;

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

    string _aiRole;  // if set, the opposite seat is an AI bot — spawn locally

    void Start()
    {
        Debug.Log("[ShepherdGM] Start() invoked");
        foreach (var s in sheep) ActiveSheep.Add(s.transform);
        InitialSheepCount = sheep != null ? sheep.Length : 0;

        // Read session config: PlayerPrefs (set by MatchmakeManager) wins, fall back to URL/CLI
        _sessionCode = PlayerPrefs.GetString("shepherd_session", GetUrlParam("session"));
        _jwt         = PlayerPrefs.GetString("shepherd_token",   GetUrlParam("token"));
        _localRole   = PlayerPrefs.GetString("shepherd_role",    GetUrlParam("role"));
        _aiRole      = PlayerPrefs.GetString("shepherd_ai_role", "");
        bool isHostParam = PlayerPrefs.HasKey("shepherd_host")
                           ? PlayerPrefs.GetInt("shepherd_host") == 1
                           : GetUrlParam("host") == "1";
        IsHost = isHostParam;
        Debug.Log($"[ShepherdGM] params session={_sessionCode}, jwt-len={_jwt?.Length}, role={_localRole}, host={IsHost}");

        if (string.IsNullOrEmpty(_sessionCode))
        {
#if UNITY_EDITOR
            _sessionCode = "EDITOR";
            _jwt = "dev-token";
            _localRole = editorRole;
            IsHost = true;
#else
            // Offline fallback: no session means "single-player vs AI" — JWT optional
            _sessionCode = "OFFLINE";
            _jwt = "";
            if (string.IsNullOrEmpty(_localRole)) _localRole = "drone";
            if (string.IsNullOrEmpty(_aiRole))    _aiRole    = _localRole == "wolf" ? "drone" : "wolf";
            IsHost = true;
            Debug.Log("[ShepherdGM] No session — offline AI mode, role=" + _localRole + ", ai_role=" + _aiRole);
#endif
        }

        _localPlayerId = int.TryParse(GetUrlParam("uid"), out var uid) ? uid : UnityEngine.Random.Range(1000, 9999);

        // CLI/URL override: -api=http://localhost:8000 — points client at local dev server
        var apiOverride = GetUrlParam("api");
        if (!string.IsNullOrEmpty(apiOverride))
        {
            apiBase = apiOverride.TrimEnd('/') + "/api/shepherd";
            if (RevbClient.Instance != null)
                RevbClient.Instance.apiBase = apiOverride.TrimEnd('/');
        }

        for (int i = 0; i < sheep.Length; i++)
        {
            sheep[i].SheepId = i;
            sheep[i].gameObject.SetActive(true);
        }

        Debug.Log($"[ShepherdGM] RevbClient.Instance={(RevbClient.Instance != null ? "OK" : "NULL")}");
        if (RevbClient.Instance != null && !IsOfflineMode())
        {
            RevbClient.Instance.OnEvent += HandleEvent;
            RevbClient.Instance.Connect(_sessionCode, _jwt);
        }
        else if (IsOfflineMode())
        {
            Debug.Log("[ShepherdGM] Offline mode — skipping Pusher Connect");
        }
        else
        {
            Debug.LogError("[ShepherdGM] RevbClient.Instance is null — multiplayer disabled. No NetworkManager GameObject in scene?");
        }

        // WHY: the scene has a pre-placed Wolf GameObject (left over from Agent1 training mode)
        // with localPlayerOverride=true. DroneControlPanel.BootstrapSceneMode activates it when
        // shepherdMode=true, and its WolfPlayer.Start() then sets IsLocal=true — which makes
        // LocalPlayerCamera follow IT, not the spawned drone. Strip the relic before spawning.
        StripScenePreplacedActors();

        if (_localRole == "wolf")
            SpawnLocalWolf();
        else
            SpawnLocalDrone();

        // AI fallback: spawn the OPPOSITE side as a bot if matchmaking told us to
        if (!string.IsNullOrEmpty(_aiRole))
        {
            Debug.Log("[ShepherdGM] Spawning AI bot for role: " + _aiRole);
            SpawnAiBot(_aiRole);
        }

        // WHY: sheep prefab carries a NavMeshAgent from earlier wave-mode design. Even when
        // disabled in Awake, the native NavMeshAgent occasionally drags transforms toward
        // its default destination (0,0,0). Hard-destroy it + reseed positions if collapsed.
        RepairSheep();

        if (IsHost)
            StartCoroutine(WaitThenStartRound());
    }

    void SpawnAiBot(string role)
    {
        GameObject botGo = null;
        if (role == "wolf")
        {
            if (localWolfPrefab != null)
            {
                var bot = Instantiate(localWolfPrefab,
                    wolfSpawnPoints != null && wolfSpawnPoints.Length > 0 ? wolfSpawnPoints[0].position : Vector3.zero,
                    Quaternion.identity);
                bot.IsLocal = false;     // not human-controlled
                bot.localPlayerOverride = false;
                bot.enabled = false;     // disable input + send loops
                var fear = bot.GetComponent<WolfFear>();
                if (fear != null) fear.enabled = false;
                botGo = bot.gameObject;
                botGo.name = "AI_Wolf";
            }
        }
        else if (role == "drone")
        {
            if (localDronePrefab != null)
            {
                var bot = Instantiate(localDronePrefab,
                    droneSpawnPoint != null ? droneSpawnPoint.position + Vector3.up * 3f : Vector3.up * 5f,
                    Quaternion.identity);
                bot.IsLocal = false;
                bot.enabled = false;
                // WHY: SimpleAIBot drives the bot drone via NavMesh logic. The ML-Agents
                // pipeline would fight it. Disable both the agent + sim controller.
                var agent = bot.GetComponent<ShepherdDroneAgent>();
                if (agent != null) agent.enabled = false;
                var sdc = bot.GetComponent<SimulatedDroneController>();
                if (sdc != null) sdc.enabled = false;
                var bpBot = bot.GetComponent<BehaviorParameters>();
                if (bpBot != null) bpBot.BehaviorType = BehaviorType.InferenceOnly;
                botGo = bot.gameObject;
                botGo.name = "AI_Drone";
            }
        }

        if (botGo == null)
        {
            Debug.LogError("[ShepherdGM] AI bot spawn failed — local prefab for " + role + " missing");
            return;
        }

        // WHY: AI-bot prefab carries cameras (drone has GroundCam/DepthCam/MainCam, wolf may
        // have a follow-cam). Multiple enabled cameras fight LocalPlayerCamera for the render
        // target. Disable any camera in the bot hierarchy so only LocalPlayerCamera renders.
        foreach (var cam in botGo.GetComponentsInChildren<Camera>(true))
            cam.enabled = false;
        foreach (var listener in botGo.GetComponentsInChildren<AudioListener>(true))
            listener.enabled = false;

        var ai = botGo.AddComponent<SimpleAIBot>();
        ai.role = role == "wolf" ? SimpleAIBot.Role.Wolf : SimpleAIBot.Role.Drone;
        if (role == "drone")
        {
            // Wire scarer reference for the drone-bot
            var droneBot = botGo.GetComponent<DronePlayer>();
            if (droneBot != null) ai.scarer = droneBot.scarer;
        }
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
#if !UNITY_EDITOR
        // Skip server-side round-start in offline mode (no JWT, no session on server → 401 spam)
        if (RevbClient.Instance != null && IsHost && !IsOfflineMode())
            StartCoroutine(PostJson($"{apiBase}/sessions/{_sessionCode}/start", "{}"));
#endif
    }

    bool IsOfflineMode() => string.IsNullOrEmpty(_jwt) || _sessionCode == "OFFLINE";

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
        OnSheepCaughtEvent?.Invoke(s);

        // Real-time broadcast so the drone-player sees the catch immediately (host-authoritative)
        if (RevbClient.Instance != null && IsHost && !IsOfflineMode())
        {
            var json = $"{{\"sheep_id\":{s.SheepId},\"caught\":{_sheepCaught},\"remaining\":{ActiveSheep.Count}}}";
            RevbClient.Instance.Send("sheep.caught", json);
        }

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
            if (IsOfflineMode()) { _pendingEvents.Clear(); continue; }
#if UNITY_EDITOR
            _pendingEvents.Clear();
            continue;
#endif
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

        OnRoundEnded?.Invoke(_sheepSaved, _sheepCaught, _elapsed);

        StartCoroutine(PostEnd());
    }

    IEnumerator PostEnd()
    {
        if (IsOfflineMode()) { _pendingEvents.Clear(); yield break; }
#if !UNITY_EDITOR
        if (_pendingEvents.Count > 0)
        {
            var wrapper = new EventBatchWrapper { events = new List<EventBatch>(_pendingEvents) };
            _pendingEvents.Clear();
            yield return PostJson($"{apiBase}/sessions/{_sessionCode}/events", JsonUtility.ToJson(wrapper));
        }

        var endJson = $"{{\"sheep_saved\":{_sheepSaved},\"sheep_caught\":{_sheepCaught},\"duration_seconds\":{(int)_elapsed}}}";
        yield return PostJson($"{apiBase}/sessions/{_sessionCode}/end", endJson);
#else
        _pendingEvents.Clear();
        yield break;
#endif
    }

    void HandleEvent(string eventName, string json)
    {
        switch (eventName)
        {
            case "game.started":
                if (!_running) StartRound();
                break;
            case "game.ended":
                EndRound();
                break;
            case "sheep.caught":
                // Non-host sees a sheep caught — drop it from local ActiveSheep + visuals
                if (IsHost) break;
                var sc = JsonUtility.FromJson<SheepCaughtData>(json);
                _sheepCaught = sc.caught;
                if (sheep != null && sc.sheep_id >= 0 && sc.sheep_id < sheep.Length)
                {
                    var s = sheep[sc.sheep_id];
                    if (s != null && !s.IsCaught)
                    {
                        ActiveSheep.Remove(s.transform);
                        s.OnCaught();
                        OnSheepCaughtEvent?.Invoke(s);
                    }
                }
                break;
        }
    }

    [System.Serializable]
    class SheepCaughtData { public int sheep_id; public int caught; public int remaining; }

    // WHY: ShepherdArena scene was built for Agent1 training and contains a stale Wolf
    // GameObject with localPlayerOverride=true. Without stripping it, LocalPlayerCamera
    // follows the wolf even when the user picked drone.
    void StripScenePreplacedActors()
    {
        foreach (var wolf in FindObjectsByType<WolfPlayer>(FindObjectsSortMode.None))
        {
            if (wolf == null) continue;
            wolf.localPlayerOverride = false;
            wolf.IsLocal = false;
            wolf.gameObject.SetActive(false);
        }
        foreach (var dp in FindObjectsByType<DronePlayer>(FindObjectsSortMode.None))
        {
            if (dp == null) continue;
            dp.IsLocal = false;
            dp.gameObject.SetActive(false);
        }
    }

    // WHY: scene-sheep are parented under a "Sheep" group GameObject (parent at origin) AND
    // carry stale NavMeshAgent + Animator-with-rootMotion. The parent transform plus root-motion
    // override our transform.position writes — sheep visually snap toward the parent (0,0,0).
    // Fix: unparent (so transform.position writes are absolute world-space), destroy NavMeshAgent,
    // disable Animator-rootMotion, freeze Rigidbody to non-kinematic-but-no-gravity, re-spread
    // clumped positions onto a ring.
    void RepairSheep()
    {
        var all = FindObjectsByType<SheepNPC>(FindObjectsSortMode.None);
        for (int i = 0; i < all.Length; i++)
        {
            var s = all[i];
            if (s == null) continue;

            // Detach from any parent — transform.position writes are world-space but a parent
            // with Animator/Constraint/Script can still override per-frame.
            if (s.transform.parent != null)
                s.transform.SetParent(null, true);

            var nav = s.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (nav != null) Destroy(nav);

            // Kill root-motion so the Animator doesn't yank position back
            foreach (var anim in s.GetComponentsInChildren<Animator>(true))
            {
                anim.applyRootMotion = false;
            }

            // Rigidbody: lock to ground plane, no rotation jitter, no gravity-pull-to-zero
            var rb = s.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.useGravity = false;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.constraints = RigidbodyConstraints.FreezeRotation | RigidbodyConstraints.FreezePositionY;
            }

            s.enabled = true;

            // Spread sheep onto a ring so they aren't bunched at parent's origin
            var ang = (Mathf.PI * 2f) * (i / (float)Mathf.Max(1, all.Length)) + UnityEngine.Random.Range(0f, 0.6f);
            var r   = UnityEngine.Random.Range(10f, 18f);
            var pos = s.transform.position;
            // Always reseed — the parent-snap already moved them home
            s.transform.position = new Vector3(Mathf.Cos(ang) * r, pos.y, Mathf.Sin(ang) * r);
        }
        Debug.Log($"[ShepherdGM] RepairSheep: detached + spread {all.Length} sheep");
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

        // WHY: prefab is configured with BehaviorType.Default + a trained DroneShepherd .onnx
        // model. Default means "use the model if present" → the trained agent flies, not the
        // human. Force HeuristicOnly on the LOCAL drone so Heuristic() reads keyboard input.
        var bp = drone.GetComponent<BehaviorParameters>();
        if (bp != null) bp.BehaviorType = BehaviorType.HeuristicOnly;
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
        // Standalone (Linux/Win/Mac) — parse command-line args.
        // Accepted forms: -key=value, --key=value, -key value, --key value
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            var prefix1 = "-" + key + "=";
            var prefix2 = "--" + key + "=";
            if (a.StartsWith(prefix1)) return a.Substring(prefix1.Length);
            if (a.StartsWith(prefix2)) return a.Substring(prefix2.Length);
            if ((a == "-" + key || a == "--" + key) && i + 1 < args.Length)
                return args[i + 1];
        }
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

// recompile trigger 1778929181
