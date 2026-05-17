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
    DroneAgent _agent;          // Agent1 mode
    DronePlayer _drone;         // Shepherd mode
    SimulatedDroneController _sdc;  // Shepherd mode — state/alt/speed/battery source
    DroneScarer _scarer;        // Shepherd mode — fire button cooldown
    WolfPlayer _aiWolf;         // Shepherd mode — fear bar source
    WolfFear _aiWolfFear;       // Shepherd mode
    bool _shepherdMode;
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
    Image _fireCooldownMask;    // Shepherd mode — fill drops as cooldown elapses
    Text _fireText;
    Image _fearBarFill;         // Shepherd mode — wolf fear 0..1
    Text _fearLabel;            // Shepherd mode — "Wolfsfurcht: 42%" or "PANIK!"

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
        DroneHUD existing = FindAnyObjectByType<DroneHUD>();
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

    /// <summary>
    /// Shepherd-Mode HUD: same layout as Agent1 but driven by DronePlayer/Scarer/AI-Wolf
    /// instead of DroneAgent (no Mission-Panel, FireButton mirrors DroneScarer cooldown,
    /// optional Wolf-Fear-Bar oben-rechts).
    /// </summary>
    public static DroneHUD CreateShepherdHUD(DronePlayer drone, WolfPlayer aiWolf = null)
    {
        DroneHUD existing = FindAnyObjectByType<DroneHUD>();
        if (existing != null)
        {
            existing._drone = drone;
            existing._sdc = drone != null ? drone.GetComponent<SimulatedDroneController>() : null;
            existing._scarer = drone != null ? drone.scarer : null;
            existing._aiWolf = aiWolf;
            existing._aiWolfFear = aiWolf != null ? aiWolf.GetComponent<WolfFear>() : null;
            existing._shepherdMode = true;
            return existing;
        }

        GameObject canvasGO = new GameObject("DroneShepherdHUD_Canvas");
        Canvas canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        canvasGO.AddComponent<GraphicRaycaster>();

        DroneHUD hud = canvasGO.AddComponent<DroneHUD>();
        hud._drone = drone;
        hud._sdc = drone != null ? drone.GetComponent<SimulatedDroneController>() : null;
        hud._scarer = drone != null ? drone.scarer : null;
        hud._aiWolf = aiWolf;
        hud._aiWolfFear = aiWolf != null ? aiWolf.GetComponent<WolfFear>() : null;
        hud._canvas = canvas;
        hud._shepherdMode = true;
        hud.BuildShepherdUI(canvasGO.transform);

        DontDestroyOnLoad(canvasGO);
        Debug.Log($"[DroneHUD] Shepherd-HUD erstellt (aiWolf={(aiWolf != null ? "yes" : "no")}).");
        return hud;
    }

    void BuildShepherdUI(Transform parent)
    {
        BuildCrosshair(parent);
        BuildStatusPanel(parent);
        if (_aiWolf != null) BuildFearPanel(parent);
        BuildFireButtonWithCooldown(parent);
        BuildMessageLine(parent);
    }

    // Fire-Button mit Scarer-Cooldown-Maske: roter Button mit dunklerem Overlay das von oben
    // nach unten "leerläuft" während Cooldown
    void BuildFireButtonWithCooldown(Transform parent)
    {
        GameObject btnGO = new GameObject("FireButton");
        btnGO.transform.SetParent(parent, false);

        _fireIndicator = btnGO.AddComponent<Image>();
        _fireIndicator.color = new Color(0.8f, 0.1f, 0.1f, 0.85f);

        RectTransform rt = btnGO.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(1, 0);
        rt.pivot = new Vector2(1, 0);
        rt.anchoredPosition = new Vector2(-20, 20);
        rt.sizeDelta = new Vector2(140, 70);

        // Cooldown mask: dark overlay, fillAmount drops from 1 to 0 as scarer recovers
        GameObject mask = new GameObject("CooldownMask");
        mask.transform.SetParent(btnGO.transform, false);
        _fireCooldownMask = mask.AddComponent<Image>();
        _fireCooldownMask.color = new Color(0, 0, 0, 0.6f);
        _fireCooldownMask.type = Image.Type.Filled;
        _fireCooldownMask.fillMethod = Image.FillMethod.Vertical;
        _fireCooldownMask.fillOrigin = (int)Image.OriginVertical.Top;
        _fireCooldownMask.fillAmount = 0f;
        _fireCooldownMask.raycastTarget = false;
        RectTransform mrt = mask.GetComponent<RectTransform>();
        mrt.anchorMin = Vector2.zero;
        mrt.anchorMax = Vector2.one;
        mrt.offsetMin = Vector2.zero;
        mrt.offsetMax = Vector2.zero;

        _fireText = CreateText(btnGO.transform, "FireLabel", 22, TextAnchor.MiddleCenter,
            Vector2.zero, "🔆 SCARER [F]");
        _fireText.color = Color.white;
        RectTransform ftrt = _fireText.rectTransform;
        ftrt.anchorMin = Vector2.zero;
        ftrt.anchorMax = Vector2.one;
        ftrt.offsetMin = Vector2.zero;
        ftrt.offsetMax = Vector2.zero;
    }

    // Wolf-Fear-Bar oben-rechts (statt Mission-Panel)
    void BuildFearPanel(Transform parent)
    {
        GameObject panel = CreatePanel(parent, "FearPanel",
            new Vector2(1, 1), new Vector2(1, 1),
            new Vector2(-10, -10),
            new Vector2(280, 90));
        // anchor pivot top-right
        var prt = panel.GetComponent<RectTransform>();
        prt.pivot = new Vector2(1, 1);

        _fearLabel = CreateText(panel.transform, "FearLabel", 16, TextAnchor.UpperLeft,
            new Vector2(10, -8), "Wolfsfurcht: 0%");

        // Bar background
        GameObject bg = new GameObject("FearBarBG");
        bg.transform.SetParent(panel.transform, false);
        var bgImg = bg.AddComponent<Image>();
        bgImg.color = new Color(0.2f, 0.2f, 0.2f, 0.8f);
        bgImg.raycastTarget = false;
        var bgrt = bg.GetComponent<RectTransform>();
        bgrt.anchorMin = new Vector2(0, 0);
        bgrt.anchorMax = new Vector2(0, 0);
        bgrt.pivot = new Vector2(0, 0);
        bgrt.anchoredPosition = new Vector2(10, 12);
        bgrt.sizeDelta = new Vector2(260, 24);

        // Bar fill
        GameObject fill = new GameObject("FearBarFill");
        fill.transform.SetParent(bg.transform, false);
        _fearBarFill = fill.AddComponent<Image>();
        _fearBarFill.color = new Color(1f, 0.85f, 0.2f, 1f);
        _fearBarFill.type = Image.Type.Filled;
        _fearBarFill.fillMethod = Image.FillMethod.Horizontal;
        _fearBarFill.fillAmount = 0f;
        _fearBarFill.raycastTarget = false;
        var frt = fill.GetComponent<RectTransform>();
        frt.anchorMin = Vector2.zero;
        frt.anchorMax = Vector2.one;
        frt.offsetMin = Vector2.zero;
        frt.offsetMax = Vector2.zero;
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
        if (_shepherdMode) { UpdateShepherd(); return; }
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

    // ═══ Shepherd-Mode Update ═══

    void UpdateShepherd()
    {
        if (_sdc != null)
        {
            string stateStr = _sdc.State.ToString().ToUpper();
            Color stateColor = GetStateColor(_sdc.State);
            if (_stateText != null) { _stateText.text = $"STATE: {stateStr}"; _stateText.color = stateColor; }

            var nd = _sdc.Navdata;
            if (_altitudeText != null) _altitudeText.text = $"ALT: {nd.altitude:F1}m";

            float speed = Mathf.Sqrt(nd.vx * nd.vx + nd.vy * nd.vy + nd.vz * nd.vz);
            if (_speedText != null) _speedText.text = $"SPD: {speed:F1} m/s";

            float bat = nd.battery;
            if (_batteryText != null)
            {
                _batteryText.text = $"BAT: {bat:F0}%";
                _batteryText.color = bat < 20f ? Color.red : (bat < 50f ? Color.yellow : Color.green);
            }
        }

        // Scarer: F-key triggers + cooldown mask animates
        if (_scarer != null)
        {
            if (Input.GetKeyDown(KeyCode.F) && _scarer.IsReady)
            {
                _scarer.Activate();
                ShowMessage("🔆 Scarer aktiviert!", 1.5f);
            }

            if (_fireIndicator != null)
            {
                if (_scarer.IsActive)
                {
                    _fireIndicator.color = new Color(1f, 0.5f, 0f, 0.95f);
                    _fireText.text = ">>> AKTIV <<<";
                }
                else if (!_scarer.IsReady)
                {
                    _fireIndicator.color = new Color(0.4f, 0.4f, 0.4f, 0.85f);
                    _fireText.text = "Cooldown…";
                }
                else
                {
                    _fireIndicator.color = new Color(0.8f, 0.1f, 0.1f, 0.85f);
                    _fireText.text = "🔆 SCARER [F]";
                }
            }
            if (_fireCooldownMask != null)
                _fireCooldownMask.fillAmount = _scarer.CooldownFraction;
        }

        // Wolf-Fear-Bar
        if (_aiWolfFear != null)
        {
            float f = _aiWolfFear.Fear;
            if (_fearBarFill != null)
            {
                _fearBarFill.fillAmount = f;
                _fearBarFill.color = _aiWolfFear.IsPanicking
                    ? new Color(1f, 0.15f, 0.1f, 1f)
                    : Color.Lerp(new Color(1f, 0.85f, 0.2f, 1f), new Color(1f, 0.3f, 0.1f, 1f), f);
            }
            if (_fearLabel != null)
                _fearLabel.text = _aiWolfFear.IsPanicking ? "🐺 PANIK!" : $"Wolfsfurcht: {(f * 100):F0}%";
        }

        // Crosshair: green normal, orange when scarer active
        Color chColor = (_scarer != null && _scarer.IsActive)
            ? new Color(1f, 0.5f, 0f, 0.9f)
            : new Color(0f, 1f, 0.4f, 0.8f);
        if (_crosshairH != null) _crosshairH.color = chColor;
        if (_crosshairV != null) _crosshairV.color = chColor;
        if (_crosshairDot != null) _crosshairDot.color = chColor;

        // Message timer
        if (_messageTimer > 0)
        {
            _messageTimer -= Time.deltaTime;
            if (_messageTimer <= 0 && _messageText != null) _messageText.text = "";
        }
    }

    // ═══ Public API ═══

    public void ShowMessage(string msg, float duration = 2f)
    {
        if (_messageText != null) _messageText.text = msg;
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
