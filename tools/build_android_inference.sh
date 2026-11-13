#!/usr/bin/env bash
# Safely build a dedicated DroneFollow inference scene for Android.
# Without --build this only performs filesystem/tooling checks and never starts Unity.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
UNITY_VERSION="$(sed -n 's/^m_EditorVersion: //p' "$PROJECT_ROOT/ProjectSettings/ProjectVersion.txt" | head -n 1)"
SCENE=""
OUTPUT=""
BUILD=0

usage() {
  cat <<'EOF'
Usage:
  tools/build_android_inference.sh --scene Assets/Scenes/DroneFollowAndroid.unity [options]

Options:
  --output PATH   APK output path (default: Builds/DroneFollowAndroid.apk)
  --build         Explicitly launch Unity batchmode and build the APK
  --help          Show this help

Without --build, this is a read-only preflight. It does not launch Unity,
write under Assets/, or trigger a Unity script compilation.
EOF
}

while (($#)); do
  case "$1" in
    --scene)
      (($# >= 2)) || { echo "Missing value for --scene." >&2; exit 2; }
      SCENE="$2"
      shift 2
      ;;
    --output)
      (($# >= 2)) || { echo "Missing value for --output." >&2; exit 2; }
      OUTPUT="$2"
      shift 2
      ;;
    --build)
      BUILD=1
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ -z "$SCENE" ]]; then
  echo "Specify the separate Android inference scene with --scene." >&2
  usage >&2
  exit 2
fi

if [[ "$SCENE" = /* || "$SCENE" != Assets/*.unity || ! -f "$PROJECT_ROOT/$SCENE" ]]; then
  echo "Scene must be an existing Unity scene under Assets/: $SCENE" >&2
  exit 2
fi

if [[ -z "$OUTPUT" ]]; then
  OUTPUT="Builds/DroneFollowAndroid.apk"
fi
if [[ "$OUTPUT" != *.apk ]]; then
  echo "Output must end in .apk: $OUTPUT" >&2
  exit 2
fi
if [[ "$OUTPUT" != /* ]]; then
  OUTPUT="$PROJECT_ROOT/$OUTPUT"
fi

UNITY_EDITOR="${UNITY_EDITOR:-}"
if [[ -z "$UNITY_EDITOR" ]]; then
  candidate="$HOME/Unity/Hub/Editor/$UNITY_VERSION/Editor/Unity"
  if [[ -x "$candidate" ]]; then
    UNITY_EDITOR="$candidate"
  elif command -v Unity >/dev/null 2>&1; then
    UNITY_EDITOR="$(command -v Unity)"
  fi
fi

if [[ -z "$UNITY_EDITOR" || ! -x "$UNITY_EDITOR" ]]; then
  echo "Unity $UNITY_VERSION editor was not found. Set UNITY_EDITOR to its executable path." >&2
  exit 2
fi

# Unity Hub installations place platform modules under the editor's Data directory.
UNITY_DATA="$(dirname "$UNITY_EDITOR")/Data"
if [[ ! -d "$UNITY_DATA/PlaybackEngines/AndroidPlayer" ]]; then
  echo "Android Build Support is not installed for Unity $UNITY_VERSION." >&2
  echo "Install Android Build Support (SDK/NDK/OpenJDK) through Unity Hub before building." >&2
  exit 2
fi

echo "Unity:  $UNITY_EDITOR"
echo "Scene:  $SCENE"
echo "Output: $OUTPUT"
echo "Android Build Support: present"

if ((BUILD == 0)); then
  echo
  echo "Preflight only: no Unity process was started and no project files were changed."
  echo "When the dedicated scene and inference models are ready, stop training and run again with --build."
  exit 0
fi

while read -r pid; do
  [[ -n "$pid" ]] || continue
  args="$(ps -p "$pid" -o args= 2>/dev/null || true)"
  if [[ "$args" == *"$PROJECT_ROOT"* ]]; then
    echo "Refusing to build while a Unity editor/build process for this project is running." >&2
    echo "This prevents build/import activity from interrupting training. Close Unity first." >&2
    exit 3
  fi
done < <(ps -eo pid=,args= | awk '$0 ~ /[U]nity/ && $0 ~ /-projectpath/ {print $1}')

while read -r pid; do
  [[ -n "$pid" ]] || continue
  cwd="$(readlink -f "/proc/$pid/cwd" 2>/dev/null || true)"
  if [[ "$cwd" == "$PROJECT_ROOT" ]]; then
    echo "Refusing to build while an ML-Agents trainer for this project is running." >&2
    echo "Stop the trainer first so the Android build cannot disrupt training." >&2
    exit 3
  fi
done < <(ps -eo pid=,args= | awk '$0 ~ /[m]lagents-learn/ {print $1}')

HELPER="$PROJECT_ROOT/Assets/Editor/DroneFollowAndroidBuildTemporary.cs"
if [[ -e "$HELPER" || -e "$HELPER.meta" ]]; then
  echo "Temporary build helper already exists; refusing to overwrite it: $HELPER" >&2
  exit 3
fi
mkdir -p "$PROJECT_ROOT/Assets/Editor"
mkdir -p "$(dirname "$OUTPUT")"
LOG_FILE="$(mktemp "${TMPDIR:-/tmp}/dronefollow-android-build.XXXXXX.log")"

cleanup() {
  rm -f -- "$HELPER" "$HELPER.meta"
}
trap cleanup EXIT

cat > "$HELPER" <<'CS'
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;

public static class DroneFollowAndroidBuildTemporary
{
    public static void Build()
    {
        try
        {
            string scenePath = Environment.GetEnvironmentVariable("DRONEFOLLOW_ANDROID_SCENE");
            string outputPath = Environment.GetEnvironmentVariable("DRONEFOLLOW_ANDROID_OUTPUT");
            if (string.IsNullOrWhiteSpace(scenePath) || string.IsNullOrWhiteSpace(outputPath))
                throw new InvalidOperationException("Scene or output path was not supplied by the build script.");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
                throw new InvalidOperationException("Inference scene not found: " + scenePath);

            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            DroneFollowAgent[] agents = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<DroneFollowAgent>(true))
                .Where(agent => agent != null && agent.gameObject.activeInHierarchy)
                .ToArray();
            if (agents.Length != 1)
                throw new InvalidOperationException("The selected scene must contain exactly one active DroneFollowAgent; found " + agents.Length + ".");

            DroneFollowAgent agent = agents[0];
            BehaviorParameters behavior = agent.GetComponent<BehaviorParameters>();
            if (behavior == null || behavior.BehaviorName != DroneFollowContract.BehaviorName)
                throw new InvalidOperationException("The DroneFollowAgent needs matching BehaviorParameters.");
            if (behavior.BehaviorType != BehaviorType.InferenceOnly)
                throw new InvalidOperationException("Set Behavior Type to Inference Only in the dedicated Android scene.");
            if (behavior.Model == null)
                throw new InvalidOperationException("Assign the trained DroneFollow ONNX policy model in the dedicated scene.");

            DecisionRequester requester = agent.GetComponent<DecisionRequester>();
            if (requester == null || requester.Agent != agent)
                throw new InvalidOperationException("DecisionRequester is missing or bound to a different Agent.");

            RenderTextureSensorComponent depthSensor = agent.GetComponent<RenderTextureSensorComponent>();
            DroneDepthCamera depthCamera = agent.GetComponentInChildren<DroneDepthCamera>(true);
            if (depthSensor == null ||
                depthSensor.RenderTexture == null ||
                depthSensor.RenderTexture.width != DroneFollowContract.DepthObservationWidth ||
                depthSensor.RenderTexture.height != DroneFollowContract.DepthObservationHeight ||
                !depthSensor.Grayscale ||
                depthCamera == null ||
                depthCamera.outputTexture != depthSensor.RenderTexture)
                throw new InvalidOperationException("The 84x84 grayscale depth output and RenderTextureSensor must be connected on the DroneFollowAgent.");

            MonoBehaviour[] components = agent.GetComponents<MonoBehaviour>();
            if (!components.Any(component => component is IDroneFollowController))
                throw new InvalidOperationException("The scene has no IDroneFollowController implementation.");
            if (!components.Any(component => component is IDroneTargetDetectionSource))
                throw new InvalidOperationException("The scene has no IDroneTargetDetectionSource implementation (for example, the YOLO adapter).");

            var options = new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = outputPath,
                target = BuildTarget.Android,
                options = BuildOptions.None
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Android build result: " + report.summary.result + ". See the Unity build log for details.");

            Debug.Log("[DroneFollowAndroidBuild] APK created: " + report.summary.outputPath +
                      " (" + report.summary.totalSize + " bytes).");
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }
}
CS

echo "Starting isolated Unity batchmode Android build. Log: $LOG_FILE"
set +e
DRONEFOLLOW_ANDROID_SCENE="$SCENE" \
DRONEFOLLOW_ANDROID_OUTPUT="$OUTPUT" \
"$UNITY_EDITOR" \
  -batchmode \
  -quit \
  -projectPath "$PROJECT_ROOT" \
  -buildTarget Android \
  -executeMethod DroneFollowAndroidBuildTemporary.Build \
  -logFile "$LOG_FILE"
UNITY_STATUS=$?
set -e

if ((UNITY_STATUS != 0)); then
  echo "Android build failed (Unity exit code $UNITY_STATUS). Log: $LOG_FILE" >&2
  tail -n 80 "$LOG_FILE" >&2
  exit "$UNITY_STATUS"
fi

if [[ ! -s "$OUTPUT" ]]; then
  echo "Unity returned success, but the APK is missing or empty: $OUTPUT" >&2
  echo "Log: $LOG_FILE" >&2
  tail -n 80 "$LOG_FILE" >&2
  exit 1
fi

echo "Android inference APK ready: $OUTPUT"
echo "Build log: $LOG_FILE"
