#!/usr/bin/env bash
# Packs the Linux Standalone build into a zip and copies to app.linn.games public/.
# Run AFTER Unity has finished `Build > Linux Standalone ShepherdArena (Deploy)`.
set -euo pipefail

BUILD_DIR=/home/nileneb/DroneDetect/Build/Linux/ShepherdArena
DEPLOY_DIR=/home/nileneb/Desktop/WebDev/app.linn.games/public/shepherd/downloads
ZIP_NAME=shepherd-linux-x64.zip

if [[ ! -f "$BUILD_DIR/ShepherdArena.x86_64" ]]; then
  echo "Build not found at $BUILD_DIR/ShepherdArena.x86_64" >&2
  echo "Run Unity > Build > Linux Standalone ShepherdArena (Deploy) first." >&2
  exit 1
fi

mkdir -p "$DEPLOY_DIR"

# Set executable bit before zipping so it's preserved in the archive
chmod +x "$BUILD_DIR/ShepherdArena.x86_64"

TMP=$(mktemp -d)
trap "rm -rf $TMP" EXIT
cp -r "$BUILD_DIR" "$TMP/ShepherdArena"

cd "$TMP"
zip -qry "$DEPLOY_DIR/$ZIP_NAME" ShepherdArena
SIZE=$(du -h "$DEPLOY_DIR/$ZIP_NAME" | awk '{print $1}')

echo "Deployed: $DEPLOY_DIR/$ZIP_NAME ($SIZE)"
echo "Available at: https://app.linn.games/shepherd/downloads/$ZIP_NAME"
echo ""
echo "Production deploy (push to u-server):"
echo "  rsync -avz $DEPLOY_DIR/$ZIP_NAME nileneb@192.168.178.12:/var/www/app.linn.games/public/shepherd/downloads/"
