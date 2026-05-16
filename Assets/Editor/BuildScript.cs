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

    static readonly string[] DefaultScenes = new[] {
        "Assets/Scenes/MainMenu.unity",
        "Assets/Scenes/ShepherdArena.unity",
    };

    [MenuItem("Build/Linux Standalone ShepherdArena (Deploy)")]
    public static void BuildLinuxStandaloneDeploy()
    {
        ConfigureCommonPlayerSettings();

        var outputPath = Path.Combine(Application.dataPath, "../" + DeployRootLinux, "ShepherdArena.x86_64");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        var options = new BuildPlayerOptions
        {
            scenes = new[] {
                "Assets/Scenes/MainMenu.unity",       // start screen with role select
                "Assets/Scenes/ShepherdArena.unity",  // play scene
            },
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
            scenes = new[] {
                "Assets/Scenes/MainMenu.unity",       // start screen with role select
                "Assets/Scenes/ShepherdArena.unity",  // play scene
            },
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
            scenes = new[] {
                "Assets/Scenes/MainMenu.unity",       // start screen with role select
                "Assets/Scenes/ShepherdArena.unity",  // play scene
            },
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

    // ───────────────────────────────────────────────────────────────────────
    // Cloud entrypoint — called via `-executeMethod BuildScript.CloudBuild`.
    // game-ci/unity-builder passes `-customBuildPath` + `-buildTarget`.
    // WHY (issue: cloud-cross-platform): Linux runner produces Windows-IL2CPP
    //   and macOS-Mono artifacts; one method covers both targets.
    // ───────────────────────────────────────────────────────────────────────
    public static void CloudBuild()
    {
        var args = System.Environment.GetCommandLineArgs();
        BuildTarget target = BuildTarget.StandaloneWindows64;
        string buildPath = "build/ShepherdArena";

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-buildTarget" && i + 1 < args.Length)
            {
                if (!System.Enum.TryParse(args[i + 1], true, out target))
                    target = BuildTarget.StandaloneWindows64;
            }
            else if (args[i] == "-customBuildPath" && i + 1 < args.Length)
            {
                buildPath = args[i + 1];
            }
        }

        ConfigureCommonPlayerSettings();

        // Force Mono for macOS cross-compile (Linux runner cannot IL2CPP→OSX).
        // Windows stays IL2CPP (Linux runner has windows-il2cpp module).
        var named = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target));
        if (target == BuildTarget.StandaloneOSX)
            PlayerSettings.SetScriptingBackend(named, ScriptingImplementation.Mono2x);
        else if (target == BuildTarget.StandaloneWindows64)
            PlayerSettings.SetScriptingBackend(named, ScriptingImplementation.IL2CPP);

        Directory.CreateDirectory(Path.GetDirectoryName(buildPath));

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = DefaultScenes,
            locationPathName = buildPath,
            target = target,
            options = BuildOptions.None,
        });

        var s = report.summary;
        if (s.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            Debug.LogError($"[CloudBuild] FAILED ({target}): {s.totalErrors} errors");
            EditorApplication.Exit(1);
            return;
        }
        Debug.Log($"[CloudBuild] OK {target} → {buildPath} ({s.totalSize / (1024 * 1024)} MB, {s.totalTime})");
        EditorApplication.Exit(0);
    }

    [MenuItem("Build/Windows Standalone ShepherdArena (Local)")]
    public static void BuildWindowsStandaloneLocal()
    {
        ConfigureCommonPlayerSettings();
        var outputPath = Path.Combine(Application.dataPath, "../Build/Windows/ShepherdArena/ShepherdArena.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = DefaultScenes,
            locationPathName = outputPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        });
        Debug.Log($"[BuildScript] Windows: {report.summary.result}");
    }

    [MenuItem("Build/macOS Standalone ShepherdArena (Local)")]
    public static void BuildMacStandaloneLocal()
    {
        ConfigureCommonPlayerSettings();
        var outputPath = Path.Combine(Application.dataPath, "../Build/macOS/ShepherdArena.app");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = DefaultScenes,
            locationPathName = outputPath,
            target = BuildTarget.StandaloneOSX,
            options = BuildOptions.None,
        });
        Debug.Log($"[BuildScript] macOS: {report.summary.result}");
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
