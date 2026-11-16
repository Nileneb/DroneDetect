using System;
using System.Globalization;

/// <summary>
/// Wire format of the Parrot AR.Drone 2.0 SDK: AT command strings (UDP 5556),
/// navdata packets (UDP 5554) and PaVE video frame headers (TCP 5555).
/// Pure functions only, so they are covered by EditMode tests without hardware.
/// </summary>
public static class ArDroneProtocol
{
    public const string DefaultAddress = "192.168.1.1";
    public const int NavdataPort = 5554;
    public const int VideoPort = 5555;
    public const int AtPort = 5556;

    // AT*REF bit field: bits 18, 20, 22, 24 and 28 must always be set.
    public const int RefBase = 290717696;
    public const int RefTakeoff = RefBase | (1 << 9);
    public const int RefLand = RefBase;
    public const int RefEmergencyToggle = RefBase | (1 << 8);

    public const uint NavdataHeader = 0x55667788;
    const ushort NavdataDemoTag = 0;
    const ushort NavdataChecksumTag = 0xFFFF;

    public const uint StateFlyingMask = 1u << 0;
    public const uint StateCommandAckMask = 1u << 6;
    public const uint StateNavdataDemoMask = 1u << 10;
    public const uint StateNavdataBootstrapMask = 1u << 11;
    public const uint StateBatteryLowMask = 1u << 15;
    public const uint StateComWatchdogMask = 1u << 30;
    public const uint StateEmergencyMask = 1u << 31;

    static readonly string[] StateBitNames =
    {
        "FLY", "VIDEO", "VISION", "CONTROL_ALGO", "ALTITUDE_CTRL", "USER_FEEDBACK", "CMD_ACK", "CAMERA",
        "TRAVELLING", "USB", "NAVDATA_DEMO", "NAVDATA_BOOTSTRAP", "MOTORS_BRUSHED", "COM_LOST", "GYROS_ZERO", "VBAT_LOW",
        "VBAT_HIGH", "TIMER_ELAPSED", "NOT_ENOUGH_POWER", "ANGLES_OUT_OF_RANGE", "WIND", "ULTRASOUND_ERR", "CUTOUT", "PIC_VERSION_OK",
        "ATCODEC_THREAD", "NAVDATA_THREAD", "VIDEO_THREAD", "ACQ_THREAD", "CTRL_WATCHDOG", "ADC_WATCHDOG", "COM_WATCHDOG", "EMERGENCY"
    };

    /// <summary>Readable names of the SDK's ardrone_state bits that matter for diagnosing a crash.</summary>
    public static string DescribeState(uint state)
    {
        // Bits that are normally set in flight (threads running, demo mode, ...) would drown out the faults.
        const uint noise = (1u << 1) | (1u << 2) | (1u << 3) | (1u << 4) | (1u << 6) | (1u << 7) | (1u << 10) |
                           (1u << 23) | (1u << 24) | (1u << 25) | (1u << 26) | (1u << 27);
        var names = new System.Text.StringBuilder();
        for (int bit = 0; bit < 32; bit++)
        {
            if ((state & (1u << bit)) == 0 || (noise & (1u << bit)) != 0)
                continue;
            if (names.Length > 0)
                names.Append('|');
            names.Append(StateBitNames[bit]);
        }
        return names.Length == 0 ? "-" : names.ToString();
    }

    public static string Ref(int sequence, int value) =>
        string.Format(CultureInfo.InvariantCulture, "AT*REF={0},{1}\r", sequence, value);

    /// <summary>
    /// Progressive command; all inputs in [-1, 1] as fractions of the configured maxima.
    /// roll &gt; 0 tilts right, pitch &gt; 0 tilts the nose up (flies backwards),
    /// gaz &gt; 0 climbs, yaw &gt; 0 turns clockwise seen from above.
    /// All-zero inputs send the hover flag instead, which engages the drone's own position hold.
    /// </summary>
    public static string Pcmd(int sequence, float roll, float pitch, float gaz, float yaw)
    {
        bool hover = roll == 0f && pitch == 0f && gaz == 0f && yaw == 0f;
        return string.Format(
            CultureInfo.InvariantCulture,
            "AT*PCMD={0},{1},{2},{3},{4},{5}\r",
            sequence,
            hover ? 0 : 1,
            FloatBits(Clamp(roll)),
            FloatBits(Clamp(pitch)),
            FloatBits(Clamp(gaz)),
            FloatBits(Clamp(yaw)));
    }

    /// <summary>
    /// Policy/simulator command (forward, left, up, turn clockwise) to AR.Drone PCMD axes.
    /// Forward flight needs the nose down (negative pitch), flying left a left tilt (negative roll).
    /// </summary>
    public static (float roll, float pitch, float gaz, float yaw) FromPolicyCommand(
        float forward, float left, float up, float turn) =>
        (-left, -forward, up, turn);

    /// <summary>
    /// Drone attitude/velocity in the simulator conventions of IDroneController.Navdata:
    /// rotY + = nose down (the drone's theta is + = nose up), rotX + = right side down, rotZ in [0, 360).
    /// </summary>
    public static void ToSimulatorConvention(in Navdata raw, NavdataPacket target)
    {
        target.altitude = raw.AltitudeM;
        target.vx = raw.VxMs;
        target.vy = raw.VyMs;
        target.rotX = raw.RollDeg;
        target.rotY = -raw.PitchDeg;
        target.rotZ = raw.YawDeg - 360f * (float)Math.Floor(raw.YawDeg / 360f);
        target.battery = raw.BatteryPercent;
    }

    public static string FlatTrim(int sequence) =>
        string.Format(CultureInfo.InvariantCulture, "AT*FTRIM={0}\r", sequence);

    public static string Config(int sequence, string key, string value) =>
        string.Format(CultureInfo.InvariantCulture, "AT*CONFIG={0},\"{1}\",\"{2}\"\r", sequence, key, value);

    public static string Control(int sequence, int mode) =>
        string.Format(CultureInfo.InvariantCulture, "AT*CTRL={0},{1},0\r", sequence, mode);

    public static string ResetWatchdog(int sequence) =>
        string.Format(CultureInfo.InvariantCulture, "AT*COMWDG={0}\r", sequence);

    /// <summary>The SDK transmits floats as the signed integer with the same IEEE-754 bits.</summary>
    public static int FloatBits(float value) => BitConverter.SingleToInt32Bits(value);

    static float Clamp(float value) => value < -1f ? -1f : value > 1f ? 1f : value;

    /// <summary>Decoded "navdata_demo" option plus the header state word.</summary>
    public struct Navdata
    {
        public uint DroneState;
        public uint Sequence;
        public ControlState ControlState;
        public int BatteryPercent;
        public float PitchDeg;   // theta, + = nose up
        public float RollDeg;    // phi, + = right side down
        public float YawDeg;     // psi, + = clockwise seen from above
        public float AltitudeM;
        public float VxMs;       // body frame, + = forward
        public float VyMs;       // body frame, + = right
        public float VzMs;

        public bool Emergency => (DroneState & StateEmergencyMask) != 0;
        public bool BatteryLow => (DroneState & StateBatteryLowMask) != 0;
        public bool CommandAck => (DroneState & StateCommandAckMask) != 0;
        public bool Bootstrap => (DroneState & StateNavdataBootstrapMask) != 0;
    }

    public enum ControlState
    {
        Default = 0,
        Init = 1,
        Landed = 2,
        Flying = 3,
        Hovering = 4,
        Test = 5,
        TakingOff = 6,
        GotoFix = 7,
        Landing = 8,
        Looping = 9
    }

    /// <summary>
    /// Parses a navdata datagram. Returns false for malformed packets and for
    /// bootstrap packets that carry only the header (no demo option yet).
    /// </summary>
    public static bool TryParseNavdata(byte[] packet, int length, out Navdata navdata)
    {
        navdata = default;
        if (packet == null || length < 16 || ReadUInt32(packet, 0) != NavdataHeader)
            return false;

        navdata.DroneState = ReadUInt32(packet, 4);
        navdata.Sequence = ReadUInt32(packet, 8);
        bool hasDemo = false;
        int offset = 16;
        while (offset + 4 <= length)
        {
            ushort tag = ReadUInt16(packet, offset);
            ushort size = ReadUInt16(packet, offset + 2);
            if (size < 4 || offset + size > length)
                return false;

            if (tag == NavdataDemoTag && size >= 4 + 36)
            {
                int o = offset + 4;
                navdata.ControlState = (ControlState)(ReadUInt32(packet, o) >> 16);
                navdata.BatteryPercent = (int)ReadUInt32(packet, o + 4);
                navdata.PitchDeg = ReadSingle(packet, o + 8) / 1000f;
                navdata.RollDeg = ReadSingle(packet, o + 12) / 1000f;
                navdata.YawDeg = ReadSingle(packet, o + 16) / 1000f;
                navdata.AltitudeM = (int)ReadUInt32(packet, o + 20) / 1000f;
                navdata.VxMs = ReadSingle(packet, o + 24) / 1000f;
                navdata.VyMs = ReadSingle(packet, o + 28) / 1000f;
                navdata.VzMs = ReadSingle(packet, o + 32) / 1000f;
                hasDemo = true;
            }
            else if (tag == NavdataChecksumTag)
            {
                break;
            }

            offset += size;
        }

        return hasDemo;
    }

    /// <summary>Header of one PaVE-wrapped H.264 frame on the video TCP stream.</summary>
    public struct PaveHeader
    {
        public int HeaderSize;
        public int PayloadSize;
        public int EncodedWidth;
        public int EncodedHeight;
        public int DisplayWidth;
        public int DisplayHeight;
        public uint FrameNumber;
        public int FrameType;

        public bool IsKeyFrame => FrameType == 1 || FrameType == 2;
    }

    public const int PaveMinimumHeaderSize = 64;

    public static bool TryParsePave(byte[] buffer, int offset, int available, out PaveHeader header)
    {
        header = default;
        if (available < PaveMinimumHeaderSize ||
            buffer[offset] != (byte)'P' || buffer[offset + 1] != (byte)'a' ||
            buffer[offset + 2] != (byte)'V' || buffer[offset + 3] != (byte)'E')
            return false;

        header.HeaderSize = ReadUInt16(buffer, offset + 6);
        header.PayloadSize = (int)ReadUInt32(buffer, offset + 8);
        header.EncodedWidth = ReadUInt16(buffer, offset + 12);
        header.EncodedHeight = ReadUInt16(buffer, offset + 14);
        header.DisplayWidth = ReadUInt16(buffer, offset + 16);
        header.DisplayHeight = ReadUInt16(buffer, offset + 18);
        header.FrameNumber = ReadUInt32(buffer, offset + 20);
        header.FrameType = buffer[offset + 30];
        return header.HeaderSize >= PaveMinimumHeaderSize && header.PayloadSize > 0;
    }

    static ushort ReadUInt16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));

    static uint ReadUInt32(byte[] b, int o) =>
        (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    static float ReadSingle(byte[] b, int o) => BitConverter.Int32BitsToSingle((int)ReadUInt32(b, o));
}
