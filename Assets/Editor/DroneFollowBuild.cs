using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Batch-mode build entry points:
///   Unity -batchmode -quit -projectPath . -executeMethod DroneFollowBuild.BuildTrainingLinux
///   Unity -batchmode -quit -projectPath . -executeMethod DroneFollowBuild.BuildAndroidGroundStation
/// </summary>
public static class DroneFollowBuild
{
    const string TrainingScenePath = "Assets/Scenes/Agent1.unity";
    const string TrainingOutputPath = "Builds/Training/DroneFollow.x86_64";
    const string AndroidOutputPath = "Builds/Android/DroneFollowGroundStation.apk";

    [MenuItem("DroneDetect/Build/Linux Training Environment")]
    public static void BuildTrainingLinux()
    {
        RunBuild(() =>
        {
            EditorSceneManager.OpenScene(TrainingScenePath, OpenSceneMode.Single);
            if (!DroneFollowSetup.ConfigureActiveScene())
                throw new InvalidOperationException("Agent1 could not be configured for DroneFollow; see the errors above.");

            // WHY(training-speed): a windowed player at full resolution renders the scenery for nothing;
            // the policy only sees the 640x360 camera RenderTexture.
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 640;
            PlayerSettings.defaultScreenHeight = 360;
            PlayerSettings.runInBackground = true;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);

            return Build(new[] { TrainingScenePath }, TrainingOutputPath, BuildTarget.StandaloneLinux64);
        });
    }

    [MenuItem("DroneDetect/Build/Android Ground Station APK")]
    public static void BuildAndroidGroundStation()
    {
        RunBuild(() =>
        {
            if (!DroneFollowGroundStationSetup.CreateScene())
                throw new InvalidOperationException("The ground-station scene could not be generated; see the errors above.");

            PlayerSettings.productName = "DroneFollow Ground Station";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.dronedetect.groundstation");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            // Sockets to the drone need the INTERNET permission even though no internet is used.
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.runInBackground = false;
            // WHY(sentis): GPUCompute inference needs compute shaders, which Vulkan provides on current phones.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });

            return Build(new[] { DroneFollowGroundStationSetup.ScenePath }, AndroidOutputPath, BuildTarget.Android);
        });
    }

    static BuildReport Build(string[] scenes, string outputPath, BuildTarget target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        return BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = target,
            options = BuildOptions.None
        });
    }

    static void RunBuild(Func<BuildReport> build)
    {
        int exitCode = 1;
        try
        {
            BuildReport report = build();
            BuildSummary summary = report.summary;
            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[DroneFollowBuild] {summary.platform} build ready: {summary.outputPath} ({summary.totalSize / (1024 * 1024)} MB, {summary.totalTime}).");
                exitCode = 0;
            }
            else
            {
                Debug.LogError($"[DroneFollowBuild] {summary.platform} build {summary.result} with {summary.totalErrors} error(s).");
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }

        if (Application.isBatchMode)
            EditorApplication.Exit(exitCode);
    }
}
