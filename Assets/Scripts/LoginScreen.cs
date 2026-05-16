using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

/// <summary>
/// Standalone login UI for the Shepherd client. Builds itself programmatically
/// so it works in any scene without manual Canvas wiring. Calls
/// POST /api/shepherd/auth/login → Sanctum personal-access-token, cached in
/// PlayerPrefs so subsequent launches skip the login flow until token revoke
/// or expiry.
///
/// Usage: drop this on any GameObject in MainMenu next to MatchmakeManager.
/// On Start it checks for a cached token; if present, calls /auth/me to
/// verify, then hides itself and lets MatchmakeManager show the role-select.
/// If not present or invalid, it shows email/password inputs.
/// </summary>
public class LoginScreen : MonoBehaviour
{
    public const string PrefToken = "shepherd_token";
    public const string PrefUserEmail = "shepherd_user_email";
    public const string PrefUserName = "shepherd_user_name";

    [Header("Server")]
    public string apiBase = "https://app.linn.games";

    [Header("Refs (set by Awake if null)")]
    public MatchmakeManager matchmake;

    Canvas _canvas;
    GameObject _panel;
    InputField _emailField;
    InputField _passwordField;
    Text _statusText;
    Button _loginBtn;
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
            SetStatus($"Token vorhanden — verifiziere…");
            yield return VerifyToken(cached);
            yield break;
        }
        ShowPanel();
    }

    IEnumerator VerifyToken(string token)
    {
        using var req = UnityWebRequest.Get($"{apiBase}/api/shepherd/auth/me");
        req.SetRequestHeader("Authorization", $"Bearer {token}");
        req.SetRequestHeader("Accept", "application/json");
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            var me = JsonUtility.FromJson<UserPayload>(req.downloadHandler.text);
            PlayerPrefs.SetString(PrefUserEmail, me.email ?? "");
            PlayerPrefs.SetString(PrefUserName, me.name ?? "");
            PlayerPrefs.Save();
            ApplyToMatchmake(token);
            HidePanel();
            yield break;
        }

        Debug.Log($"[LoginScreen] cached token rejected ({req.responseCode}) — falling back to login");
        PlayerPrefs.DeleteKey(PrefToken);
        ShowPanel();
    }

    public void OnLoginClicked()
    {
        if (_loginBtn != null) _loginBtn.interactable = false;
        StartCoroutine(DoLogin(_emailField.text.Trim(), _passwordField.text));
    }

    public void OnOfflineClicked()
    {
        SetStatus("Offline-Modus — Demo gegen KI");
        HidePanel();
        ApplyToMatchmake("");  // empty token → MatchmakeManager.DoMatchmake falls into offline flow
    }

    IEnumerator DoLogin(string email, string password)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            SetStatus("Email + Passwort erforderlich");
            if (_loginBtn != null) _loginBtn.interactable = true;
            yield break;
        }
        SetStatus("Logge ein…");

        var body = $"{{\"email\":\"{EscapeJson(email)}\",\"password\":\"{EscapeJson(password)}\",\"device_name\":\"shepherd-standalone\"}}";
        using var req = new UnityWebRequest($"{apiBase}/api/shepherd/auth/login", "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Accept", "application/json");
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[LoginScreen] login failed ({req.responseCode}): {req.error} — {req.downloadHandler.text}");
            SetStatus(req.responseCode == 422 ? "Falsche Email oder Passwort" : $"Login-Fehler ({req.responseCode})");
            if (_loginBtn != null) _loginBtn.interactable = true;
            yield break;
        }

        var resp = JsonUtility.FromJson<LoginResponse>(req.downloadHandler.text);
        PlayerPrefs.SetString(PrefToken, resp.token);
        PlayerPrefs.SetString(PrefUserEmail, resp.user?.email ?? email);
        PlayerPrefs.SetString(PrefUserName, resp.user?.name ?? "");
        PlayerPrefs.Save();

        SetStatus($"Eingeloggt als {resp.user?.name ?? email}");
        ApplyToMatchmake(resp.token);
        HidePanel();
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
        // Shared canvas — reuse Matchmake's if already present in scene
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
        panelRt.anchorMin = new Vector2(0.5f, 0.5f);
        panelRt.anchorMax = new Vector2(0.5f, 0.5f);
        panelRt.pivot     = new Vector2(0.5f, 0.5f);
        panelRt.sizeDelta = new Vector2(420, 320);
        var bg = _panel.AddComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0.78f);

        BuildLabel("Title", "Shepherd Login", 24, new Vector2(0, 130), FontStyle.Bold);
        _emailField    = BuildInput("EmailField",    "Email",    new Vector2(0,  60), false);
        _passwordField = BuildInput("PasswordField", "Passwort", new Vector2(0,  10), true);
        _statusText    = BuildLabel("Status", "", 14, new Vector2(0, -40), FontStyle.Italic);

        _loginBtn = BuildButton("LoginBtn", "Einloggen", new Vector2(-90, -100), 160, 44);
        _loginBtn.onClick.AddListener(OnLoginClicked);

        _offlineBtn = BuildButton("OfflineBtn", "Offline (KI)", new Vector2(90, -100), 160, 44);
        _offlineBtn.onClick.AddListener(OnOfflineClicked);
        var off = _offlineBtn.GetComponent<Image>();
        if (off != null) off.color = new Color(0.4f, 0.4f, 0.4f, 0.9f);
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
        rt.sizeDelta = new Vector2(380, size + 6);
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

    InputField BuildInput(string name, string placeholder, Vector2 anchored, bool password)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_panel.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(340, 36);
        rt.anchoredPosition = anchored;
        var bg = go.AddComponent<Image>();
        bg.color = new Color(1f, 1f, 1f, 0.13f);

        var input = go.AddComponent<InputField>();
        if (password) input.contentType = InputField.ContentType.Password;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        var textRt = textGo.AddComponent<RectTransform>();
        textRt.anchorMin = new Vector2(0, 0); textRt.anchorMax = new Vector2(1, 1);
        textRt.offsetMin = new Vector2(10, 4); textRt.offsetMax = new Vector2(-10, -4);
        var text = textGo.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 18; text.color = Color.white; text.alignment = TextAnchor.MiddleLeft;
        text.supportRichText = false;

        var phGo = new GameObject("Placeholder");
        phGo.transform.SetParent(go.transform, false);
        var phRt = phGo.AddComponent<RectTransform>();
        phRt.anchorMin = new Vector2(0, 0); phRt.anchorMax = new Vector2(1, 1);
        phRt.offsetMin = new Vector2(10, 4); phRt.offsetMax = new Vector2(-10, -4);
        var ph = phGo.AddComponent<Text>();
        ph.font = text.font; ph.fontSize = 16; ph.color = new Color(1f, 1f, 1f, 0.4f);
        ph.alignment = TextAnchor.MiddleLeft; ph.text = placeholder; ph.fontStyle = FontStyle.Italic;

        input.textComponent = text;
        input.placeholder = ph;
        return input;
    }

    Button BuildButton(string name, string label, Vector2 anchored, float width, float height)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_panel.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(width, height);
        rt.anchoredPosition = anchored;
        var bg = go.AddComponent<Image>();
        bg.color = new Color(0.18f, 0.45f, 0.78f, 0.95f);
        var btn = go.AddComponent<Button>();

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(go.transform, false);
        var textRt = textGo.AddComponent<RectTransform>();
        textRt.anchorMin = new Vector2(0, 0); textRt.anchorMax = new Vector2(1, 1);
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

    static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    static string GetArg(string key)
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        foreach (var a in System.Environment.GetCommandLineArgs())
        {
            if (a.StartsWith("-" + key + "=")) return a.Substring(key.Length + 2);
            if (a.StartsWith("--" + key + "=")) return a.Substring(key.Length + 3);
        }
#endif
        return "";
    }

    [System.Serializable] class UserPayload { public int id; public string name; public string email; }
    [System.Serializable] class LoginResponse { public string token; public UserPayload user; }
}
