using System;
using System.IO;
using NUnit.Framework;

public sealed class ArDroneProtocolTests
{
    [Test]
    public void RefConstantsMatchSdk()
    {
        Assert.AreEqual(290718208, ArDroneProtocol.RefTakeoff);
        Assert.AreEqual(290717696, ArDroneProtocol.RefLand);
        Assert.AreEqual(290717952, ArDroneProtocol.RefEmergencyToggle);
        Assert.AreEqual("AT*REF=7,290718208\r", ArDroneProtocol.Ref(7, ArDroneProtocol.RefTakeoff));
    }

    [Test]
    public void PcmdEncodesFloatsAsIeeeBits()
    {
        // SDK documentation example: -0.8 is sent as -1085485875.
        Assert.AreEqual(-1085485875, ArDroneProtocol.FloatBits(-0.8f));
        Assert.AreEqual(
            "AT*PCMD=3,1,1056964608,-1085485875,0,1065353216\r",
            ArDroneProtocol.Pcmd(3, 0.5f, -0.8f, 0f, 1f));
    }

    [Test]
    public void PcmdWithZeroInputsRequestsHover()
    {
        Assert.AreEqual("AT*PCMD=1,0,0,0,0,0\r", ArDroneProtocol.Pcmd(1, 0f, 0f, 0f, 0f));
    }

    [Test]
    public void PcmdClampsInputs()
    {
        Assert.AreEqual(
            ArDroneProtocol.Pcmd(1, 1f, -1f, 0.2f, 0f),
            ArDroneProtocol.Pcmd(1, 3f, -2f, 0.2f, 0f));
    }

    [Test]
    public void PolicyForwardTiltsNoseDownAndLeftTiltsLeft()
    {
        var (roll, pitch, gaz, yaw) = ArDroneProtocol.FromPolicyCommand(0.6f, 0.3f, -0.2f, 0.4f);
        Assert.AreEqual(-0.3f, roll, 1e-6f, "left > 0 must tilt left (negative roll)");
        Assert.AreEqual(-0.6f, pitch, 1e-6f, "forward > 0 must lower the nose (negative pitch)");
        Assert.AreEqual(-0.2f, gaz, 1e-6f);
        Assert.AreEqual(0.4f, yaw, 1e-6f, "turn > 0 is clockwise in both conventions");
    }

    [Test]
    public void ParsesDemoNavdata()
    {
        byte[] packet = BuildNavdata(
            droneState: ArDroneProtocol.StateFlyingMask | ArDroneProtocol.StateBatteryLowMask,
            controlState: 4,
            battery: 18,
            thetaMilliDeg: -5000f,
            phiMilliDeg: 2000f,
            psiMilliDeg: -90000f,
            altitudeMm: 1500,
            vx: 1000f,
            vy: -500f,
            vz: 0f);

        Assert.IsTrue(ArDroneProtocol.TryParseNavdata(packet, packet.Length, out ArDroneProtocol.Navdata navdata));
        Assert.AreEqual(ArDroneProtocol.ControlState.Hovering, navdata.ControlState);
        Assert.AreEqual(18, navdata.BatteryPercent);
        Assert.IsTrue(navdata.BatteryLow);
        Assert.IsFalse(navdata.Emergency);
        Assert.AreEqual(-5f, navdata.PitchDeg, 1e-4f);
        Assert.AreEqual(2f, navdata.RollDeg, 1e-4f);
        Assert.AreEqual(-90f, navdata.YawDeg, 1e-4f);
        Assert.AreEqual(1.5f, navdata.AltitudeM, 1e-4f);
        Assert.AreEqual(1f, navdata.VxMs, 1e-4f);
        Assert.AreEqual(-0.5f, navdata.VyMs, 1e-4f);
    }

    [Test]
    public void NavdataMapsToSimulatorConvention()
    {
        var raw = new ArDroneProtocol.Navdata
        {
            PitchDeg = -5f,
            RollDeg = 2f,
            YawDeg = -90f,
            AltitudeM = 1.5f,
            VxMs = 1f,
            VyMs = -0.5f,
            BatteryPercent = 80
        };
        var packet = new NavdataPacket();
        ArDroneProtocol.ToSimulatorConvention(raw, packet);

        Assert.AreEqual(5f, packet.rotY, 1e-4f, "nose down must be positive pitch like the simulator");
        Assert.AreEqual(2f, packet.rotX, 1e-4f);
        Assert.AreEqual(270f, packet.rotZ, 1e-4f);
        Assert.AreEqual(1.5f, packet.altitude, 1e-4f);
        Assert.AreEqual(1f, packet.vx, 1e-4f);
        Assert.AreEqual(-0.5f, packet.vy, 1e-4f);
        Assert.AreEqual(80f, packet.battery, 1e-4f);
    }

    [Test]
    public void RejectsBootstrapAndForeignPackets()
    {
        byte[] headerOnly = new byte[16];
        BitConverter.GetBytes(ArDroneProtocol.NavdataHeader).CopyTo(headerOnly, 0);
        Assert.IsFalse(ArDroneProtocol.TryParseNavdata(headerOnly, headerOnly.Length, out _));
        Assert.IsFalse(ArDroneProtocol.TryParseNavdata(new byte[64], 64, out _));
    }

    [Test]
    public void ParsesPaveHeader()
    {
        byte[] header = new byte[64];
        header[0] = (byte)'P';
        header[1] = (byte)'a';
        header[2] = (byte)'V';
        header[3] = (byte)'E';
        BitConverter.GetBytes((ushort)64).CopyTo(header, 6);
        BitConverter.GetBytes(12345).CopyTo(header, 8);
        BitConverter.GetBytes((ushort)640).CopyTo(header, 12);
        BitConverter.GetBytes((ushort)368).CopyTo(header, 14);
        BitConverter.GetBytes((ushort)640).CopyTo(header, 16);
        BitConverter.GetBytes((ushort)360).CopyTo(header, 18);
        BitConverter.GetBytes(42u).CopyTo(header, 20);
        header[30] = 1;

        Assert.IsTrue(ArDroneProtocol.TryParsePave(header, 0, header.Length, out ArDroneProtocol.PaveHeader pave));
        Assert.AreEqual(12345, pave.PayloadSize);
        Assert.AreEqual(368, pave.EncodedHeight);
        Assert.AreEqual(360, pave.DisplayHeight);
        Assert.AreEqual(42u, pave.FrameNumber);
        Assert.IsTrue(pave.IsKeyFrame);

        header[0] = (byte)'X';
        Assert.IsFalse(ArDroneProtocol.TryParsePave(header, 0, header.Length, out _));
    }

    static byte[] BuildNavdata(
        uint droneState, uint controlState, uint battery,
        float thetaMilliDeg, float phiMilliDeg, float psiMilliDeg,
        int altitudeMm, float vx, float vy, float vz)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(ArDroneProtocol.NavdataHeader);
        writer.Write(droneState);
        writer.Write(1u);   // sequence
        writer.Write(0u);   // vision flag
        writer.Write((ushort)0);    // demo option tag
        writer.Write((ushort)148);  // demo option size
        writer.Write(controlState << 16);
        writer.Write(battery);
        writer.Write(thetaMilliDeg);
        writer.Write(phiMilliDeg);
        writer.Write(psiMilliDeg);
        writer.Write(altitudeMm);
        writer.Write(vx);
        writer.Write(vy);
        writer.Write(vz);
        writer.Write(new byte[148 - 40]);
        writer.Write((ushort)0xFFFF);  // checksum option
        writer.Write((ushort)8);
        writer.Write(0u);
        return stream.ToArray();
    }
}
