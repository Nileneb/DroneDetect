using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// DroneHUD — Erstellt automatisch ein Canvas mit allen UI-Elementen.
/// Wird von DroneAgent.Initialize() aufgerufen.
///
/// Elemente:
///   - Fadenkreuz (zentriert)
///   - Status-Panel (oben links): Zustand, Hoehe, Speed, Batterie
///   - Mission-Panel (oben rechts): Targets, Steps
///   - Fire-Button (unten rechts, nur visuell/Keyboard F)
///   - Nachrichten-Zeile (unten mitte)
/// </summary>
public class DroneHUD : MonoBehaviour
{
    // ═══ Referenzen ═══
    DroneAgent _agent;
    Canvas _canvas;

    // UI Elemente
    Text _stateText;
    Text _altitudeText;
    Text _speedText;
    Text _batteryText;
    Text _targetsText;
    Text _stepsText;
    Text _messageText;
    Image _crosshairH;
    Image _crosshairV;
    Image _crosshairDot;
    Image _fireIndicator;
    Text _fireText;

    // Message Timer
    float _messageTimer;
    string _currentMessage;

    // ═══ Factory ═══

    /// <summary>
    /// Erstellt das komplette HUD per Code. Aufruf: DroneHUD.CreateHUD(agentRef);
    /// </summary>
    public static DroneHUD CreateHUD(DroneAgent agent)
    {
        // Pruefen ob schon ein HUD existiert
        DroneHUD existing = FindObjectOfType<DroneHUD>();
        if (existing != null)
        {
            existing._agent = agent;
            return existing;
        }

        // Canvas erstellen
        GameObject canvasGO = new GameObject("DroneHUD_Canvas");
        Canvas canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        canvasGO.AddComponent<GraphicRaycaster>();

        // HUD-Komponente
        DroneHUD hud = canvasGO.AddComponent<DroneHUD>();
        hud._agent = agent;
        hud._canvas = canvas;
        hud.BuildUI(canvasGO.transform);

        DontDestroyOnLoad(canvasGO);
        Debug.Log("[DroneHUD] HUD erstellt.");
        return hud;
    }

    // ═══ UI aufbauen ═══

    void BuildUI(Transform parent)
    {
        BuildCrosshair(parent);
        BuildStatusPanel(parent);
        BuildMissionPanel(parent);
        BuildFireButton(parent);
        BuildMessageLine(parent);
    }

    // ── Fadenkreuz ──

    void BuildCrosshair(Transform parent)
    {
        Color crossColor = new Color(0f, 1f, 0.4f, 0.8f); // Gruen, leicht transparent

        // Horizontale Linie
        _crosshairH = CreateImage(parent, "Crosshair_H", crossColor);
        RectTransform rtH = _crosshairH.rectTransform;
        rtH.anchorMin = rtH.anchorMax = new Vector2(0.5f, 0.5f);
        rtH.sizeDelta = new Vector2(30, 2);
        rtH.anchoredPosition = Vector2.zero;

        // Vertikale Linie
        _crosshairV = CreateImage(parent, "Crosshair_V", crossColor);
        RectTransform rtV = _crosshairV.rectTransform;
        rtV.anchorMin = rtV.anchorMax = new Vector2(0.5f, 0.5f);
        rtV.sizeDelta = new Vector2(2, 30);
        rtV.anchoredPosition = Vector2.zero;

        // Mittelpunkt
        _crosshairDot = CreateImage(parent, "Crosshair_Dot", crossColor);
        RectTransform rtD = _crosshairDot.rectTransform;
        rtD.anchorMin = rtD.anchorMax = new Vector2(0.5f, 0.5f);
        rtD.sizeDelta = new Vector2(4, 4);
        rtD.anchoredPosition = Vector2.zero;

        // Aeusserer Ring (4 kurze Striche)
        float ringSize = 20f;
        CreateRingLine(parent, "Ring_T", crossColor, new Vector2(0, ringSize), new Vector2(2, 8));
        CreateRingLine(parent, "Ring_B", crossColor, new Vector2(0, -ringSize), new Vector2(2, 8));
        CreateRingLine(parent, "Ring_L", crossColor, new Vector2(-ringSize, 0), new Vector2(8, 2));
        CreateRingLine(parent, "Ring_R", crossColor, new Vector2(ringSize, 0), new Vector2(8, 2));
    }

    void CreateRingLine(Transform parent, string name, Color color, Vector2 pos, Vector2 size)
    {
        Image img = CreateImage(parent, name, color);
        RectTransform rt = img.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = size;
        rt.anchoredPosition = pos;
    }

    // ── Status-Panel (oben links) ──

    void BuildStatusPanel(Transform parent)
    {
        // Hintergrund
        GameObject panel = CreatePanel(parent, "StatusPanel",
            new Vector2(0, 1), new Vector2(0, 1),  // oben links
            new Vector2(10, -10),                    // offset
            new Vector2(280, 130));

        _stateText = CreateText(panel.transform, "StateText", 18, TextAnchor.UpperLeft,
            new Vector2(10, -8), "STATE: ---");
        _altitudeText = CreateText(panel.transform, "AltText", 16, TextAnchor.UpperLeft,
            new Vector2(10, -32), "ALT: 0.0m");
        _speedText = CreateText(panel.transform, "SpeedText", 16, TextAnchor.UpperLeft,
            new Vector2(10, -54), "SPD: 0.0 m/s");
        _batteryText = CreateText(panel.transform, "BatteryText", 16, TextAnchor.UpperLeft,
            new Vector2(10, -76), "BAT: 100%");
    }

    // ── Mission-Panel (oben rechts) ──

    void BuildMissionPanel(Transform parent)
    {
        GameObject panel = CreatePanel(parent, "MissionPanel",
            new Vector2(1, 1), new Vector2(1, 1),  // oben rechts
            new Vector2(-10, -10),
            new Vector2(250, 100));

        _targetsText = CreateText(panel.transform, "TargetsText", 18, TextAnchor.UpperRight,
            new Vector2(-10, -8), "TARGETS: 0/4");
        _stepsText = CreateText(panel.transform, "StepsText", 16, TextAnchor.UpperRight,
            new Vector2(-10, -34), "STEPS: 0/3000");
    }

    // ── Fire-Button (unten rechts) ──

    void BuildFireButton(Transform parent)
    {
        GameObject btnGO = new GameObject("FireButton");
        btnGO.transform.SetParent(parent, false);

        _fireIndicator = btnGO.AddComponent<Image>();
        _fireIndicator.color = new Color(0.8f, 0.1f, 0.1f, 0.6f);

        RectTransform rt = btnGO.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(1, 0); // unten rechts
        rt.pivot = new Vector2(1, 0);
        rt.anchoredPosition = new Vector2(-20, 20);
        rt.sizeDelta = new Vector2(120, 50);

        _fireText = CreateText(btnGO.transform, "FireLabel", 20, TextAnchor.MiddleCenter,
            Vector2.zero, "FIRE [F]");
        _fireText.color = Color.white;
        RectTransform ftrt = _fireText.rectTransform;
        ftrt.anchorMin = Vector2.zero;
        ftrt.anchorMax = Vector2.one;
        ftrt.offsetMin = Vector2.zero;
        ftrt.offsetMax = Vector2.zero;
    }

    // ── Nachrichten-Zeile (unten mitte) ──

    void BuildMessageLine(Transform parent)
    {
        _messageText = CreateText(parent, "MessageText", 22, TextAnchor.MiddleCenter,
            new Vector2(0, 60), "");
        RectTransform rt = _messageText.rectTransform;
        rt.anchorMin = new Vector2(0.5f, 0);
        rt.anchorMax = new Vector2(0.5f, 0);
        rt.sizeDelta = new Vector2(600, 40);

        _messageText.color = new Color(1f, 0.9f, 0.2f, 1f); // Gelb
    }

    // ═══ Update ═══

    void Update()
    {
        if (_agent == null) return;

        // Status
        string stateStr = _agent.CurrentDroneState.ToString().ToUpper();
        Color stateColor = GetStateColor(_agent.CurrentDroneState);
        _stateText.text = $"STATE: {stateStr}";
        _stateText.color = stateColor;

        _altitudeText.text = $"ALT: {_agent.Altitude:F1}m";
        _speedText.text = $"SPD: {_agent.Speed:F1} m/s";

        float bat = _agent.Battery;
        _batteryText.text = $"BAT: {bat:F0}%";
        _batteryText.color = bat < 20f ? Color.red : (bat < 50f ? Color.yellow : Color.green);

        // Mission
        _targetsText.text = $"TARGETS: {_agent.TargetsCollected}/{_agent.TotalTargets}";
        _targetsText.color = _agent.AllTargetsCollected ? Color.green : Color.white;

        _stepsText.text = $"STEPS: {_agent.StepsInEpisode}/{_agent.CurrentEpisodeStepLimit}";

        // Fire-Button Feedback
        if (Input.GetKey(KeyCode.F))
        {
            _fireIndicator.color = new Color(1f, 0.3f, 0.1f, 0.9f);
            _fireText.text = ">>> FIRE <<<";
            ShowMessage("MARKER DEPLOYED!", 0.5f);
        }
        else
        {
            _fireIndicator.color = new Color(0.8f, 0.1f, 0.1f, 0.6f);
            _fireText.text = "FIRE [F]";
        }

        // Fadenkreuz Farbe: gruen normal, orange wenn Lande-Modus
        Color chColor = _agent.AllTargetsCollected
            ? new Color(1f, 0.5f, 0f, 0.9f)  // Orange = Lande-Modus
            : new Color(0f, 1f, 0.4f, 0.8f);  // Gruen = Navigation
        _crosshairH.color = chColor;
        _crosshairV.color = chColor;
        _crosshairDot.color = chColor;

        // Nachrichten-Timer
        if (_messageTimer > 0)
        {
            _messageTimer -= Time.deltaTime;
            if (_messageTimer <= 0)
                _messageText.text = "";
        }
    }

    // ═══ Public API ═══

    public void ShowMessage(string msg, float duration = 2f)
    {
        _messageText.text = msg;
        _messageTimer = duration;
    }

    // ═══ Helper ═══

    Color GetStateColor(DroneState state)
    {
        switch (state)
        {
            case DroneState.Landed:    return Color.gray;
            case DroneState.TakingOff: return Color.yellow;
            case DroneState.Hovering:  return Color.cyan;
            case DroneState.Flying:    return Color.green;
            case DroneState.Landing:   return new Color(1f, 0.5f, 0f); // Orange
            case DroneState.Emergency: return Color.red;
            default:                   return Color.white;
        }
    }

    Image CreateImage(Transform parent, string name, Color color)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Image img = go.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    GameObject CreatePanel(Transform parent, string name,
        Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);

        Image bg = go.AddComponent<Image>();
        bg.color = new Color(0, 0, 0, 0.5f); // Halbtransparent schwarz
        bg.raycastTarget = false;

        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.pivot = anchorMin; // Pivot = Anker
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;

        return go;
    }

    Text CreateText(Transform parent, string name, int fontSize,
        TextAnchor alignment, Vector2 pos, string defaultText)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);

        Text txt = go.AddComponent<Text>();
        txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        txt.fontSize = fontSize;
        txt.alignment = alignment;
        txt.color = Color.white;
        txt.text = defaultText;
        txt.raycastTarget = false;

        // Shadow fuer bessere Lesbarkeit
        Shadow shadow = go.AddComponent<Shadow>();
        shadow.effectColor = new Color(0, 0, 0, 0.8f);
        shadow.effectDistance = new Vector2(1, -1);

        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = pos;
        rt.sizeDelta = new Vector2(260, 24);

        return txt;
    }
}
