using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Unity.MLAgents.Demonstrations;

/// <summary>
/// Standalone client uploads each recorded episode's .demo file to
/// app.linn.games for GAIL training. Replaces the previous
/// WebGL-multiplayer-only flow (Unity 6 alpha URP+WebGL is broken — see
/// docs/STATUS.md).
///
/// Setup:
///   1. Add this component to the drone GameObject alongside DemonstrationRecorder.
///   2. Set apiBase + jwtSource in the Inspector (jwtSource: env var, file, or CLI arg).
///   3. On EndEpisode, call <see cref="UploadLatestDemo"/> via ShepherdGameManager.OnRoundEnded.
/// </summary>
public class DemoUploader : MonoBehaviour
{
    [Header("Server")]
    public string apiBase = "https://app.linn.games/api/shepherd";

    [Header("JWT source")]
    [Tooltip("Read JWT from this env var (highest priority).")]
    public string jwtEnvVar = "SHEPHERD_JWT";
    [Tooltip("Or from this file path (fallback). Use $HOME-expansion-style with '~' allowed.")]
    public string jwtFilePath = "~/.config/dronedetect/jwt.txt";
    [Tooltip("Or last-resort: directly pasted token. NEVER ship a build with this filled in.")]
    public string jwtInlineFallback = "";

    [Header("Demo Metadata")]
    public string agentType = "DroneShepherd";
    [Tooltip("Standalone is always role=drone in current setup.")]
    public string role = "drone";

    [Header("Auto-upload")]
    [Tooltip("If true: scan recorder output folder for new .demo files and upload them.")]
    public bool autoUploadOnEpisodeEnd = true;

    DemonstrationRecorder _recorder;

    /// <summary>Status updates for UI hooks: "Uploading…", "Demo uploaded ✓", "Upload failed: …".</summary>
    public event System.Action<string> OnStatusChanged;

    void Awake()
    {
        // Recorder may live on a child object (drone prefab) or on the same GO
        _recorder = GetComponent<DemonstrationRecorder>()
                    ?? FindAnyObjectByType<DemonstrationRecorder>();
    }

    /// <summary>
    /// Called by ShepherdGameManager.OnRoundEnded.
    /// Picks the most recently modified .demo file in the recorder's output
    /// directory and uploads it.
    /// </summary>
    public void UploadLatestDemo(int sheepSaved, int sheepCaught, float duration, string sessionCode = null)
    {
        if (!autoUploadOnEpisodeEnd) return;
        var demoPath = FindLatestDemoFile();
        if (demoPath == null)
        {
            Debug.LogWarning("[DemoUploader] No .demo file found in recorder output");
            return;
        }
        StartCoroutine(PostDemo(demoPath, sheepSaved, sheepCaught, duration, sessionCode));
    }

    string FindLatestDemoFile()
    {
        if (_recorder == null) return null;
        var dir = _recorder.DemonstrationDirectory;
        if (string.IsNullOrEmpty(dir)) dir = "Assets/Demonstrations/DroneSessions";

        // In standalone build the recorder writes to Application.persistentDataPath
        var searchDirs = new[]
        {
            dir,
            Path.Combine(Application.persistentDataPath, dir),
            Path.Combine(Application.persistentDataPath, "Demonstrations"),
        };

        FileInfo newest = null;
        foreach (var d in searchDirs)
        {
            if (!Directory.Exists(d)) continue;
            foreach (var fi in new DirectoryInfo(d).GetFiles("*.demo"))
            {
                if (newest == null || fi.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                    newest = fi;
            }
        }
        return newest?.FullName;
    }

    string ResolveJwt()
    {
        var t = System.Environment.GetEnvironmentVariable(jwtEnvVar);
        if (!string.IsNullOrEmpty(t)) return t;

        var path = jwtFilePath?.Replace("~", System.Environment.GetEnvironmentVariable("HOME") ?? "");
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try { return File.ReadAllText(path).Trim(); } catch { /* fall through */ }
        }

        return jwtInlineFallback;
    }

    IEnumerator PostDemo(string demoPath, int sheepSaved, int sheepCaught, float duration, string sessionCode)
    {
        var token = ResolveJwt();
        if (string.IsNullOrEmpty(token))
        {
            Debug.LogWarning("[DemoUploader] No JWT available — skipping upload");
            OnStatusChanged?.Invoke("Demo-Upload übersprungen (kein JWT)");
            yield break;
        }
        OnStatusChanged?.Invoke("Demo wird hochgeladen…");

        byte[] demoBytes;
        try { demoBytes = File.ReadAllBytes(demoPath); }
        catch (System.Exception ex) { Debug.LogError("[DemoUploader] read failed: " + ex.Message); yield break; }

        var form = new WWWForm();
        form.AddBinaryData("demo", demoBytes, Path.GetFileName(demoPath), "application/octet-stream");
        form.AddField("agent_type", agentType);
        form.AddField("role", role);
        form.AddField("sheep_saved", sheepSaved);
        form.AddField("sheep_caught", sheepCaught);
        form.AddField("duration_seconds", Mathf.RoundToInt(duration));
        if (!string.IsNullOrEmpty(sessionCode)) form.AddField("session_code", sessionCode);

        using var req = UnityWebRequest.Post($"{apiBase}/demos/upload", form);
        req.SetRequestHeader("Authorization", $"Bearer {token}");
        req.SetRequestHeader("Accept", "application/json");
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            Debug.Log($"[DemoUploader] OK ({demoBytes.Length / 1024} KB): {req.downloadHandler.text}");
            OnStatusChanged?.Invoke($"Demo hochgeladen ✓ ({demoBytes.Length / 1024} KB)");
        }
        else
        {
            Debug.LogWarning($"[DemoUploader] Upload failed ({req.responseCode}): {req.error} — {req.downloadHandler.text}");
            OnStatusChanged?.Invoke($"Upload fehlgeschlagen ({req.responseCode})");
        }
    }
}
