using System;
using System.Runtime.InteropServices;
using UnityEngine;

public class RevbClient : MonoBehaviour
{
    public static RevbClient Instance { get; private set; }

    [Header("Connection")]
    public string reverbUrl = "wss://app.linn.games";  // Lokal: ws://192.168.178.11:8080
    public string appKey = "linn-games-key";

    public event Action<string, string> OnEvent;

    string _sessionCode;
    string _jwt;
    float _lastSendTime;
    const float SendInterval = 0.1f;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] static extern void RevbConnect(string url, string appKey, string channel, string token);
    [DllImport("__Internal")] static extern void RevbSubscribe(string eventName, Action<string> callback);
    [DllImport("__Internal")] static extern void RevbTrigger(string eventName, string data);
    [DllImport("__Internal")] static extern void RevbDisconnect();
#else
    static void RevbConnect(string u, string k, string c, string t) => Debug.Log($"[RevbClient] Connect(Editor) channel=shepherd.{c}");
    static void RevbSubscribe(string e, Action<string> cb) { }
    static void RevbTrigger(string e, string d) => Debug.Log($"[RevbClient] Trigger {e}: {d}");
    static void RevbDisconnect() { }
#endif

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public void Connect(string sessionCode, string jwt)
    {
        _sessionCode = sessionCode;
        _jwt = jwt;
        RevbConnect(reverbUrl, appKey, $"shepherd.{sessionCode}", jwt);
        RevbSubscribe("wolf.moved", OnRawEvent);
        RevbSubscribe("drone.moved", OnRawEvent);
        RevbSubscribe("sheep.state", OnRawEvent);
        RevbSubscribe("scarer.activated", OnRawEvent);
        RevbSubscribe("game.started", OnRawEvent);
        RevbSubscribe("game.ended", OnRawEvent);
    }

    [AOT.MonoPInvokeCallback(typeof(Action<string>))]
    static void OnRawEvent(string json)
    {
        if (Instance == null) return;
        try
        {
            var wrapper = JsonUtility.FromJson<EventWrapper>(json);
            Instance.OnEvent?.Invoke(wrapper.e, wrapper.d);
        }
        catch
        {
            Instance.OnEvent?.Invoke("raw", json);
        }
    }

    public void SendThrottled(string eventName, string json)
    {
        if (Time.unscaledTime - _lastSendTime < SendInterval) return;
        _lastSendTime = Time.unscaledTime;
        RevbTrigger(eventName, json);
    }

    public void Send(string eventName, string json) => RevbTrigger(eventName, json);

    void OnDestroy() => RevbDisconnect();

    [Serializable] class EventWrapper { public string e; public string d; }
}
