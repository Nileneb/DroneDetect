#!/usr/bin/env bash
# Headless WebGL build via Unity batchmode. Use when Unity Editor is NOT running.
# (When the Editor is open with the project, this will fail with a license lock error —
# in that case trigger the build from Editor menu: Build > WebGL ShepherdArena (Deploy).)
set -euo pipefail

PROJECT="${PROJECT:-/home/nileneb/DroneDetect}"
UNITY="${UNITY_BIN:-/home/nileneb/Unity/Hub/Editor/6000.6.0a3/Editor/Unity}"
LOG=/tmp/dronedetect_webgl_build_$(date +%Y%m%d_%H%M%S).log

if [[ ! -x "$UNITY" ]]; then
  echo "Unity binary not found at $UNITY" >&2
  exit 1
fi

# Bail if Editor is already running on this project — Unity will refuse to open twice.
if pgrep -f "Editor/Unity -projectpath $PROJECT" >/dev/null 2>&1; then
  echo "Unity Editor is currently open on $PROJECT — batchmode build would deadlock." >&2
  echo "Trigger the build from inside the Editor: Build > WebGL ShepherdArena (Deploy)" >&2
  exit 2
fi

echo "Building WebGL → /home/nileneb/Desktop/WebDev/app.linn.games/public/shepherd/"
echo "Log: $LOG"

"$UNITY" \
  -batchmode -nographics -quit \
  -projectPath "$PROJECT" \
  -buildTarget WebGL \
  -executeMethod BuildScript.BuildWebGLDeploy \
  -logFile "$LOG"

EXIT=$?
echo "Unity exited with code $EXIT"
echo "Last 20 log lines:"
tail -20 "$LOG"
exit $EXIT
