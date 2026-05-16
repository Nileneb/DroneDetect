#!/usr/bin/env bash
# Packs the Linux Standalone build into a zip, copies to local app.linn.games dev-tree,
# and optionally rsyncs to production u-server.
#
# Usage:
#   zip-and-deploy-linux.sh           # local-only (dev tree)
#   zip-and-deploy-linux.sh --prod    # local + rsync to u-server
#
# Run AFTER Unity has finished `Build > Linux Standalone ShepherdArena (Deploy)`.
set -euo pipefail

BUILD_DIR=/home/nileneb/DroneDetect/Build/Linux/ShepherdArena
DEPLOY_DIR=/home/nileneb/Desktop/WebDev/app.linn.games/public/shepherd/downloads
ZIP_NAME=shepherd-linux-x64.zip

# Prod target — matches GH-Actions cloud-build deploy
PROD_HOST="${SHEPHERD_DEPLOY_HOST:-192.168.178.12}"
PROD_USER="${SHEPHERD_DEPLOY_USER:-nileneb}"
PROD_PATH="${SHEPHERD_DEPLOY_PATH:-/var/www/app.linn.games/public/shepherd/downloads}"

DO_PROD=0
for arg in "$@"; do
  case "$arg" in
    --prod) DO_PROD=1 ;;
    -h|--help)
      grep -E '^# ' "$0" | sed 's/^# //'
      exit 0 ;;
  esac
done

if [[ ! -f "$BUILD_DIR/ShepherdArena.x86_64" ]]; then
  echo "Build not found at $BUILD_DIR/ShepherdArena.x86_64" >&2
  echo "Run Unity > Build > Linux Standalone ShepherdArena (Deploy) first." >&2
  exit 1
fi

mkdir -p "$DEPLOY_DIR"
chmod +x "$BUILD_DIR/ShepherdArena.x86_64"

TMP=$(mktemp -d)
trap "rm -rf $TMP" EXIT
cp -r "$BUILD_DIR" "$TMP/ShepherdArena"

cd "$TMP"
zip -qry "$DEPLOY_DIR/$ZIP_NAME" ShepherdArena
SIZE=$(du -h "$DEPLOY_DIR/$ZIP_NAME" | awk '{print $1}')

echo "Local deploy: $DEPLOY_DIR/$ZIP_NAME ($SIZE)"
echo "  → http://localhost/shepherd/downloads/$ZIP_NAME (dev)"

if [[ $DO_PROD -eq 1 ]]; then
  echo "Production deploy (rsync to ${PROD_USER}@${PROD_HOST}) …"
  rsync -avz --progress "$DEPLOY_DIR/$ZIP_NAME" \
    "${PROD_USER}@${PROD_HOST}:${PROD_PATH}/"
  echo "  → https://app.linn.games/shepherd/downloads/$ZIP_NAME"
else
  echo
  echo "To push to production:"
  echo "  $0 --prod"
fi
