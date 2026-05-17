#!/usr/bin/env bash
# Packs ALL three standalone builds (Linux + Windows + macOS) into zips,
# copies them into the local app.linn.games dev tree, and optionally rsyncs
# to the production server.
#
# Usage:
#   zip-and-deploy.sh             # local-only (dev tree)
#   zip-and-deploy.sh --prod      # local + rsync to u-server
#   zip-and-deploy.sh --only=lin  # one platform: lin|win|mac
#
# Run AFTER Unity → Build → ALL Standalone (or per-platform menu items).
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_ROOT="$PROJECT_ROOT/Build"
DEPLOY_LOCAL=/home/nileneb/Desktop/WebDev/app.linn.games/public/shepherd/downloads

# Prod target — matches GH-Actions cloud-build deploy
PROD_HOST="${SHEPHERD_DEPLOY_HOST:-app.linn.games}"
PROD_USER="${SHEPHERD_DEPLOY_USER:-nileneb}"
PROD_PATH="${SHEPHERD_DEPLOY_PATH:-/var/www/app.linn.games/public/shepherd/downloads}"

DO_PROD=0
ONLY=""
for arg in "$@"; do
  case "$arg" in
    --prod) DO_PROD=1 ;;
    --only=*) ONLY="${arg#--only=}" ;;
    -h|--help)
      grep -E '^# ' "$0" | sed 's/^# //'
      exit 0 ;;
  esac
done

mkdir -p "$DEPLOY_LOCAL"

# platform | source-subdir | zip-name | entry-binary (presence-check)
PLATFORMS=(
  "lin|Linux/ShepherdArena|shepherd-linux-x64.zip|ShepherdArena.x86_64"
  "win|Windows/ShepherdArena|shepherd-windows-x64.zip|ShepherdArena.exe"
  "mac|macOS|shepherd-macos.zip|ShepherdArena.app"
)

ZIPPED=()
for spec in "${PLATFORMS[@]}"; do
  IFS='|' read -r plat subdir zipname check <<<"$spec"
  if [[ -n "$ONLY" && "$ONLY" != "$plat" ]]; then continue; fi

  src="$BUILD_ROOT/$subdir"
  if [[ ! -e "$src/$check" ]]; then
    echo "skip $plat — no build at $src/$check"
    continue
  fi

  [[ "$plat" == "lin" ]] && chmod +x "$src/$check" || true

  TMP=$(mktemp -d)
  case "$plat" in
    lin) cp -r "$src" "$TMP/ShepherdArena" ;;
    win) cp -r "$src" "$TMP/ShepherdArena" ;;
    mac) cp -r "$src/$check" "$TMP/" ;;
  esac
  (cd "$TMP" && zip -qry "$DEPLOY_LOCAL/$zipname" .)
  rm -rf "$TMP"

  SIZE=$(du -h "$DEPLOY_LOCAL/$zipname" | awk '{print $1}')
  echo "packed $plat → $DEPLOY_LOCAL/$zipname ($SIZE)"
  ZIPPED+=("$zipname")
done

if [[ ${#ZIPPED[@]} -eq 0 ]]; then
  echo "Nothing to deploy. Did you run Unity > Build > ALL Standalone first?" >&2
  exit 1
fi

if [[ $DO_PROD -eq 1 ]]; then
  echo "Production deploy (rsync to ${PROD_USER}@${PROD_HOST}) …"
  for z in "${ZIPPED[@]}"; do
    rsync -avz --progress "$DEPLOY_LOCAL/$z" "${PROD_USER}@${PROD_HOST}:${PROD_PATH}/"
    echo "  → https://app.linn.games/shepherd/downloads/$z"
  done
else
  echo
  echo "To push to production:"
  echo "  $0 --prod"
fi
