using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Cdm.Authentication.Browser;
using Cdm.Authentication.OAuth2;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

/// <summary>
/// Standalone login UI for the Shepherd client. Drives the OAuth2 + PKCE
/// authorization-code flow against app.linn.games's Laravel Passport
/// installation. Builds itself programmatically so it works in any scene
/// without manual Canvas wiring.
///
/// Flow:
///   1. Check cached token in PlayerPrefs.shepherd_token.
///   2. If present → verify via GET /api/user; on success skip UI, hand to MatchmakeManager.
///   3. If absent / invalid → show "Login mit Browser" + "Offline (KI)" buttons.
///   4. Click → AuthenticationSession opens the system browser to
///      /oauth/authorize, captures the redirect on http://127.0.0.1:{port}/callback,
///      exchanges code for tokens with PKCE.
///   5. Token cached in PlayerPrefs.shepherd_token (same key
///      MatchmakeManager + ShepherdGameManager read).
/// </summary>
public class LoginScreen : MonoBehaviour
{
    public const string PrefToken = "shepherd_token";
    public const string PrefUserEmail = "shepherd_user_email";
    public const string PrefUserName = "shepherd_user_name";

    // Matches the Passport public-client UUID seeded by app.linn.games migration
    // 2026_05_17_register_oauth_client_dronedetect_desktop.php
    const string OAuthClientId = "01931abc-de50-7000-8000-d51adf444444";
    static readonly int[] LoopbackPorts = { 51742, 51743, 51744 };

    [Header("Server")]
    public string apiBase = "https://app.linn.games";

    [Header("Refs (set by Awake if null)")]
    public MatchmakeManager matchmake;

    Canvas _canvas;
    GameObject _panel;
    Text _statusText;
    Button _browserBtn;
    Button _offlineBtn;

    void Awake()
    {
        if (matchmake == null) matchmake = FindFirstObjectByType<MatchmakeManager>();
    }

    IEnumerator Start()
    {
        BuildUI();

        // CLI override — same convention as MatchmakeManager
        var cliApi = GetArg("api");
        if (!string.IsNullOrEmpty(cliApi)) apiBase = cliApi;

        var cached = PlayerPrefs.GetString(PrefToken, "");
        if (!string.IsNullOrEmpty(cached))
        {
            SetStatus("Token vorhanden — verifiziere…");
            yield return VerifyToken(cached);
            yield break;
        }
        ShowPanel();
    }

    IEnumerator VerifyToken(string token)
    {
        // /api/user accepts both Sanctum (legacy) and Passport (new) per
        // app.linn.games's auth:sanctum,api middleware.
        using var req = UnityWebRequest.Get($"{apiBase}/api/user");
        req.SetRequestHeader("Authorization", $"Bearer {token}");
        req.SetRequestHeader("Accept", "application/json");
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            var me = JsonUtility.FromJson<UserPayload>(req.downloadHandler.text);
            PlayerPrefs.SetString(PrefUserEmail, me?.email ?? "");
            PlayerPrefs.SetString(PrefUserName, me?.name ?? "");
            PlayerPrefs.Save();
            ApplyToMatchmake(token);
            HidePanel();
            yield break;
        }

        Debug.Log($"[LoginScreen] cached token rejected ({req.responseCode}) — falling back to login");
        PlayerPrefs.DeleteKey(PrefToken);
        ShowPanel();
    }

    // ── OAuth browser flow ─────────────────────────────────────────────────

    public void OnBrowserLoginClicked()
    {
        if (_browserBtn != null) _browserBtn.interactable = false;
        SetStatus("Browser öffnet sich — bitte einloggen…");
        _ = RunOAuthFlowAsync();
    }

    async Task RunOAuthFlowAsync()
    {
        try
        {
            var port = PickFreeLoopbackPort();
            if (port == 0)
            {
                SetStatus("Kein freier Port (51742-51744 belegt)");
                if (_browserBtn != null) _browserBtn.interactable = true;
                return;
            }

            var auth = new PassportAuth(new AuthorizationCodeFlow.Configuration
            {
                clientId = OAuthClientId,
                clientSecret = null,                // public PKCE client
                redirectUri = $"http://127.0.0.1:{port}/callback",
                scope = "",
            });

            using var session = new AuthenticationSession(auth, new StandaloneBrowser
            {
                closePageResponse =
                    "<html><body style='font-family:system-ui;background:#0a0a0f;color:#e0e0e0;text-align:center;padding-top:4em'>" +
                    "<h2 style='color:#7fd8ff'>ShepherdArena — Login erfolgreich</h2>" +
                    "<p>Du kannst dieses Fenster jetzt schließen und zum Spiel zurückkehren.</p>" +
                    "</body></html>"
            });

            var token = await session.AuthenticateAsync();
            if (string.IsNullOrEmpty(token?.accessToken))
            {
                SetStatus("Login fehlgeschlagen (kein Token)");
                if (_browserBtn != null) _browserBtn.interactable = true;
                return;
            }

            PlayerPrefs.SetString(PrefToken, token.accessToken);
            PlayerPrefs.Save();

            // Fetch user info to display in HUD later
            await FetchUserInfoAsync(token.accessToken);

            // Back on main thread (cdmvision returns there) — apply + hide
            ApplyToMatchmake(token.accessToken);
            SetStatus("Eingeloggt — wähle eine Rolle");
            HidePanel();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LoginScreen] OAuth failed: {ex.Message}");
            SetStatus("Login-Fehler: " + ex.Message);
            if (_browserBtn != null) _browserBtn.interactable = true;
        }
    }

    async Task FetchUserInfoAsync(string token)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var resp = await http.GetAsync($"{apiBase}/api/user");
            if (!resp.IsSuccessStatusCode) return;
            var body = await resp.Content.ReadAsStringAsync();
            var user = JsonUtility.FromJson<UserPayload>(body);
            if (user != null)
            {
                PlayerPrefs.SetString(PrefUserEmail, user.email ?? "");
                PlayerPrefs.SetString(PrefUserName, user.name ?? "");
                PlayerPrefs.Save();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LoginScreen] User-info fetch failed: {ex.Message}");
        }
    }

    static int PickFreeLoopbackPort()
    {
        foreach (var port in LoopbackPorts)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Start(); return port; }
            catch (SocketException) { continue; }
            finally { listener.Stop(); }
        }
        return 0;
    }

    // ── Offline fallback ──────────────────────────────────────────────────

    public void OnOfflineClicked()
    {
        SetStatus("Offline-Modus — Demo gegen KI");
        HidePanel();
        ApplyToMatchmake("");  // empty token → MatchmakeManager's offline branch
    }

    void ApplyToMatchmake(string token)
    {
        if (matchmake == null) return;
        matchmake.jwt = token;
        matchmake.apiBase = apiBase;
    }

    // ── UI build (programmatic, no scene-asset dependency) ─────────────────

    void BuildUI()
    {
        var existing = FindFirstObjectByType<Canvas>();
        if (existing != null)
        {
            _canvas = existing;
        }
        else
        {
            var canvasGo = new GameObject("LoginCanvas");
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            canvasGo.AddComponent<GraphicRaycaster>();
        }

        EnsureEventSystem();

        _panel = new GameObject("LoginPanel");
        _panel.transform.SetParent(_canvas.transform, false);
        var panelRt = _panel.AddComponent<RectTransform>();
        panelRt.anchorMin = panelRt.anchorMax = panelRt.pivot = new Vector2(0.5f, 0.5f);
        panelRt.sizeDelta = new Vector2(460, 260);
        _panel.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.82f);

        BuildLabel("Title",   "Shepherd Login",                              26, new Vector2(0, 95),  FontStyle.Bold);
        BuildLabel("Hint",    "Login mit deinem app.linn.games Account",     14, new Vector2(0, 60),  FontStyle.Normal);
        _statusText = BuildLabel("Status", "",                                14, new Vector2(0, 22),  FontStyle.Italic);

        _browserBtn = BuildButton("BrowserLoginBtn", "Login mit Browser (sicher)",
            new Vector2(0, -25), 380, 50);
        _browserBtn.onClick.AddListener(OnBrowserLoginClicked);

        _offlineBtn = BuildButton("OfflineBtn", "Offline (Demo gegen KI)",
            new Vector2(0, -85), 380, 40);
        var off = _offlineBtn.GetComponent<Image>();
        if (off != null) off.color = new Color(0.4f, 0.4f, 0.4f, 0.9f);
        _offlineBtn.onClick.AddListener(OnOfflineClicked);
    }

    void EnsureEventSystem()
    {
        if (FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<UnityEngine.EventSystems.EventSystem>();
            es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
        }
    }

    Text BuildLabel(string name, string text, int size, Vector2 anchored, FontStyle style)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_panel.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(420, size + 8);
        rt.anchoredPosition = anchored;
        var t = go.AddComponent<Text>();
        t.text = text;
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        return t;
    }

    Button BuildButton(string name, string label, Vector2 anchored, float width, float height)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_panel.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(width, height);
        rt.anchoredPosition = anchored;
        go.AddComponent<Image>().color = new Color(0.18f, 0.45f, 0.78f, 0.95f);
        var btn = go.AddComponent<Button>();

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        var textRt = textGo.AddComponent<RectTransform>();
        textRt.anchorMin = new Vector2(0, 0);
        textRt.anchorMax = new Vector2(1, 1);
        textRt.offsetMin = textRt.offsetMax = Vector2.zero;
        var text = textGo.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 18; text.color = Color.white; text.alignment = TextAnchor.MiddleCenter;
        text.text = label;
        return btn;
    }

    void ShowPanel() { if (_panel != null) _panel.SetActive(true); }
    void HidePanel() { if (_panel != null) _panel.SetActive(false); }
    void SetStatus(string msg) { if (_statusText != null) _statusText.text = msg; }

    static string GetArg(string key)
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        foreach (var a in System.Environment.GetCommandLineArgs())
        {
            if (a.StartsWith("-" + key + "="))  return a.Substring(key.Length + 2);
            if (a.StartsWith("--" + key + "=")) return a.Substring(key.Length + 3);
        }
#endif
        return "";
    }

    [System.Serializable] class UserPayload { public int id; public string name; public string email; }
}
