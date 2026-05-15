#!/bin/bash
set -e

# BigOne: 192.168.178.11
# WebGL build landet in docker volume: app.linn.games/docker/common/nginx/shepherd/
# nginx mappt /shepherd → /var/www/shepherd

UNITY_BUILD_DIR="Build/WebGL/ShepherdArena"
DEPLOY_DIR="/home/nileneb/Desktop/WebDev/app.linn.games/shepherd"
# nginx mappt ./shepherd → /var/www/shepherd im Docker-Container

echo "=== ShepherdArena WebGL Deploy ==="

if [ ! -d "$UNITY_BUILD_DIR" ]; then
    echo "ERROR: Build nicht gefunden: $UNITY_BUILD_DIR"
    echo "Erst in Unity: File → Build Settings → WebGL → Build"
    exit 1
fi

echo "Kopiere Build nach $DEPLOY_DIR ..."
mkdir -p "$DEPLOY_DIR"
rsync -av --delete "$UNITY_BUILD_DIR/" "$DEPLOY_DIR/"

echo ""
echo "Deploy abgeschlossen!"
echo "URL: https://app.linn.games/shepherd"
echo ""
echo "Wenn der Container nicht neu gestartet werden muss:"
echo "  docker exec app_nginx nginx -s reload"
