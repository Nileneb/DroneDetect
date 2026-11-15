#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;

/// <summary>Hardware decoder on Android, backed by Assets/Plugins/Android/ArDroneDecoder.java.</summary>
public sealed class AndroidH264Decoder : IH264Decoder
{
    const string JavaClass = "dronedetect.ardrone.ArDroneDecoder";

    readonly AndroidJavaObject _decoder;
    [ThreadStatic] static bool _threadAttached;

    public AndroidH264Decoder(int width, int height)
    {
        _decoder = new AndroidJavaObject(JavaClass, width, height);
    }

    /// <summary>Routes all sockets of this app over Wi-Fi even though the drone network has no internet.</summary>
    public static bool BindProcessToWifi()
    {
        using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        using AndroidJavaObject activity = player.GetStatic<AndroidJavaObject>("currentActivity");
        using var decoderClass = new AndroidJavaClass(JavaClass);
        return decoderClass.CallStatic<bool>("bindProcessToWifi", activity);
    }

    public void Submit(byte[] data, int length, bool keyFrame)
    {
        // Submit runs on the video thread, which the JVM must know before any JNI call.
        if (!_threadAttached)
        {
            AndroidJNI.AttachCurrentThread();
            _threadAttached = true;
        }

        var payload = new sbyte[length];
        Buffer.BlockCopy(data, 0, payload, 0, length);
        _decoder.Call("submit", payload, length, keyFrame);
    }

    public bool TryGetLatestFrame(byte[] rgba)
    {
        sbyte[] frame = _decoder.Call<sbyte[]>("latestRgba");
        if (frame == null)
            return false;
        Buffer.BlockCopy(frame, 0, rgba, 0, Math.Min(frame.Length, rgba.Length));
        return true;
    }

    public void Dispose()
    {
        _decoder.Call("release");
        _decoder.Dispose();
    }
}
#endif
