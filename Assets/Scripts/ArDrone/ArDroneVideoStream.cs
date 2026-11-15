using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

/// <summary>
/// Reads the AR.Drone 2.0 front-camera stream (TCP 5555), strips the PaVE
/// headers and forwards each H.264 access unit to a decoder. Reconnects on
/// errors and waits for the next key frame before feeding the decoder again.
/// </summary>
public sealed class ArDroneVideoStream : IDisposable
{
    const int ReconnectDelayMs = 500;

    readonly string _address;
    readonly IH264Decoder _decoder;
    Thread _thread;
    volatile bool _running;
    TcpClient _client;

    public ArDroneVideoStream(IH264Decoder decoder, string address = ArDroneProtocol.DefaultAddress)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _address = address;
    }

    public int FramesReceived { get; private set; }

    public void Start()
    {
        if (_running)
            return;
        _running = true;
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "ArDrone video" };
        _thread.Start();
    }

    void ReadLoop()
    {
        while (_running)
        {
            try
            {
                using (_client = new TcpClient())
                {
                    _client.Connect(_address, ArDroneProtocol.VideoPort);
                    _client.ReceiveTimeout = 2000;
                    ReadFrames(_client.GetStream());
                }
            }
            catch (Exception exception) when (exception is IOException || exception is SocketException)
            {
                if (_running)
                    UnityEngine.Debug.LogWarning($"[ArDroneVideoStream] Stream interrupted ({exception.Message}); reconnecting.");
            }
            catch (ObjectDisposedException) when (!_running)
            {
                return;
            }

            if (_running)
                Thread.Sleep(ReconnectDelayMs);
        }
    }

    void ReadFrames(NetworkStream stream)
    {
        byte[] header = new byte[ArDroneProtocol.PaveMinimumHeaderSize];
        byte[] payload = new byte[256 * 1024];
        bool waitingForKeyFrame = true;
        bool headerBuffered = false;
        while (_running)
        {
            if (!headerBuffered)
                ReadExactly(stream, header, 0, header.Length);
            headerBuffered = false;
            if (!ArDroneProtocol.TryParsePave(header, 0, header.Length, out ArDroneProtocol.PaveHeader pave))
            {
                Resynchronize(stream, header);
                headerBuffered = true;
                continue;
            }

            if (pave.HeaderSize > header.Length)
                Skip(stream, pave.HeaderSize - header.Length);
            if (pave.PayloadSize > payload.Length)
                payload = new byte[pave.PayloadSize];
            ReadExactly(stream, payload, 0, pave.PayloadSize);
            FramesReceived++;

            // P-frames without their reference frame decode to garbage; skip them until the next I-frame.
            if (waitingForKeyFrame && !pave.IsKeyFrame)
                continue;
            waitingForKeyFrame = false;
            _decoder.Submit(payload, pave.PayloadSize, pave.IsKeyFrame);
        }
    }

    /// <summary>Slides at least one byte, then until the buffer starts with the next "PaVE" signature.</summary>
    static void Resynchronize(NetworkStream stream, byte[] header)
    {
        UnityEngine.Debug.LogWarning("[ArDroneVideoStream] Lost PaVE framing; resynchronizing.");
        do
        {
            Buffer.BlockCopy(header, 1, header, 0, header.Length - 1);
            ReadExactly(stream, header, header.Length - 1, 1);
        }
        while (!(header[0] == 'P' && header[1] == 'a' && header[2] == 'V' && header[3] == 'E'));
    }

    static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int read = stream.Read(buffer, offset, count);
            if (read <= 0)
                throw new IOException("Video stream closed by the drone.");
            offset += read;
            count -= read;
        }
    }

    static void Skip(Stream stream, int count)
    {
        byte[] scratch = new byte[count];
        ReadExactly(stream, scratch, 0, count);
    }

    public void Dispose()
    {
        _running = false;
        _client?.Close();
        _thread?.Join(500);
    }
}

/// <summary>H.264 decoder that turns access units into RGBA frames for Unity.</summary>
public interface IH264Decoder : IDisposable
{
    /// <summary>Called from the network thread with one complete Annex-B access unit.</summary>
    void Submit(byte[] data, int length, bool keyFrame);

    /// <summary>
    /// Main thread: copies the newest decoded frame (bottom-up RGBA32, display size) into
    /// <paramref name="rgba"/>; returns false if no frame arrived since the last call.
    /// </summary>
    bool TryGetLatestFrame(byte[] rgba);
}
