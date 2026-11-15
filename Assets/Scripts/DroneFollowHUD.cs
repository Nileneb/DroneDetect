using UnityEngine;
using UnityEngine.UI;

/// <summary>On-screen alignment and curriculum status for DroneFollow training.</summary>
public sealed class DroneFollowHUD : MonoBehaviour
{
    static readonly string[] StageNames =
    {
        "Flacher Pfad, langsames Ziel",
        "Leichte Huegel",
        "Huegel + engere Kurven",
        "Voller Parcours, schnelleres Ziel"
    };

    const string MonitorCanvasName = "DroneFollowMainCameraCanvas";
    const string MainCameraContentPath = "Screen1_MainCamera/ImageArea/MainCameraContent";

    DroneFollowAgent _agent;
    Text _stageText;
    Text _taskText;
    Text _flightText;
    Text _alignmentText;
    Text _detectionText;
    readonly Text[] _stageRows = new Text[DroneFollowContract.CurriculumStages];

    public static DroneFollowHUD CreateHUD(DroneFollowAgent agent)
    {
        GameObject canvasObject = GameObject.Find(MonitorCanvasName);
        DroneFollowHUD existing = FindAnyObjectByType<DroneFollowHUD>();
        if (canvasObject == null && existing != null)
            canvasObject = existing.gameObject;
        if (canvasObject == null)
        {
            canvasObject = new GameObject(
                "DroneFollowHUD",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
        }

        if (existing != null && existing.gameObject != canvasObject)
            Destroy(existing.gameObject);

        Canvas canvas = canvasObject.GetComponent<Canvas>();
        if (canvas == null)
            canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 50;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        if (scaler == null)
            scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        if (canvasObject.GetComponent<GraphicRaycaster>() == null)
            canvasObject.AddComponent<GraphicRaycaster>();

        DroneFollowHUD hud = canvasObject.GetComponent<DroneFollowHUD>();
        if (hud == null)
            hud = canvasObject.AddComponent<DroneFollowHUD>();
        hud._agent = agent;
        Transform mainCameraContent = canvasObject.transform.Find(MainCameraContentPath);
        Transform uiRoot = mainCameraContent != null ? mainCameraContent : canvasObject.transform;
        if (!hud.BindExistingUI(uiRoot))
        {
            ClearHUD(uiRoot);
            hud.BuildUI(uiRoot);
        }
        return hud;
    }

    void Update()
    {
        if (_agent == null) return;

        int currentStage = Mathf.Clamp(_agent.CurrentCurriculumStage, 0, StageNames.Length - 1);
        int elapsedSeconds = Mathf.FloorToInt(
            _agent.CurrentEpisodeFlightDecisions / DroneFollowContract.DecisionRateHz);

        bool finalChallengeCompleted = _agent.FinalCurriculumStageCompleted;
        _stageText.text = finalChallengeCompleted
            ? "CURRICULUM  |  ALLE AUFGABEN ERLEDIGT"
            : $"CURRICULUM  |  STUFE {currentStage + 1}/{StageNames.Length}";
        _taskText.text = finalChallengeCompleted
            ? $"Abgeschlossen: {StageNames[currentStage]}"
            : $"Aktuell: {StageNames[currentStage]}";
        _flightText.text = finalChallengeCompleted
            ? "Letzte Flugzeit erfolgreich absolviert"
            : $"Flugzeit: {FormatTime(elapsedSeconds)}  |  Tore: {_agent.GatesPassedWell}/{_agent.GateCount}";
        _alignmentText.text = $"Ziel im Fadenkreuz: {_agent.TargetCenteringQuality:P0}";
        DroneFollowSentisPerception perception = _agent.perception;
        if (perception == null)
        {
            _detectionText.text = "YOLO: Quelle fehlt";
            _detectionText.color = new Color(1f, 0.35f, 0.35f);
        }
        else if (perception.DetectionFramesProcessed == 0)
        {
            _detectionText.text = "YOLO: warte auf erstes Ergebnis";
            _detectionText.color = new Color(1f, 0.72f, 0.35f);
        }
        else if (perception.Current.Visible)
        {
            _detectionText.text = $"YOLO: ZIEL ERKANNT ({perception.LastPersonScore:P0})";
            _detectionText.color = new Color(0.35f, 1f, 0.55f);
        }
        else
        {
            _detectionText.text = $"YOLO: kein Ziel ({perception.LastPersonScore:P0})";
            _detectionText.color = new Color(1f, 0.62f, 0.42f);
        }

        for (int i = 0; i < _stageRows.Length; i++)
        {
            bool completed = i < currentStage ||
                             (i == StageNames.Length - 1 && _agent.FinalCurriculumStageCompleted);
            bool active = i == currentStage && !completed;
            string marker = completed ? "✓" : active ? "▶" : "○";
            _stageRows[i].text = $"{marker}  {StageNames[i]}";
            _stageRows[i].color = completed
                ? new Color(0.35f, 1f, 0.55f)
                : active ? new Color(1f, 0.82f, 0.3f) : new Color(0.72f, 0.76f, 0.82f);
        }
    }

    void BuildUI(Transform parent)
    {
        BuildCrosshair(parent);

        GameObject panel = new GameObject("CurriculumPanel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(parent, false);
        Image background = panel.GetComponent<Image>();
        background.color = new Color(0.025f, 0.04f, 0.055f, 0.82f);
        background.raycastTarget = false;

        RectTransform panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = Vector2.zero;
        panelRect.anchorMax = Vector2.zero;
        panelRect.pivot = Vector2.zero;
        panelRect.anchoredPosition = new Vector2(20f, 20f);
        panelRect.sizeDelta = new Vector2(470f, 258f);

        _stageText = CreateText(panel.transform, "CurriculumStage", 19, new Vector2(12f, -8f), 28f);
        _stageText.color = new Color(0.6f, 0.9f, 1f);
        _taskText = CreateText(panel.transform, "CurrentTask", 15, new Vector2(12f, -38f), 24f);
        _flightText = CreateText(panel.transform, "FlightProgress", 15, new Vector2(12f, -62f), 22f);
        _alignmentText = CreateText(panel.transform, "TargetAlignment", 15, new Vector2(12f, -85f), 22f);
        _detectionText = CreateText(panel.transform, "HumanDetection", 15, new Vector2(12f, -108f), 22f);

        for (int i = 0; i < _stageRows.Length; i++)
            _stageRows[i] = CreateText(panel.transform, $"Stage{i}", 14, new Vector2(12f, -135f - i * 21f), 20f);
    }

    bool BindExistingUI(Transform uiRoot)
    {
        Transform panel = uiRoot.Find("CurriculumPanel");
        if (panel == null)
            return false;

        _stageText = panel.Find("CurriculumStage")?.GetComponent<Text>();
        _taskText = panel.Find("CurrentTask")?.GetComponent<Text>();
        _flightText = panel.Find("FlightProgress")?.GetComponent<Text>();
        _alignmentText = panel.Find("TargetAlignment")?.GetComponent<Text>();
        _detectionText = panel.Find("HumanDetection")?.GetComponent<Text>();
        for (int i = 0; i < _stageRows.Length; i++)
            _stageRows[i] = panel.Find($"Stage{i}")?.GetComponent<Text>();

        if (_stageText == null || _taskText == null || _flightText == null ||
            _alignmentText == null || _detectionText == null)
            return false;

        for (int i = 0; i < _stageRows.Length; i++)
        {
            if (_stageRows[i] == null)
                return false;
        }

        return true;
    }

    void BuildCrosshair(Transform parent)
    {
        Color color = new Color(0.35f, 1f, 0.65f, 0.92f);
        CreateCrosshairPart(parent, "CrosshairLeft", color, new Vector2(8f, 2f), new Vector2(-14f, 0f));
        CreateCrosshairPart(parent, "CrosshairRight", color, new Vector2(8f, 2f), new Vector2(14f, 0f));
        CreateCrosshairPart(parent, "CrosshairTop", color, new Vector2(2f, 8f), new Vector2(0f, 14f));
        CreateCrosshairPart(parent, "CrosshairBottom", color, new Vector2(2f, 8f), new Vector2(0f, -14f));
        CreateCrosshairPart(parent, "CrosshairCenter", Color.white, new Vector2(3f, 3f), Vector2.zero);
    }

    static void CreateCrosshairPart(Transform parent, string name, Color color, Vector2 size, Vector2 position)
    {
        GameObject part = new GameObject(name, typeof(RectTransform), typeof(Image));
        part.transform.SetParent(parent, false);
        Image image = part.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;

        RectTransform rect = part.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = position;
    }

    static Text CreateText(Transform parent, string name, int fontSize, Vector2 position, float height)
    {
        GameObject textObject = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(Shadow));
        textObject.transform.SetParent(parent, false);

        Text text = textObject.GetComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = fontSize;
        text.alignment = TextAnchor.MiddleLeft;
        text.color = Color.white;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Truncate;

        Shadow shadow = textObject.GetComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.9f);
        shadow.effectDistance = new Vector2(1f, -1f);

        RectTransform rect = text.rectTransform;
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = position;
        rect.sizeDelta = new Vector2(-24f, height);
        return text;
    }

    static void ClearHUD(Transform uiRoot)
    {
        string[] names =
        {
            "CurriculumPanel",
            "CrosshairLeft",
            "CrosshairRight",
            "CrosshairTop",
            "CrosshairBottom",
            "CrosshairCenter"
        };
        foreach (string name in names)
        {
            Transform child = uiRoot.Find(name);
            if (child != null)
                Destroy(child.gameObject);
        }
    }

    static string FormatTime(int totalSeconds)
    {
        return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
    }
}
