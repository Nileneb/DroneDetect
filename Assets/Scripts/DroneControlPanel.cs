using UnityEngine;
using UnityEngine.UI;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Demonstrations;

/// <summary>
/// Drone-Mode Panel.
/// 🎮 Spielen  = Mensch fliegt + Demo wird aufgenommen (immer!)
/// 🤖 KI fliegt = DroneCSI.onnx übernimmt (Curriculum Phase 4)
///
/// shepherdMode (Inspector-Flag, vor Build setzen):
///   false → Agent1-Parcours-Training (DroneAgent aktiv, Targets an)
///   true  → Shepherd-Demo (ShepherdDroneAgent aktiv, Wolf+Schafe an)
/// </summary>
public class DroneControlPanel : MonoBehaviour
{
    public enum DroneMode { Play, AIFly }

    [Header("Scene Mode (set before build)")]
    public bool shepherdMode = false;

    [Header("Buttons")]
    public Button btnPlay;
    public Button btnAI;

    [Header("Status")]
    public Text modeLabel;
    public Text recIndicator;

    static readonly Color ColPlay     = new Color(0.12f, 0.52f, 0.22f);
    static readonly Color ColAI       = new Color(0.12f, 0.32f, 0.72f);
    static readonly Color ColInactive = new Color(0.22f, 0.22f, 0.28f);
    static readonly Color ColWarn     = new Color(0.75f, 0.45f, 0.05f);

    DroneMode _mode = DroneMode.Play;
    float _blinkTimer;

    // ── Persistent listener entry points ────────────────────────────────────
    public void OnClickPlay() => ApplyMode(DroneMode.Play);
    public void OnClickAI()   => ApplyMode(DroneMode.AIFly);

    void Start()
    {
        BootstrapSceneMode();
        // WHY: shepherd PvP/single-player mode has no concept of "Demo recording" vs "AI flight".
        // The Play/AI buttons are training-mode-only. Hide them + the panel chrome in Shepherd.
        if (shepherdMode)
        {
            if (btnPlay != null)      btnPlay.gameObject.SetActive(false);
            if (btnAI != null)        btnAI.gameObject.SetActive(false);
            if (modeLabel != null)    modeLabel.gameObject.SetActive(false);
            if (recIndicator != null) recIndicator.gameObject.SetActive(false);
            return;
        }
        ApplyMode(DroneMode.Play);
    }

    void Update()
    {
        if (recIndicator == null) return;
        if (_mode == DroneMode.Play)
        {
            _blinkTimer += Time.deltaTime;
            recIndicator.gameObject.SetActive(_blinkTimer % 1f < 0.5f);
        }
        else
        {
            recIndicator.gameObject.SetActive(false);
        }
    }

    // ── Szenen-Modus beim Start anwenden ────────────────────────────────────

    void BootstrapSceneMode()
    {
        // Shepherd-Objekte per Discovery aktivieren/deaktivieren
        var wolf  = FindAnyObjectByType<WolfPlayer>();
        var sheep = FindObjectsByType<SheepNPC>();
        var mgr   = FindAnyObjectByType<ShepherdGameManager>();
        var targetsGo = GameObject.Find("Targets");

        if (wolf != null)  wolf.gameObject.SetActive(shepherdMode);
        if (mgr  != null)  mgr.gameObject.SetActive(shepherdMode);
        foreach (var s in sheep) s.gameObject.SetActive(shepherdMode);
        if (targetsGo != null) targetsGo.SetActive(!shepherdMode);

        // Drohne: richtige Agent-Komponente aktivieren
        var sdc = FindAnyObjectByType<SimulatedDroneController>();
        if (sdc == null) return;
        var droneGo = sdc.gameObject;

        var droneAgent   = droneGo.GetComponent<DroneAgent>();
        var shepherdAgent = droneGo.GetComponent<ShepherdDroneAgent>();

        if (droneAgent    != null) droneAgent.enabled    = !shepherdMode;
        if (shepherdAgent != null) shepherdAgent.enabled =  shepherdMode;

        Debug.Log($"[DroneControlPanel] SceneMode={( shepherdMode ? "Shepherd" : "Parcours" )} " +
                  $"droneAgent={droneAgent?.enabled} shepherdAgent={shepherdAgent?.enabled}");
    }

    // ── Flug-Modus umschalten ───────────────────────────────────────────────

    void ApplyMode(DroneMode mode)
    {
        _mode = mode;
        var bp = FindAnyObjectByType<BehaviorParameters>();
        var dr = FindAnyObjectByType<DemonstrationRecorder>();

        switch (mode)
        {
            case DroneMode.Play:
                if (bp != null) bp.BehaviorType = BehaviorType.Default;
                if (dr != null) dr.Record = true;
                SetLabel("🎮 Spielen + ⏺ REC", ColPlay);
                Highlight(btnPlay, ColPlay);
                Highlight(btnAI,   ColInactive);
                _blinkTimer = 0f;
                break;

            case DroneMode.AIFly:
                if (dr != null) dr.Record = false;
                if (bp != null)
                {
                    if (bp.Model != null)
                    {
                        bp.BehaviorType = BehaviorType.InferenceOnly;
                        SetLabel("🤖 KI fliegt (DroneCSI)", ColAI);
                        Highlight(btnPlay, ColInactive);
                        Highlight(btnAI,   ColAI);
                    }
                    else
                    {
                        SetLabel("⚠ Kein Modell geladen!", ColWarn);
                        Highlight(btnAI, ColWarn);
                        _mode = DroneMode.Play;
                    }
                }
                break;
        }

        // UX: hide the button that triggered the now-active mode — clicking it again is a no-op,
        // user can still switch back via the OTHER (still-visible) button. Falls back to "both
        // visible" if user lands in Play after AIFly missing-model rollback (handled above).
        if (btnPlay != null) btnPlay.gameObject.SetActive(_mode != DroneMode.Play);
        if (btnAI   != null) btnAI.gameObject.SetActive(_mode != DroneMode.AIFly);

        Debug.Log($"[DroneControlPanel] {mode} | bp={bp != null} model={bp?.Model != null} dr={dr?.Record}");
    }

    void SetLabel(string text, Color col)
    {
        if (modeLabel == null) return;
        modeLabel.text  = text;
        modeLabel.color = col;
    }

    void Highlight(Button btn, Color col)
    {
        if (btn == null) return;
        var img = btn.GetComponent<Image>();
        if (img != null) img.color = col;
    }
}
