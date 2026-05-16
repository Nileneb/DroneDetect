#!/usr/bin/env bash
# 1v1 PvP Live-Test bootstrap.
#
# Vorraussetzungen (du startest die DREI Server in separaten Terminals BEVOR du dieses Skript läufst):
#   1. cd ~/Desktop/WebDev/app.linn.games && php artisan reverb:start
#   2. cd ~/Desktop/WebDev/app.linn.games && php artisan serve --host=0.0.0.0 --port=8000
#   3. cd ~/Desktop/WebDev/app.linn.games && php artisan queue:work    (optional, for broadcast)
#
# Was dieses Skript macht:
#   - Pingt Server, Reverb
#   - Erzeugt 2 Test-User + Sanctum-Tokens via tinker
#   - Erzeugt eine Shepherd-Session
#   - Lässt beide User joinen (1 als wolf, 1 als drone)
#   - Spawnt 2 ShepherdArena-Standalone-Instanzen mit den richtigen CLI args
set -euo pipefail

API=${API:-http://localhost:6480}
REVERB_VIA=${REVERB_VIA:-ws://localhost:6480}      # nginx-proxied path to reverb's /app/{key}
BIN=${BIN:-/home/nileneb/DroneDetect/Build/Linux/ShepherdArena/ShepherdArena.x86_64}
APP_DIR=${APP_DIR:-/home/nileneb/Desktop/WebDev/app.linn.games}
DOCKER_COMPOSE=${DOCKER_COMPOSE:-"docker compose"}
PHP_SERVICE=${PHP_SERVICE:-php-fpm}                # service to exec tinker on (uses /app working dir inside container)

echo "→ Checking nginx at $API"
HTTP_CODE=$(curl -fsS -o /dev/null -w "%{http_code}" "$API" 2>/dev/null || echo "000")
if [[ ! "$HTTP_CODE" =~ ^[23] ]]; then
  echo "  ✗ nginx not reachable (HTTP $HTTP_CODE). Start: cd $APP_DIR && docker compose up -d web" >&2
  exit 1
fi
echo "  ✓ HTTP server up (status $HTTP_CODE)"

echo "→ Checking Reverb container is running"
if ! docker ps --filter "name=reverb" --format "{{.Names}}" | grep -q reverb; then
  echo "  ✗ Reverb container not running. Start: cd $APP_DIR && docker compose up -d reverb" >&2
  exit 2
fi
echo "  ✓ Reverb container up"

echo "→ Checking binary at $BIN"
[[ -x "$BIN" ]] || { echo "  ✗ Binary not found. Build first: Editor menu > Build > Linux Standalone ShepherdArena (Deploy)" >&2; exit 3; }
echo "  ✓ Binary present"

echo "→ Minting Sanctum tokens via docker tinker"
TOKEN_FILE=$(mktemp)
trap "rm -f $TOKEN_FILE" EXIT

cd "$APP_DIR"
$DOCKER_COMPOSE exec -T "$PHP_SERVICE" php artisan tinker --no-interaction <<'PHP' > "$TOKEN_FILE"
$wolf  = \App\Models\User::firstOrCreate(['email' => 'pvp-wolf@test'],  ['name' => 'WolfTester',  'password' => bcrypt('test')]);
$drone = \App\Models\User::firstOrCreate(['email' => 'pvp-drone@test'], ['name' => 'DroneTester', 'password' => bcrypt('test')]);
echo "WOLF_ID=" . $wolf->id . "\n";
echo "WOLF_TOKEN=" . $wolf->createToken('pvp-test')->plainTextToken . "\n";
echo "DRONE_ID=" . $drone->id . "\n";
echo "DRONE_TOKEN=" . $drone->createToken('pvp-test')->plainTextToken . "\n";
PHP
cd /home/nileneb/DroneDetect

# Extract tokens
WOLF_ID=$(grep ^WOLF_ID= "$TOKEN_FILE" | cut -d= -f2)
WOLF_TOKEN=$(grep ^WOLF_TOKEN= "$TOKEN_FILE" | cut -d= -f2)
DRONE_ID=$(grep ^DRONE_ID= "$TOKEN_FILE" | cut -d= -f2)
DRONE_TOKEN=$(grep ^DRONE_TOKEN= "$TOKEN_FILE" | cut -d= -f2)

if [[ -z "$WOLF_TOKEN" || -z "$DRONE_TOKEN" ]]; then
  echo "  ✗ Token mint failed. Output was:" >&2
  cat "$TOKEN_FILE" >&2
  exit 4
fi
echo "  ✓ Tokens minted: wolf id=$WOLF_ID, drone id=$DRONE_ID"

echo "→ Creating session (host=wolf)"
SESSION=$(curl -fsS -X POST "$API/api/shepherd/sessions" \
    -H "Authorization: Bearer $WOLF_TOKEN" \
    -H "Accept: application/json" \
    -H "Content-Type: application/json" \
    -d '{}')
CODE=$(echo "$SESSION" | python3 -c "import sys,json; print(json.load(sys.stdin)['session']['code'])")
echo "  ✓ Session code: $CODE"

echo "→ Joining as roles"
curl -fsS -X POST "$API/api/shepherd/sessions/$CODE/join" \
    -H "Authorization: Bearer $WOLF_TOKEN" -H "Accept: application/json" \
    -H "Content-Type: application/json" -d '{"role":"wolf"}'   > /dev/null
curl -fsS -X POST "$API/api/shepherd/sessions/$CODE/join" \
    -H "Authorization: Bearer $DRONE_TOKEN" -H "Accept: application/json" \
    -H "Content-Type: application/json" -d '{"role":"drone"}'  > /dev/null
echo "  ✓ Both joined"

echo "→ Host starts the round"
curl -fsS -X POST "$API/api/shepherd/sessions/$CODE/start" \
    -H "Authorization: Bearer $WOLF_TOKEN" -H "Accept: application/json" > /dev/null
echo "  ✓ Round started"

# Launch both clients
echo "→ Launching wolf client (window 1)"
WOLF_LOG=/tmp/dronedetect_pvp_wolf.log
"$BIN" \
    -session="$CODE" \
    -token="$WOLF_TOKEN" \
    -role=wolf \
    -uid="$WOLF_ID" \
    -host=1 \
    -api="$API" \
    -screen-width=900 -screen-height=600 -screen-fullscreen=0 \
    > "$WOLF_LOG" 2>&1 &
WOLF_PID=$!
sleep 2

echo "→ Launching drone client (window 2)"
DRONE_LOG=/tmp/dronedetect_pvp_drone.log
"$BIN" \
    -session="$CODE" \
    -token="$DRONE_TOKEN" \
    -role=drone \
    -uid="$DRONE_ID" \
    -host=0 \
    -api="$API" \
    -screen-width=900 -screen-height=600 -screen-fullscreen=0 \
    -screen-position-x=920 \
    > "$DRONE_LOG" 2>&1 &
DRONE_PID=$!

echo ""
echo "═══════════════════════════════════════════════════════════════"
echo "  PvP Test running"
echo "═══════════════════════════════════════════════════════════════"
echo "  Session code: $CODE"
echo "  Wolf  PID=$WOLF_PID  log=$WOLF_LOG"
echo "  Drone PID=$DRONE_PID log=$DRONE_LOG"
echo ""
echo "  Tail logs in separate terminals:"
echo "    tail -f $WOLF_LOG"
echo "    tail -f $DRONE_LOG"
echo ""
echo "  Kill both: kill $WOLF_PID $DRONE_PID"
echo ""
echo "  Test commands:"
echo "  - In wolf window: WASD movement, try to catch sheep (touch them)"
echo "  - In drone window: WASD + Space/Shift + Q/E movement, press F for scarer"
echo "  - Drone scarer should make wolf panic (sees fear bar fill in wolf HUD)"
echo "═══════════════════════════════════════════════════════════════"
