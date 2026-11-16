using UnityEngine;

/// <summary>
/// Real Parrot AR.Drone 2.0 behind <see cref="IDroneController"/>: forwards the
/// policy's commands over Wi-Fi, maps navdata into the simulator's conventions
/// and feeds the decoded front-camera video into the Sentis perception.
///
/// Safety envelope (it only stops or lands, it never steers):
/// shadow mode (policy runs, nothing is sent), hover when the target has been
/// lost, altitude ceiling, land on low battery, lost link or app pause.
/// </summary>
public sealed class ArDroneFollowController : MonoBehaviour, IDroneController
{
    public string droneAddress = ArDroneProtocol.DefaultAddress;
    public DroneFollowSentisPerception perception;

    [Header("Safety")]
    [Tooltip("Policy runs and its commands are displayed, but the drone only hovers.")]
    public bool shadowMode = true;
    [Min(0.1f)] public float hoverAfterTargetLostSeconds = 1.5f;
    [Min(0.5f)] public float maxAltitudeMeters = 2.5f;
    [Range(0, 100)] public int landBelowBatteryPercent = 15;
    [Min(0.2f)] public float landAfterLinkLostSeconds = 1f;

    ArDroneLink _link;
    ArDroneVideoStream _video;
    IH264Decoder _decoder;
    Texture2D _frameTexture;
    byte[] _frameBuffer;
    readonly NavdataPacket _navdata = new NavdataPacket();
    DroneState _state = DroneState.Landed;
    float _lastLinkTime;
    float _lastTargetTime;
    float _previousAltitude;
    float _previousAltitudeTime;
    bool _landingForSafety;

    public DroneState State => _state;
    public NavdataPacket Navdata => _navdata;
    public bool IsReady => _link != null && _link.IsConnected && _state != DroneState.Emergency;
    public bool IsLinkUp => _link != null && _link.IsConnected;
    public int VideoFramesReceived => _video?.FramesReceived ?? 0;
    public Vector4 PolicyCommand { get; private set; }
    public Vector4 SentCommand { get; private set; }
    public string SafetyStatus { get; private set; } = "Not connected";

    /// <summary>True while the bottom camera is streamed (experimental): perception and policy are paused.</summary>
    public bool BottomCameraActive { get; private set; }

    public void SetBottomCamera(bool bottom)
    {
        if (_link == null || bottom == BottomCameraActive)
            return;
        BottomCameraActive = bottom;
        _link.SetVideoChannel(bottom);
        perception?.ResetDetection();
        Hover();
    }

    void OnEnable()
    {
        int width = DroneFollowContract.CameraWidth;
        int height = DroneFollowContract.CameraHeight;
        _frameBuffer = new byte[width * height * 4];
        _frameTexture = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = "ArDroneVideo" };

#if UNITY_ANDROID && !UNITY_EDITOR
        bool wifiBound = false;
        try { wifiBound = AndroidH264Decoder.BindProcessToWifi(); }
        catch (System.Exception exception) { Debug.LogException(exception); }
        if (!wifiBound)
            Debug.LogError("[ArDroneFollowController] Could not bind to Wi-Fi; connect the phone to the drone's ardrone2_xxxx network.");
        _decoder = new AndroidH264Decoder(width, height);
#else
        _decoder = new FfmpegH264Decoder(width, height);
#endif
        _link = new ArDroneLink(droneAddress);
        _link.Start();
        _video = new ArDroneVideoStream(_decoder, droneAddress);
        _video.Start();
        _lastLinkTime = Time.time;
        _lastTargetTime = Time.time;
    }

    void OnDisable()
    {
        _video?.Dispose();
        _link?.Dispose();
        _decoder?.Dispose();
        _video = null;
        _link = null;
        _decoder = null;
        if (_frameTexture != null)
            Destroy(_frameTexture);
    }

    // WHY(safety): leaving the app (call, home button) must never leave the drone flying without a pilot.
    void OnApplicationPause(bool paused)
    {
        if (paused)
            Land();
    }

    void Update()
    {
        if (_link == null)
            return;

        UpdateNavdata();
        PushVideoFrame();
        EnforceSafety();
    }

    void UpdateNavdata()
    {
        if (!_link.TryGetNavdata(out ArDroneProtocol.Navdata raw))
            return;

        if (_link.IsConnected)
            _lastLinkTime = Time.time;

        ArDroneProtocol.ToSimulatorConvention(raw, _navdata);
        // WHY(ardrone, vz): demo navdata reports vz as 0 on most firmware, so climb rate comes from altitude.
        float dt = Time.time - _previousAltitudeTime;
        if (dt > 0.05f)
        {
            _navdata.vz = (raw.AltitudeM - _previousAltitude) / dt;
            _previousAltitude = raw.AltitudeM;
            _previousAltitudeTime = Time.time;
        }
        _navdata.timestamp = Time.time;
        _state = MapState(raw);
        _navdata.state = (int)_state;
    }

    static DroneState MapState(ArDroneProtocol.Navdata raw)
    {
        if (raw.Emergency)
            return DroneState.Emergency;

        switch (raw.ControlState)
        {
            case ArDroneProtocol.ControlState.TakingOff:
                return DroneState.TakingOff;
            case ArDroneProtocol.ControlState.Hovering:
                return DroneState.Hovering;
            case ArDroneProtocol.ControlState.Flying:
            case ArDroneProtocol.ControlState.GotoFix:
            case ArDroneProtocol.ControlState.Looping:
                return DroneState.Flying;
            case ArDroneProtocol.ControlState.Landing:
                return DroneState.Landing;
            default:
                return DroneState.Landed;
        }
    }

    void PushVideoFrame()
    {
        if (perception == null || !_decoder.TryGetLatestFrame(_frameBuffer))
            return;

        _frameTexture.LoadRawTextureData(_frameBuffer);
        _frameTexture.Apply(false, false);
        // The bottom view is for the operator only: YOLO/OSNet/MiDaS and the policy were trained on the front view.
        if (BottomCameraActive)
        {
            Graphics.Blit(_frameTexture, perception.rgbInput);
            return;
        }
        perception.SubmitFrame(_frameTexture);
    }

    public Texture LatestFrame => _frameTexture;

    void EnforceSafety()
    {
        bool airborne = _state == DroneState.Hovering || _state == DroneState.Flying;
        if (perception != null && perception.Current.Visible)
            _lastTargetTime = Time.time;

        if (airborne && Time.time - _lastLinkTime > landAfterLinkLostSeconds)
        {
            SafetyStatus = "Link lost: landing";
            Land();
        }
        else if (airborne && _navdata.battery > 0 && _navdata.battery < landBelowBatteryPercent)
        {
            SafetyStatus = $"Battery {_navdata.battery:F0}%: landing";
            Land();
        }
        else if (!_landingForSafety)
        {
            SafetyStatus = !IsLinkUp ? "Not connected"
                : shadowMode ? "Shadow mode (commands not sent)"
                : Time.time - _lastTargetTime > hoverAfterTargetLostSeconds ? "Target lost: hovering"
                : "Following";
        }
    }

    public void Takeoff()
    {
        if (_link == null || _state != DroneState.Landed)
            return;
        _landingForSafety = false;
        _lastTargetTime = Time.time;
        _link.Takeoff();
    }

    public void Land()
    {
        if (_link == null)
            return;
        _landingForSafety = true;
        SentCommand = Vector4.zero;
        _link.Land();
    }

    public void EmergencyReset()
    {
        _link?.ToggleEmergency();
        SentCommand = Vector4.zero;
    }

    public void Hover()
    {
        SentCommand = Vector4.zero;
        _link?.SetCommand(0f, 0f, 0f, 0f);
    }

    /// <summary>Policy output in the simulator convention: forward, left, up, turn (clockwise) in [-1, 1].</summary>
    public void Move(float forward, float left, float up, float turn)
    {
        PolicyCommand = new Vector4(forward, left, up, turn);
        if (_link == null || _landingForSafety)
            return;
        if (BottomCameraActive)
        {
            Hover();
            return;
        }

        bool targetLost = Time.time - _lastTargetTime > hoverAfterTargetLostSeconds;
        if (shadowMode || targetLost)
        {
            Hover();
            return;
        }

        if (_navdata.altitude >= maxAltitudeMeters)
            up = Mathf.Min(up, 0f);

        SentCommand = new Vector4(forward, left, up, turn);
        var (roll, pitch, gaz, yaw) = ArDroneProtocol.FromPolicyCommand(forward, left, up, turn);
        _link.SetCommand(roll, pitch, gaz, yaw);
    }
}
