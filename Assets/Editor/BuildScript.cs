using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using System.IO;

public static class BuildScript
{
    // app.linn.games public root — WebGL artifacts land directly under public/shepherd/Build/
    const string DeployRoot = "/home/nileneb/Desktop/WebDev/app.linn.games/public/shepherd";

    [MenuItem("Build/WebGL ShepherdArena (Deploy)")]
    public static void BuildWebGLDeploy()
    {
        ConfigurePlayerSettings();

        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/ShepherdArena.unity" },
            locationPathName = DeployRoot,
            target = BuildTarget.WebGL,
            options = BuildOptions.None,
        };

        Debug.Log($"[BuildScript] Building WebGL → {DeployRoot}");
        var report = BuildPipeline.BuildPlayer(options);

        var s = report.summary;
        if (s.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            Debug.Log($"[BuildScript] OK ({s.totalSize / (1024 * 1024)} MB, {s.totalTime})");
        else
            Debug.LogError($"[BuildScript] FAILED: {s.totalErrors} errors");
    }

    [MenuItem("Build/WebGL ShepherdArena (Local)")]
    public static void BuildWebGLLocal()
    {
        ConfigurePlayerSettings();

        var outputPath = Path.Combine(Application.dataPath, "../Build/WebGL/ShepherdArena");
        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/ShepherdArena.unity" },
            locationPathName = outputPath,
            target = BuildTarget.WebGL,
            options = BuildOptions.None,
        };

        Debug.Log($"[BuildScript] Building WebGL (local) → {outputPath}");
        var report = BuildPipeline.BuildPlayer(options);
        Debug.Log($"[BuildScript] Result: {report.summary.result}");
    }

    static void ConfigurePlayerSettings()
    {
        PlayerSettings.productName = "ShepherdArena";
        PlayerSettings.companyName = "DroneDetect";

        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
        PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.None;
        PlayerSettings.WebGL.dataCaching = true;
        PlayerSettings.WebGL.threadsSupport = false;
        PlayerSettings.WebGL.decompressionFallback = false;
        PlayerSettings.WebGL.linkerTarget = WebGLLinkerTarget.Wasm;

        PlayerSettings.stripEngineCode = true;
        PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.WebGL, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.Low);
    }
}
