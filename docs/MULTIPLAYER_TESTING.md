# 1v1 PvP Multiplayer — Testing Guide

Real-time Wolf vs Drone Multiplayer via Laravel Reverb + Pusher protocol.

## Architecture

```
┌────────────────────┐                   ┌────────────────────┐
│  Wolf Client       │   HTTP POST       │  Drone Client      │
│  (Standalone)      │ ─────────────────▶│  (Standalone)      │
│                    │   /api/.../relay  │                    │
└────────┬───────────┘                   └──────┬─────────────┘
         │                                       │
         │ WebSocket (wss://)                    │
         │  private-shepherd.{code}              │
         │                                       │
         └──────────────┬────────────────────────┘
                        ▼
              ┌──────────────────┐
              │  Laravel Reverb  │
              │   (port 8080)    │
              │                  │
              │  + ShepherdEvent │
              │    broadcaster   │
              └──────────────────┘
```

**Outgoing events** (client → server → broadcast to others):
- `wolf.moved`, `drone.moved` — 10Hz position throttle
- `scarer.activated` — when drone fires scarer pulse (drone → wolf gets fear)
- `sheep.caught` — host-authoritative when wolf grabs a sheep (host → all)

**Server-side**: `POST /api/shepherd/sessions/{code}/relay {event, data}` — validates, broadcasts
via `event(new ShepherdEvent($code, $event, $data))->toOthers()`.

## Prerequisites

### Server (BigOne, app.linn.games local dev)

```bash
cd ~/Desktop/WebDev/app.linn.games

# 1. Ensure feature branch with relay endpoint
git checkout feature/shepherd-demo-upload

# 2. Migrate (creates shepherd.demos + uses existing shepherd.sessions/events tables)
php artisan migrate

# 3. Start Reverb WebSocket server (separate terminal — keep running)
php artisan reverb:start

# 4. Start Laravel HTTP server (separate terminal — keep running)
php artisan serve --host=0.0.0.0 --port=8000
# OR if you have docker compose with nginx/php-fpm: use that
```

### Unity build

```bash
cd ~/DroneDetect
# Edit > Build > Linux Standalone ShepherdArena (Deploy)
# Or via batch-mode (editor must be closed):
./tools/build-webgl-batch.sh  # actually builds Linux now after recent BuildScript update
```

## Tests

### Test 1: Single-client smoke test
1. Launch one binary: `./Build/Linux/ShepherdArena/ShepherdArena.x86_64`
2. In-game: should see PiP overlays (GroundCam + DepthCam) top-right
3. Check stdout for `[RevbClient] WebSocket connected` log
4. URL parameters required: `?session=ABCDEF&token=<jwt>&role=wolf` — Standalone builds don't get these from a browser, see workaround below

**Workaround for Standalone-without-browser URL params**: edit `ShepherdGameManager` Inspector
`editorRole` field, OR start binary with env-var injection (see `command-line args` section).

### Test 2: Two-client PvP
On LAN, two machines (or two binaries on same machine with different sessions):

**Player 1 (Wolf)**:
```bash
./Build/Linux/ShepherdArena/ShepherdArena.x86_64 \
    -session=ABCDEF -token=$WOLF_JWT -role=wolf -uid=1 -host=1
```

**Player 2 (Drone)**:
```bash
./Build/Linux/ShepherdArena/ShepherdArena.x86_64 \
    -session=ABCDEF -token=$DRONE_JWT -role=drone -uid=2 -host=0
```

Expected behavior:
- Both clients see each other's character (remote wolf/drone mesh)
- Drone moves with WASD + Space/Shift, Q/E rotate
- Wolf moves with WASD
- Drone presses F (or scarer button when added) → wolf gets scared, panic-mode triggers
- Wolf gets close to sheep → sheep caught → drone's score updates
- Round ends after 5 min OR all sheep caught → end screen shown

### Test 3: Network failure resilience
- Kill `reverb:start` → clients should continue running, just no live sync
- Restart Reverb → reconnect logic… (TODO: not yet implemented; current build assumes stable connection)

## JWT Token generation (for testing)

```bash
# Create test user
php artisan tinker
>>> $u = User::factory()->withoutTwoFactor()->create(['email' => 'wolf@test']);
>>> echo $u->createToken('shepherd-test')->plainTextToken;
# → copy this token, paste as WOLF_JWT in env

>>> $u = User::factory()->withoutTwoFactor()->create(['email' => 'drone@test']);
>>> echo $u->createToken('shepherd-test')->plainTextToken;
# → DRONE_JWT
```

## Known Limitations

- **No remoteWolf/remoteDrone visual yet**: prefab fields exist but instantiating only creates
  empty GameObjects if prefab is null. Need to assign actual visual prefabs in Inspector.
- **No reconnect on WebSocket drop**: current ReverbClient doesn't auto-reconnect.
- **No session cleanup**: stale sessions in `shepherd.sessions` accumulate; need cron / TTL.
- **CORS for cross-origin Standalone WebSocket**: shouldn't be an issue for native client but worth noting.

## Debugging

```bash
# Check Reverb is up
curl http://localhost:8080/app/linn-games-key -I
# Should respond with WebSocket-upgrade headers or similar

# Check session exists in DB
psql -d linn_games -c "SELECT code, status, host_user_id FROM shepherd.sessions ORDER BY created_at DESC LIMIT 5"

# Tail Reverb logs
php artisan reverb:start --debug
```
