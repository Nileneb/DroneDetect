using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using System.IO;

public static class BuildScript
{
    // app.linn.games public root — WebGL artifacts land directly under public/shepherd/Build/
    const string DeployRootWebGL = "/home/nileneb/Desktop/WebDev/app.linn.games/public/shepherd";

    // Linux Standalone — robust path, runs anywhere, used as ZIP-download fallback to WebGL
    const string DeployRootLinux = "Build/Linux/ShepherdArena";

    [MenuItem("Build/Linux Standalone ShepherdArena (Deploy)")]
    public static void BuildLinuxStandaloneDeploy()
    {
        ConfigureCommonPlayerSettings();

        var outputPath = Path.Combine(Application.dataPath, "../" + DeployRootLinux, "ShepherdArena.x86_64");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/ShepherdArena.unity" },
            locationPathName = outputPath,
            target = BuildTarget.StandaloneLinux64,
            options = BuildOptions.None,
        };

        Debug.Log($"[BuildScript] Building Linux Standalone → {outputPath}");
        var report = BuildPipeline.BuildPlayer(options);

        var s = report.summary;
        if (s.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            Debug.Log($"[BuildScript] OK ({s.totalSize / (1024 * 1024)} MB, {s.totalTime})");
        else
            Debug.LogError($"[BuildScript] FAILED: {s.totalErrors} errors");
    }

    [MenuItem("Build/WebGL ShepherdArena (Deploy)")]
    public static void BuildWebGLDeploy()
    {
        ConfigureCommonPlayerSettings();
        ConfigureWebGLPlayerSettings();

        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/ShepherdArena.unity" },
            locationPathName = DeployRootWebGL,
            target = BuildTarget.WebGL,
            options = BuildOptions.None,
        };

        Debug.Log($"[BuildScript] Building WebGL → {DeployRootWebGL}");
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
        ConfigureCommonPlayerSettings();
        ConfigureWebGLPlayerSettings();

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

    static void ConfigureCommonPlayerSettings()
    {
        PlayerSettings.productName = "ShepherdArena";
        PlayerSettings.companyName = "DroneDetect";
        PlayerSettings.stripEngineCode = true;
    }

    static void ConfigureWebGLPlayerSettings()
    {
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
        PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.None;
        PlayerSettings.WebGL.dataCaching = true;
        PlayerSettings.WebGL.threadsSupport = false;
        PlayerSettings.WebGL.decompressionFallback = false;
        PlayerSettings.WebGL.linkerTarget = WebGLLinkerTarget.Wasm;

        PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.WebGL, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.Low);
    }
}
