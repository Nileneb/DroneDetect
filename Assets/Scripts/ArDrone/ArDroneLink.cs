using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

/// <summary>
/// Control and telemetry link to an AR.Drone 2.0: a 30 Hz AT command loop
/// (UDP 5556) and the navdata receiver (UDP 5554). Runs on background threads;
/// the latest navdata and the current flight command are exchanged under a lock.
/// </summary>
public sealed class ArDroneLink : IDisposable
{
    // WHY(ardrone, watchdog): the drone hovers after ~50 ms without AT traffic and reports a
    // communication watchdog after 2 s, so commands are re-sent continuously at 30 Hz.
    const int CommandIntervalMs = 33;
    const int NavdataTimeoutMs = 1000;

    readonly IPAddress _address;
    readonly ConcurrentQueue<string> _oneShotCommands = new ConcurrentQueue<string>();
    readonly object _lock = new object();
    UdpClient _atSocket;
    UdpClient _navSocket;
    Thread _commandThread;
    Thread _navdataThread;
    volatile bool _running;
    int _sequence;

    float _roll, _pitch, _gaz, _yaw;
    int _refValue = ArDroneProtocol.RefLand;
    ArDroneProtocol.Navdata _navdata;
    bool _hasNavdata;
    DateTime _lastNavdataUtc = DateTime.MinValue;

    public ArDroneLink(string address = ArDroneProtocol.DefaultAddress)
    {
        _address = IPAddress.Parse(address);
    }

    public bool IsConnected => _hasNavdata && (DateTime.UtcNow - _lastNavdataUtc).TotalMilliseconds < NavdataTimeoutMs;

    public bool TryGetNavdata(out ArDroneProtocol.Navdata navdata)
    {
        lock (_lock)
        {
            navdata = _navdata;
            return _hasNavdata;
        }
    }

    public void Start()
    {
        if (_running)
            return;

        _atSocket = new UdpClient();
        _atSocket.Connect(new IPEndPoint(_address, ArDroneProtocol.AtPort));
        _navSocket = new UdpClient(ArDroneProtocol.NavdataPort);
        _navSocket.Client.ReceiveTimeout = 500;
        _running = true;

        // Navdata handshake: a datagram to 5554 starts bootstrap navdata; demo mode then streams the
        // demo option (~15 Hz), and AT*CTRL acknowledges the configuration change.
        Enqueue(ArDroneProtocol.Config(NextSequence(), "general:navdata_demo", "TRUE"));
        Enqueue(ArDroneProtocol.Control(NextSequence(), 5));
        // WHY(sim-to-real, limits): the simulator twin was trained with these exact maxima.
        Enqueue(ArDroneProtocol.Config(NextSequence(), "control:euler_angle_max", "0.21"));
        Enqueue(ArDroneProtocol.Config(NextSequence(), "control:control_vz_max", "700"));
        Enqueue(ArDroneProtocol.Config(NextSequence(), "control:control_yaw", "1.75"));
        Enqueue(ArDroneProtocol.Config(NextSequence(), "control:altitude_max", "3000"));
        Enqueue(ArDroneProtocol.Config(NextSequence(), "video:video_codec", "129"));

        _navdataThread = new Thread(NavdataLoop) { IsBackground = true, Name = "ArDrone navdata" };
        _commandThread = new Thread(CommandLoop) { IsBackground = true, Name = "ArDrone AT" };
        _navdataThread.Start();
        _commandThread.Start();
    }

    public void SetCommand(float roll, float pitch, float gaz, float yaw)
    {
        lock (_lock)
        {
            _roll = roll;
            _pitch = pitch;
            _gaz = gaz;
            _yaw = yaw;
        }
    }

    public void Takeoff()
    {
        Enqueue(ArDroneProtocol.FlatTrim(NextSequence()));
        lock (_lock)
            _refValue = ArDroneProtocol.RefTakeoff;
    }

    public void Land()
    {
        SetCommand(0f, 0f, 0f, 0f);
        lock (_lock)
            _refValue = ArDroneProtocol.RefLand;
    }

    /// <summary>Cuts the motors immediately (or clears a latched emergency after a crash).</summary>
    public void ToggleEmergency()
    {
        SetCommand(0f, 0f, 0f, 0f);
        lock (_lock)
            _refValue = ArDroneProtocol.RefLand;
        Enqueue(ArDroneProtocol.Ref(NextSequence(), ArDroneProtocol.RefEmergencyToggle));
    }

    void Enqueue(string command) => _oneShotCommands.Enqueue(command);

    int NextSequence() => Interlocked.Increment(ref _sequence);

    void CommandLoop()
    {
        var builder = new StringBuilder(256);
        while (_running)
        {
            builder.Clear();
            while (_oneShotCommands.TryDequeue(out string command))
                builder.Append(command);

            float roll, pitch, gaz, yaw;
            int refValue;
            lock (_lock)
            {
                roll = _roll;
                pitch = _pitch;
                gaz = _gaz;
                yaw = _yaw;
                refValue = _refValue;
            }

            builder.Append(ArDroneProtocol.Ref(NextSequence(), refValue));
            builder.Append(ArDroneProtocol.Pcmd(NextSequence(), roll, pitch, gaz, yaw));
            builder.Append(ArDroneProtocol.ResetWatchdog(NextSequence()));
            byte[] datagram = Encoding.ASCII.GetBytes(builder.ToString());
            try
            {
                _atSocket.Send(datagram, datagram.Length);
            }
            catch (SocketException exception) when (_running)
            {
                UnityEngine.Debug.LogWarning($"[ArDroneLink] AT send failed: {exception.SocketErrorCode}");
            }

            Thread.Sleep(CommandIntervalMs);
        }
    }

    void NavdataLoop()
    {
        var droneEndpoint = new IPEndPoint(_address, ArDroneProtocol.NavdataPort);
        byte[] wakeUp = { 1, 0, 0, 0 };
        while (_running)
        {
            if (!IsConnected)
                SendSafely(_navSocket, wakeUp, droneEndpoint);

            byte[] packet;
            try
            {
                IPEndPoint sender = null;
                packet = _navSocket.Receive(ref sender);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            catch (Exception exception) when (!_running && (exception is ObjectDisposedException || exception is SocketException))
            {
                return;
            }

            if (!ArDroneProtocol.TryParseNavdata(packet, packet.Length, out ArDroneProtocol.Navdata navdata))
            {
                // Header-only bootstrap packets mean demo mode was not applied yet; ask again.
                if (packet.Length >= 16 && !_hasNavdata)
                    Enqueue(ArDroneProtocol.Config(NextSequence(), "general:navdata_demo", "TRUE"));
                continue;
            }

            lock (_lock)
            {
                _navdata = navdata;
                _hasNavdata = true;
                _lastNavdataUtc = DateTime.UtcNow;
            }

            if (navdata.CommandAck)
                Enqueue(ArDroneProtocol.Control(NextSequence(), 5));
        }
    }

    static void SendSafely(UdpClient socket, byte[] datagram, IPEndPoint endpoint)
    {
        try
        {
            socket.Send(datagram, datagram.Length, endpoint);
        }
        catch (SocketException exception)
        {
            UnityEngine.Debug.LogWarning($"[ArDroneLink] Navdata wake-up failed: {exception.SocketErrorCode}");
        }
    }

    public void Dispose()
    {
        if (!_running)
            return;

        // Last words to the drone: land, then stop the loops.
        _running = false;
        try
        {
            byte[] land = Encoding.ASCII.GetBytes(ArDroneProtocol.Ref(NextSequence(), ArDroneProtocol.RefLand));
            _atSocket?.Send(land, land.Length);
        }
        catch (SocketException exception)
        {
            // The drone still lands by itself once its AT watchdog expires.
            UnityEngine.Debug.LogWarning($"[ArDroneLink] Final land command failed: {exception.SocketErrorCode}");
        }

        _commandThread?.Join(200);
        _atSocket?.Close();
        _navSocket?.Close();
        _navdataThread?.Join(600);
    }
}
