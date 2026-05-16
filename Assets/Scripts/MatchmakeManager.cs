using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

/// <summary>
/// GameStart → ChooseSide → Matchmake → Load-Arena flow.
///
/// Drop this on a MainMenu-scene GameObject and wire the two role buttons to
/// <see cref="MatchmakeAsWolf"/> / <see cref="MatchmakeAsDrone"/>. Configure
/// apiBase + JWT in Inspector OR via CLI args (-api=, -token=).
///
/// Flow:
/// 1. User clicks Wolf or Drone.
/// 2. POST /api/shepherd/matchmake — server returns session code + matched-or-not.
/// 3. If matched=false: spin for up to <see cref="matchmakeTimeoutSeconds"/>,
///    polling /sessions/{code} until opponent_role_free flips to false.
/// 4. If timeout still no opponent and we are host: POST /start-with-ai —
///    server marks empty slot as AI (user_id=0), returns ai_role.
/// 5. Load the play scene with session/token/role/host/uid/ai-role as
///    PlayerPrefs so the next scene reads them on Start.
/// </summary>
public class MatchmakeManager : MonoBehaviour
{
    [Header("Server")]
    public string apiBase = "https://app.linn.games";    // -api= override possible
    [Tooltip("Bearer token. Production: minted by app.linn.games per user. Test: tinker artisan.")]
    public string jwt = "";

    [Header("Flow")]
    public string playScene = "ShepherdArena";
    public float matchmakeTimeoutSeconds = 30f;
    public float pollIntervalSeconds = 1.5f;

    [Header("UI hooks")]
    public GameObject roleSelectPanel;     // shown initially
    public GameObject waitingPanel;        // shown while polling
    public UnityEngine.UI.Text waitingText;

    void Start()
    {
        // CLI / URL overrides — same convention as ShepherdGameManager
        var cliApi   = GetArg("api");
        var cliToken = GetArg("token");
        if (!string.IsNullOrEmpty(cliApi))   apiBase = cliApi;
        if (!string.IsNullOrEmpty(cliToken)) jwt     = cliToken;

        ShowRoleSelect();
    }

    public void MatchmakeAsWolf()  => StartCoroutine(DoMatchmake("wolf"));
    public void MatchmakeAsDrone() => StartCoroutine(DoMatchmake("drone"));

    IEnumerator DoMatchmake(string role)
    {
        if (string.IsNullOrEmpty(jwt))
        {
            Debug.LogError("[MatchmakeManager] No JWT — set via Inspector or -token= CLI arg");
            yield break;
        }
        ShowWaiting($"Matchmaking als {role}…");

        string body = $"{{\"role\":\"{role}\"}}";
        var req = MakePostJson($"{apiBase}/api/shepherd/matchmake", body);
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[MatchmakeManager] matchmake POST failed: {req.error} — {req.downloadHandler.text}");
            ShowRoleSelect();
            yield break;
        }

        var resp = JsonUtility.FromJson<MatchmakeResponse>(req.downloadHandler.text);
        Debug.Log($"[MatchmakeManager] code={resp.code} matched={resp.matched} is_host={resp.is_host}");

        // If immediately matched, hand off to play scene
        if (resp.matched)
        {
            LoadPlayScene(resp.code, role, resp.is_host, null);
            yield break;
        }

        // Otherwise poll for opponent until timeout
        float elapsed = 0f;
        bool gotOpponent = false;
        while (elapsed < matchmakeTimeoutSeconds && !gotOpponent)
        {
            ShowWaiting($"Warte auf Gegner… {Mathf.CeilToInt(matchmakeTimeoutSeconds - elapsed)}s");
            yield return new WaitForSeconds(pollIntervalSeconds);
            elapsed += pollIntervalSeconds;

            var poll = MakeGet($"{apiBase}/api/shepherd/sessions/{resp.code}");
            yield return poll.SendWebRequest();
            if (poll.result == UnityWebRequest.Result.Success)
            {
                // Two players in `players` array = matched
                if (poll.downloadHandler.text.Contains("\"role\":\"wolf\"") &&
                    poll.downloadHandler.text.Contains("\"role\":\"drone\""))
                {
                    gotOpponent = true;
                }
            }
        }

        if (gotOpponent)
        {
            LoadPlayScene(resp.code, role, resp.is_host, null);
            yield break;
        }

        // Timeout → only the host can request AI fallback
        if (!resp.is_host)
        {
            Debug.LogError("[MatchmakeManager] Timeout and not host — cannot trigger AI fallback");
            ShowRoleSelect();
            yield break;
        }

        ShowWaiting("Kein Gegner gefunden — KI-Bot übernimmt…");
        var aiReq = MakePostJson($"{apiBase}/api/shepherd/sessions/{resp.code}/start-with-ai", "{}");
        yield return aiReq.SendWebRequest();
        if (aiReq.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[MatchmakeManager] start-with-ai failed: {aiReq.error}");
            ShowRoleSelect();
            yield break;
        }
        var aiResp = JsonUtility.FromJson<AiFallbackResponse>(aiReq.downloadHandler.text);
        LoadPlayScene(resp.code, role, resp.is_host, aiResp.ai_role);
    }

    void LoadPlayScene(string code, string role, bool isHost, string aiRole)
    {
        PlayerPrefs.SetString("shepherd_session", code);
        PlayerPrefs.SetString("shepherd_token",   jwt);
        PlayerPrefs.SetString("shepherd_role",    role);
        PlayerPrefs.SetInt   ("shepherd_host",    isHost ? 1 : 0);
        PlayerPrefs.SetString("shepherd_api",     apiBase);
        PlayerPrefs.SetString("shepherd_ai_role", aiRole ?? "");
        PlayerPrefs.Save();
        Debug.Log($"[MatchmakeManager] Loading {playScene} — code={code} role={role} host={isHost} ai_role={aiRole}");
        SceneManager.LoadScene(playScene);
    }

    // ── UI helpers ─────────────────────────────────────────────────────────
    void ShowRoleSelect()
    {
        if (roleSelectPanel) roleSelectPanel.SetActive(true);
        if (waitingPanel)    waitingPanel.SetActive(false);
    }
    void ShowWaiting(string msg)
    {
        if (roleSelectPanel) roleSelectPanel.SetActive(false);
        if (waitingPanel)    waitingPanel.SetActive(true);
        if (waitingText)     waitingText.text = msg;
    }

    // ── HTTP helpers ───────────────────────────────────────────────────────
    UnityWebRequest MakePostJson(string url, string body)
    {
        var req = new UnityWebRequest(url, "POST");
        req.uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type",  "application/json");
        req.SetRequestHeader("Authorization", $"Bearer {jwt}");
        req.SetRequestHeader("Accept",        "application/json");
        return req;
    }
    UnityWebRequest MakeGet(string url)
    {
        var req = UnityWebRequest.Get(url);
        req.SetRequestHeader("Authorization", $"Bearer {jwt}");
        req.SetRequestHeader("Accept",        "application/json");
        return req;
    }

    static string GetArg(string key)
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        var args = System.Environment.GetCommandLineArgs();
        foreach (var a in args)
        {
            if (a.StartsWith("-"  + key + "=")) return a.Substring(key.Length + 2);
            if (a.StartsWith("--" + key + "=")) return a.Substring(key.Length + 3);
        }
#endif
        return "";
    }

    [Serializable] class MatchmakeResponse  { public string code; public string role; public bool is_host; public bool opponent_role_free; public bool matched; }
    [Serializable] class AiFallbackResponse { public string ai_role; public string status; }
}
