using UnityEditor;
using UnityEngine;
using System.IO;

public static class BuildScript
{
    [MenuItem("Build/WebGL ShepherdArena")]
    public static void BuildWebGL()
    {
        var outputPath = Path.Combine(Application.dataPath, "../Build/WebGL/ShepherdArena");

        var options = new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/ShepherdArena.unity" },
            locationPathName = outputPath,
            target = BuildTarget.WebGL,
            options = BuildOptions.None
        };

        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
        PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.None;
        PlayerSettings.WebGL.dataCaching = true;
        PlayerSettings.stripEngineCode = true;

        var report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
            Debug.Log($"[BuildScript] WebGL Build OK → {outputPath}");
        else
            Debug.LogError($"[BuildScript] Build FAILED: {report.summary.totalErrors} errors");
    }
}
