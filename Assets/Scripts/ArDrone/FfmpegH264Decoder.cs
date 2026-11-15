#if UNITY_EDITOR || UNITY_STANDALONE
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

/// <summary>
/// Desktop decoder: pipes the H.264 stream through an ffmpeg process and reads
/// raw RGBA frames back. Used to test the full ground-station pipeline from a
/// laptop connected to the drone's Wi-Fi before building for Android.
/// </summary>
public sealed class FfmpegH264Decoder : IH264Decoder
{
    readonly int _width;
    readonly int _height;
    readonly Process _process;
    readonly Stream _input;
    readonly Thread _readerThread;
    readonly object _lock = new object();
    readonly byte[] _latest;
    bool _hasNewFrame;
    volatile bool _running = true;

    public FfmpegH264Decoder(int width, int height, string ffmpegPath = "ffmpeg")
    {
        _width = width;
        _height = height;
        _latest = new byte[width * height * 4];
        // vflip: Unity textures are bottom-up. crop: the drone encodes 640x368 but displays 640x360.
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = "-loglevel error -fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0 " +
                        "-f h264 -i pipe:0 " +
                        $"-vf crop={width}:{height}:0:0,vflip -pix_fmt rgba -f rawvideo pipe:1",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        _process = Process.Start(startInfo) ??
                   throw new InvalidOperationException("[FfmpegH264Decoder] Could not start ffmpeg; is it installed and on PATH?");
        _process.ErrorDataReceived += (_, line) =>
        {
            if (!string.IsNullOrEmpty(line.Data))
                UnityEngine.Debug.LogWarning("[FfmpegH264Decoder] " + line.Data);
        };
        _process.BeginErrorReadLine();
        _input = _process.StandardInput.BaseStream;
        _readerThread = new Thread(ReadFrames) { IsBackground = true, Name = "ffmpeg frames" };
        _readerThread.Start();
    }

    public void Submit(byte[] data, int length, bool keyFrame)
    {
        if (!_running)
            return;
        _input.Write(data, 0, length);
        _input.Flush();
    }

    void ReadFrames()
    {
        Stream output = _process.StandardOutput.BaseStream;
        byte[] frame = new byte[_width * _height * 4];
        while (_running)
        {
            int filled = 0;
            while (filled < frame.Length)
            {
                int read = output.Read(frame, filled, frame.Length - filled);
                if (read <= 0)
                {
                    if (_running)
                        UnityEngine.Debug.LogError("[FfmpegH264Decoder] ffmpeg exited; video decoding stopped.");
                    return;
                }
                filled += read;
            }

            lock (_lock)
            {
                Buffer.BlockCopy(frame, 0, _latest, 0, frame.Length);
                _hasNewFrame = true;
            }
        }
    }

    public bool TryGetLatestFrame(byte[] rgba)
    {
        lock (_lock)
        {
            if (!_hasNewFrame)
                return false;
            Buffer.BlockCopy(_latest, 0, rgba, 0, _latest.Length);
            _hasNewFrame = false;
            return true;
        }
    }

    public void Dispose()
    {
        _running = false;
        _input.Close();
        if (!_process.WaitForExit(500))
            _process.Kill();
        _process.Dispose();
    }
}
#endif
