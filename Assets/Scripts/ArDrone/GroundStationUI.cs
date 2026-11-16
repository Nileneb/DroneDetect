using UnityEngine;

/// <summary>
/// Phone ground-station screen: full-screen drone video with the target box,
/// MiDaS depth picture-in-picture, telemetry, and large flight buttons.
/// Tapping a person in the video locks the follow target.
/// Immediate-mode GUI keeps the whole screen in one self-contained script.
/// </summary>
public sealed class GroundStationUI : MonoBehaviour
{
    public ArDroneFollowController drone;
    public DroneFollowSentisPerception perception;
    public DroneFollowAgent agent;

    GUIStyle _label;
    GUIStyle _button;
    Texture2D _boxTexture;
    Texture2D _personTexture;
    bool _wasRecording;
    float _recordingSince;
    Texture2D _panelTexture;

    void Awake()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        _boxTexture = SolidTexture(new Color(0.1f, 1f, 0.3f, 1f));
        _personTexture = SolidTexture(new Color(1f, 0.85f, 0.1f, 1f));
        _panelTexture = SolidTexture(new Color(0f, 0f, 0f, 0.55f));
    }

    void OnDestroy()
    {
        Destroy(_boxTexture);
        Destroy(_personTexture);
        Destroy(_panelTexture);
    }

    static Texture2D SolidTexture(Color color)
    {
        var texture = new Texture2D(1, 1);
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }

    void OnGUI()
    {
        float unit = Mathf.Max(24f, Screen.height / 22f);
        _label ??= new GUIStyle(GUI.skin.label) { normal = { textColor = Color.white } };
        _button ??= new GUIStyle(GUI.skin.button);
        _label.fontSize = Mathf.RoundToInt(unit * 0.55f);
        _button.fontSize = Mathf.RoundToInt(unit * 0.7f);

        Rect video = VideoRect();
        if (perception != null && perception.rgbInput != null)
            GUI.DrawTexture(video, perception.rgbInput, ScaleMode.StretchToFill, false);
        DrawTargetBox(video);
        DrawDepth(unit);
        DrawTelemetry(unit);
        DrawBanners(unit);
        DrawButtons(unit);
        HandleTap(video);
    }

    static Rect VideoRect()
    {
        float aspect = DroneFollowContract.CameraWidth / (float)DroneFollowContract.CameraHeight;
        float width = Mathf.Min(Screen.width, Screen.height * aspect);
        float height = width / aspect;
        return new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
    }

    void DrawTargetBox(Rect video)
    {
        if (perception == null)
            return;

        // All persons thin yellow, the locked target thick green. Viewport boxes have a bottom-left origin.
        var boxes = perception.PersonBoxes;
        for (int i = 0; i < boxes.Count; i++)
        {
            bool target = i == perception.TargetBoxIndex;
            DrawFrame(ToGui(video, boxes[i]), target ? _boxTexture : _personTexture, target ? 5f : 2f);
            if (target)
                GUI.Label(new Rect(video.x + boxes[i].x * video.width, video.y + (1f - boxes[i].yMax) * video.height - _label.fontSize * 1.6f, 300f, _label.fontSize * 1.6f), "TARGET", _label);
        }
    }

    static Rect ToGui(Rect video, Rect box) => new Rect(
        video.x + box.x * video.width,
        video.y + (1f - box.yMax) * video.height,
        box.width * video.width,
        box.height * video.height);

    static void DrawFrame(Rect rect, Texture2D texture, float thickness)
    {
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), texture);
        GUI.DrawTexture(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), texture);
        GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), texture);
        GUI.DrawTexture(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), texture);
    }

    void DrawDepth(float unit)
    {
        if (perception == null || perception.depthOutput == null)
            return;
        float size = unit * 5f;
        GUI.DrawTexture(new Rect(Screen.width - size - unit * 0.3f, unit * 0.3f, size, size), perception.depthOutput);
    }

    void DrawTelemetry(float unit)
    {
        if (drone == null)
            return;

        NavdataPacket nav = drone.Navdata;
        Vector4 policy = drone.PolicyCommand;
        string tracking = perception == null ? "-"
            : !perception.Tracker.HasLock ? $"Tap the person to follow ({perception.PeopleInView} in view, best score {perception.LastPersonScore:F2})"
            : perception.Current.Visible ? $"Target locked · match {perception.Tracker.LastAppearanceMatch:P0} · {perception.Tracker.GallerySize} views"
            : $"Target locked · not visible (best person score {perception.LastPersonScore:F2})";
        string text =
            $"{(drone.behaviour != null ? $"[{drone.behaviour.State}] " : "")}{drone.SafetyStatus}\n" +
            $"State {drone.State} · Battery {nav.battery:F0}% · Alt {nav.altitude:F2} m · Video frames {drone.VideoFramesReceived}\n" +
            $"{tracking}\n" +
            $"Policy  fwd {policy.x:+0.00;-0.00}  left {policy.y:+0.00;-0.00}  up {policy.z:+0.00;-0.00}  turn {policy.w:+0.00;-0.00}";
        var panel = new Rect(unit * 0.3f, unit * 0.3f, Screen.width * 0.6f, unit * 3.2f);
        GUI.DrawTexture(panel, _panelTexture);
        GUI.Label(new Rect(panel.x + unit * 0.3f, panel.y + unit * 0.15f, panel.width, panel.height), text, _label);
    }

    void DrawBanners(float unit)
    {
        if (drone == null)
            return;

        // Shadow mode looks exactly like a normal flight (take-off works, the drone hovers), so say it loudly.
        float y = unit * 3.9f;
        if (drone.shadowMode)
            DrawBanner(unit, ref y, "SHADOW – KI STEUERT NICHT (Drohne hovert nur)", new Color(0.9f, 0.45f, 0f, 0.9f));
        if (drone.BatteryLow)
            DrawBanner(unit, ref y, $"AKKU {drone.Navdata.battery:F0}% – Notlandung bei {drone.landBelowBatteryPercent}%", new Color(0.85f, 0.1f, 0.1f, 0.9f));
    }

    void DrawBanner(float unit, ref float y, string text, Color color)
    {
        Color previous = GUI.color;
        GUI.color = color;
        var rect = new Rect(unit * 0.3f, y, Screen.width * 0.6f, unit * 1.3f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(rect.x + unit * 0.3f, rect.y + unit * 0.2f, rect.width, rect.height), text, _label);
        GUI.color = previous;
        y += unit * 1.4f;
    }

    void DrawButtons(float unit)
    {
        if (drone == null)
            return;

        float height = unit * 2f;
        float width = Screen.width / 10f - unit * 0.4f;
        float y = Screen.height - height - unit * 0.4f;
        int column = 0;
        Rect Next() => new Rect(unit * 0.3f + column++ * (width + unit * 0.4f), y, width, height);

        var behaviour = drone.behaviour;
        if (GUI.Button(Next(), "TAKE OFF", _button))
        {
            if (behaviour != null) behaviour.Request(OperatorCommand.Takeoff);
            else drone.Takeoff();
        }
        if (behaviour != null)
        {
            if (GUI.Button(Next(), "SEARCH", _button)) behaviour.Request(OperatorCommand.Search);
            if (GUI.Button(Next(), "FOLLOW", _button)) behaviour.Request(OperatorCommand.Follow);
            if (GUI.Button(Next(), "HOVER", _button)) behaviour.Request(OperatorCommand.Hover);
        }
        if (GUI.Button(Next(), "LAND", _button))
        {
            // Land is always sent directly too: the operator must be able to land even if the machine thinks it is idle.
            behaviour?.Request(OperatorCommand.Land);
            drone.Land();
        }
        if (GUI.Button(Next(), drone.shadowMode ? "SHADOW: ON" : "SHADOW: OFF", _button))
            drone.shadowMode = !drone.shadowMode;
        if (GUI.Button(Next(), "UNLOCK", _button) && perception != null)
            perception.Tracker.Reset();

        if (GUI.Button(Next(), drone.BottomCameraActive ? "CAM: BOTTOM" : "CAM: FRONT", _button))
            drone.SetBottomCamera(!drone.BottomCameraActive);

        bool recording = ScreenRecorder.IsRecording;
        if (recording && !_wasRecording)
            _recordingSince = Time.realtimeSinceStartup;
        _wasRecording = recording;
        Color previous = GUI.backgroundColor;
        GUI.backgroundColor = recording && (int)(Time.realtimeSinceStartup * 2f) % 2 == 0 ? Color.red : previous;
        string recLabel = recording ? $"STOP {(int)(Time.realtimeSinceStartup - _recordingSince) / 60:00}:{(int)(Time.realtimeSinceStartup - _recordingSince) % 60:00}" : "REC";
        if (GUI.Button(Next(), recLabel, _button))
        {
            if (recording) ScreenRecorder.Stop();
            else ScreenRecorder.Start();
        }

        GUI.backgroundColor = Color.red;
        if (GUI.Button(Next(), "EMERGENCY", _button))
            drone.EmergencyReset();
        GUI.backgroundColor = previous;
    }

    void HandleTap(Rect video)
    {
        Event current = Event.current;
        if (perception == null || current.type != EventType.MouseUp || !video.Contains(current.mousePosition))
            return;

        Vector2 normalized = new Vector2(
            (current.mousePosition.x - video.x) / video.width,
            1f - (current.mousePosition.y - video.y) / video.height);
        perception.RequestTargetLock(normalized);
        current.Use();
    }
}
