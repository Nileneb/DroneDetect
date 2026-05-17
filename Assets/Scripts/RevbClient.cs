using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Reverb / Pusher-protocol WebSocket client for shepherd multiplayer.
///
/// Dual-mode:
///   - WebGL: thin wrapper around a JS bridge (Plugins/RevbClient.jslib provides RevbConnect etc.)
///   - Standalone (Linux/Win/Mac/Editor): native managed-C# implementation using
///     System.Net.WebSockets.ClientWebSocket and HTTP POST for outgoing events.
///
/// Both modes share the same public API: Connect, Send, SendThrottled, OnEvent.
/// </summary>
public class RevbClient : MonoBehaviour
{
    public static RevbClient Instance { get; private set; }

    [Header("Connection (production defaults; Inspector-overridable)")]
    [Tooltip("Base URL of app.linn.games — used for both ws/wss and HTTPS API. No trailing slash.")]
    public string apiBase = "https://app.linn.games";
    [Tooltip("Pusher-protocol app key. Matches Laravel REVERB_APP_KEY.")]
    public string appKey = "linn-games-key";

    /// <summary>Fires on every received broadcast: (eventName, dataJson).</summary>
    public event Action<string, string> OnEvent;

    string _sessionCode;
    string _jwt;
    float _lastSendTime;
    const float SendInterval = 0.1f;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // ─────────────────────────────────────────────────────────────────────
    // WebGL: thin JS-bridge wrappers
    // ─────────────────────────────────────────────────────────────────────
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void RevbConnect(string url, string appKey, string channel, string token);
    [DllImport("__Internal")] static extern void RevbSubscribe(string eventName, Action<string> callback);
    [DllImport("__Internal")] static extern void RevbTrigger(string eventName, string data);
    [DllImport("__Internal")] static extern void RevbDisconnect();

    public void Connect(string sessionCode, string jwt)
    {
        _sessionCode = sessionCode; _jwt = jwt;
        var wsUrl = apiBase.Replace("https://", "wss://").Replace("http://", "ws://");
        RevbConnect(wsUrl, appKey, $"shepherd.{sessionCode}", jwt);
        foreach (var name in WatchedEvents) RevbSubscribe(name, OnRawEvent);
    }

    [AOT.MonoPInvokeCallback(typeof(Action<string>))]
    static void OnRawEvent(string json)
    {
        if (Instance == null) return;
        try
        {
            var w = JsonUtility.FromJson<EventWrapper>(json);
            Instance.OnEvent?.Invoke(w.e, w.d);
        }
        catch { Instance.OnEvent?.Invoke("raw", json); }
    }

    public void SendThrottled(string eventName, string json)
    {
        if (Time.unscaledTime - _lastSendTime < SendInterval) return;
        _lastSendTime = Time.unscaledTime;
        RevbTrigger(eventName, json);
    }

    public void Send(string eventName, string json) => RevbTrigger(eventName, json);
    void OnDestroy() => RevbDisconnect();

#else
    // ─────────────────────────────────────────────────────────────────────
    // Standalone / Editor: native ClientWebSocket + HTTP POST
    // ─────────────────────────────────────────────────────────────────────

    ClientWebSocket _socket;
    CancellationTokenSource _cts;
    string _socketId;                               // received from pusher:connection_established
    readonly ConcurrentQueue<(string evt, string data)> _eventQueue = new();
    bool _subscribed;
    bool _reconnecting;
    // Exponential backoff schedule for reconnect attempts. Bounded so we don't
    // hammer the server forever — final failure surfaces via OnEvent("connection.lost").
    static readonly int[] ReconnectDelaysMs = { 1000, 2000, 4000, 8000, 15000 };

    public void Connect(string sessionCode, string jwt)
    {
        Debug.Log($"[RevbClient] Connect called — session={sessionCode}, jwt-len={jwt?.Length}, apiBase={apiBase}, appKey={appKey}");
        _sessionCode = sessionCode; _jwt = jwt;
        _cts = new CancellationTokenSource();
        _ = ConnectAsync();
    }

    async Task ConnectAsync()
    {
        try
        {
            var wsBase = apiBase.Replace("https://", "wss://").Replace("http://", "ws://");
            // Pusher-protocol endpoint as expected by Laravel Reverb
            var uri = new Uri($"{wsBase}/app/{appKey}?protocol=7&client=unity&version=1.0&flash=false");
            Debug.Log($"[RevbClient] Attempting WebSocket connect to {uri}");

            _socket = new ClientWebSocket();
            await _socket.ConnectAsync(uri, _cts.Token);
            Debug.Log($"[RevbClient] WebSocket connected: {uri}");

            // Receive loop runs in background; messages get enqueued for main-thread dispatch
            _ = ReceiveLoop();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RevbClient] Connect failed: {ex.Message}");
        }
    }

    async Task ReceiveLoop()
    {
        var buf = new byte[8192];
        var ms = new System.IO.MemoryStream();

        try
        {
            while (_socket != null && _socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult res;
                do
                {
                    try { res = await _socket.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[RevbClient] Receive error: {ex.Message}");
                        return;
                    }
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);

                var msg = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                HandleMessage(msg);
            }
        }
        finally
        {
            // Drop without an explicit Disconnect() → try to reconnect with backoff.
            // Don't enqueue an event from the background thread; the next reconnect
            // attempt's pusher:connection_established will re-subscribe.
            if (!_cts.IsCancellationRequested && !_reconnecting)
                _ = ReconnectWithBackoffAsync();
        }
    }

    async Task ReconnectWithBackoffAsync()
    {
        if (_reconnecting) return;
        _reconnecting = true;
        try
        {
            _subscribed = false;
            _socketId = null;
            try { _socket?.Dispose(); } catch { }
            _socket = null;

            for (int i = 0; i < ReconnectDelaysMs.Length; i++)
            {
                if (_cts.IsCancellationRequested) return;
                Debug.Log($"[RevbClient] Reconnect attempt {i + 1}/{ReconnectDelaysMs.Length} in {ReconnectDelaysMs[i]/1000.0:0.0}s");
                try { await Task.Delay(ReconnectDelaysMs[i], _cts.Token); }
                catch (OperationCanceledException) { return; }

                try
                {
                    await ConnectAsync();
                    if (_socket != null && _socket.State == WebSocketState.Open)
                    {
                        Debug.Log("[RevbClient] Reconnected successfully");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[RevbClient] Reconnect attempt {i + 1} failed: {ex.Message}");
                }
            }
            // All attempts exhausted — surface to listeners (main-thread dispatched via Update queue).
            Debug.LogError("[RevbClient] Reconnect failed after exhausting backoff schedule");
            _eventQueue.Enqueue(("connection.lost", "{}"));
        }
        finally
        {
            _reconnecting = false;
        }
    }

    void HandleMessage(string raw)
    {
        try
        {
            // Pusher envelope: { "event": "...", "channel": "...", "data": "..." }
            var env = JsonUtility.FromJson<PusherEnvelope>(raw);
            if (env == null || string.IsNullOrEmpty(env.@event)) return;

            switch (env.@event)
            {
                case "pusher:connection_established":
                {
                    // data is double-encoded JSON: "{\"socket_id\":\"...\"}"
                    var inner = JsonUtility.FromJson<ConnEstablished>(env.data);
                    _socketId = inner?.socket_id;
                    Debug.Log($"[RevbClient] socket_id={_socketId}");
                    _ = AuthAndSubscribe();
                    break;
                }
                case "pusher:ping":
                    _ = SendRaw("{\"event\":\"pusher:pong\",\"data\":\"{}\"}");
                    break;
                case "pusher_internal:subscription_succeeded":
                    _subscribed = true;
                    Debug.Log($"[RevbClient] Subscribed to {env.channel}");
                    break;
                case "pusher:error":
                    Debug.LogError($"[RevbClient] Server error: {env.data}");
                    break;
                default:
                    // Application event — enqueue for main thread
                    _eventQueue.Enqueue((env.@event, env.data ?? string.Empty));
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RevbClient] Parse error: {ex.Message}");
        }
    }

    void Update()
    {
        while (_eventQueue.TryDequeue(out var item))
        {
            try { OnEvent?.Invoke(item.evt, item.data); }
            catch (Exception ex) { Debug.LogError($"[RevbClient] OnEvent handler threw: {ex}"); }
        }
    }

    async Task AuthAndSubscribe()
    {
        var channelName = $"private-shepherd.{_sessionCode}";

        // Laravel broadcasting auth endpoint — returns { "auth": "<key>:<signature>" }
        string authToken = null;
        try
        {
            var form = new WWWForm();
            form.AddField("channel_name", channelName);
            form.AddField("socket_id",    _socketId);

            using var req = UnityWebRequest.Post($"{apiBase}/broadcasting/auth", form);
            req.SetRequestHeader("Authorization", $"Bearer {_jwt}");
            req.SetRequestHeader("Accept", "application/json");
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            if (req.result == UnityWebRequest.Result.Success)
            {
                var authResp = JsonUtility.FromJson<AuthResponse>(req.downloadHandler.text);
                authToken = authResp?.auth;
            }
            else
            {
                Debug.LogError($"[RevbClient] Broadcasting auth failed ({req.responseCode}): {req.error} — {req.downloadHandler.text}");
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RevbClient] Auth exception: {ex.Message}");
            return;
        }

        // Pusher subscribe message — note: nested `data` is an object, not stringified
        var subscribe = $"{{\"event\":\"pusher:subscribe\",\"data\":{{\"channel\":\"{channelName}\",\"auth\":\"{authToken}\"}}}}";
        await SendRaw(subscribe);
    }

    async Task SendRaw(string json)
    {
        if (_socket == null || _socket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
    }

    public void SendThrottled(string eventName, string json)
    {
        if (Time.unscaledTime - _lastSendTime < SendInterval) return;
        _lastSendTime = Time.unscaledTime;
        Send(eventName, json);
    }

    public void Send(string eventName, string dataJson)
    {
        // Outgoing path: HTTP POST to /relay; server broadcasts back via WebSocket.
        // This is safer than Pusher client-events (which require special enabling +
        // bypass server-side validation).
        StartCoroutine(PostRelay(eventName, dataJson));
    }

    System.Collections.IEnumerator PostRelay(string eventName, string dataJson)
    {
        if (!_subscribed) yield break;  // drop pre-subscription events silently

        // Hand-craft JSON: avoid double-stringify; data is an arbitrary object the client sent
        var body = $"{{\"event\":\"{eventName}\",\"data\":{dataJson}}}";
        using var req = new UnityWebRequest($"{apiBase}/api/shepherd/sessions/{_sessionCode}/relay", "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type",  "application/json");
        req.SetRequestHeader("Authorization", $"Bearer {_jwt}");
        req.SetRequestHeader("Accept",        "application/json");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
            Debug.LogWarning($"[RevbClient] Relay POST {eventName} failed ({req.responseCode}): {req.error}");
    }

    void OnDestroy()
    {
        _cts?.Cancel();
        if (_socket != null && _socket.State == WebSocketState.Open)
        {
            try { _ = _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "scene closing", CancellationToken.None); }
            catch { /* ignore */ }
        }
        _socket?.Dispose();
    }

    [Serializable] class PusherEnvelope { public string @event; public string channel; public string data; }
    [Serializable] class ConnEstablished { public string socket_id; public int activity_timeout; }
    [Serializable] class AuthResponse    { public string auth; }
#endif

    static readonly string[] WatchedEvents = {
        "wolf.moved", "drone.moved", "sheep.state", "sheep.caught",
        "scarer.activated", "wolf.panic", "wolf.joined", "drone.joined",
        "game.started", "game.ended",
        // Matchmaking — fired by ShepherdMatchFound (broadcastAs "MatchFound")
        "MatchFound",
    };

    [Serializable] class EventWrapper { public string e; public string d; }
}
