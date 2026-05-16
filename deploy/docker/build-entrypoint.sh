#!/usr/bin/env bash
# Entrypoint for the unity-builder container.
# Copies project to a writable scratch dir (Unity needs to write Library/),
# then runs the BuildScript.BuildWebGLDeploy method.
set -euo pipefail

PROJECT_RO="${PROJECT_RO:-/project}"
OUTPUT="${BUILD_OUTPUT:-/output}"
SCENE="${BUILD_SCENE:-Assets/Scenes/ShepherdArena.unity}"
METHOD="BuildScript.BuildWebGLDeploy"
LICENSE_FILE="${UNITY_LICENSE_FILE:-/secrets/unity.ulf}"

# Activate Unity license from mounted secret
if [[ -f "$LICENSE_FILE" ]]; then
  /opt/unity/Editor/Unity -batchmode -nographics -quit \
    -manualLicenseFile "$LICENSE_FILE" || true
fi

# Mirror project into writable scratch (Unity rewrites Library/ + Logs/)
SCRATCH=/tmp/project
rsync -a --delete \
  --exclude='Library/' --exclude='Logs/' --exclude='Temp/' \
  --exclude='UserSettings/' --exclude='obj/' \
  "$PROJECT_RO/" "$SCRATCH/"

cd "$SCRATCH"

# Build
/opt/unity/Editor/Unity \
  -batchmode -nographics -quit \
  -projectPath "$SCRATCH" \
  -buildTarget WebGL \
  -executeMethod "$METHOD" \
  -logFile /dev/stdout

# Copy artifacts to mounted output volume (which is the swarm NFS mount)
BUILD_DIR="$SCRATCH/$(basename "$OUTPUT")"
if [[ -d "$BUILD_DIR/Build" ]]; then
  rsync -a --delete "$BUILD_DIR/" "$OUTPUT/"
  echo "Build copied to $OUTPUT"
else
  echo "Build directory not found at $BUILD_DIR" >&2
  exit 1
fi
