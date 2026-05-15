#!/usr/bin/env python3
"""
Converts shepherd drone events from the DB into ML-Agents GAIL .demo files.

Observations (15):
  0-2   drone pos (x,y,z) normalised /30
  3-5   drone velocity (approx from pos-diff at 4Hz)
  6-8   nearest wolf direction (normalised) — zero if no wolf
  9     nearest wolf distance (normalised /30)
  10-12 nearest sheep direction (normalised) — zero if no sheep
  13    nearest sheep distance (normalised /30)
  14    scarer ready (1) or on cooldown/active (0)

Actions (5 continuous):
  0     move_forward  (-1 to 1)
  1     move_right    (-1 to 1)
  2     move_up       (-1 to 1)
  3     yaw           (-1 to 1, approx)
  4     scarer        (0=idle, 1=activate — binary mapped to continuous)

Usage:
  python shepherd_to_drone_demo.py --session ABCDEF
  python shepherd_to_drone_demo.py --all-sessions
"""
import argparse
import os
import struct
import math
import psycopg2
from dotenv import load_dotenv

load_dotenv(os.path.join(os.path.dirname(__file__), '..', '..', 'Desktop', 'WebDev', 'app.linn.games', '.env'))

DB_DSN = (
    f"host={os.getenv('DB_HOST', '127.0.0.1')} "
    f"port={os.getenv('DB_PORT', '5432')} "
    f"dbname={os.getenv('DB_DATABASE', 'app_linn_games')} "
    f"user={os.getenv('DB_USERNAME', 'postgres')} "
    f"password={os.getenv('DB_PASSWORD', '')}"
)

DEMO_MAGIC = b'DMON'
DEMO_VERSION = 1
OBS_SIZE = 15
ACTION_SIZE = 5
TICK_RATE = 4.0  # Hz (every 5th tick of 20Hz)


def fetch_sessions(conn, code=None):
    cur = conn.cursor()
    if code:
        cur.execute("SELECT id, code FROM shepherd.sessions WHERE code = %s", (code,))
    else:
        cur.execute("SELECT id, code FROM shepherd.sessions WHERE status = 'ended'")
    return cur.fetchall()


def fetch_events(conn, session_id):
    cur = conn.cursor()
    cur.execute("""
        SELECT at_tick, player_id, role, pos_x, pos_y, pos_z, rot_y, action
        FROM shepherd.events
        WHERE session_id = %s
        ORDER BY at_tick, role, player_id
    """, (session_id,))
    return cur.fetchall()


def norm_dir(dx, dy, dz):
    mag = math.sqrt(dx*dx + dy*dy + dz*dz)
    if mag < 0.001:
        return 0.0, 0.0, 0.0
    return dx/mag, dy/mag, dz/mag


def events_to_drone_steps(events):
    by_tick = {}
    for e in events:
        t = e[0]
        if t not in by_tick:
            by_tick[t] = {'drone': [], 'wolf': [], 'sheep': []}
        by_tick[t][e[2]].append(e)

    ticks = sorted(by_tick.keys())
    steps = []
    prev_drone_pos = None
    # Track scarer activations per tick
    scarer_ticks = {e[0] for e in events if e[7] == 'scarer_activated' and e[2] == 'drone'}

    for i, tick in enumerate(ticks[:-1]):
        snap = by_tick[tick]
        drone_evs = snap.get('drone', [])
        if not drone_evs:
            continue

        de = drone_evs[0]
        dx, dy, dz, drot = de[3], de[4], de[5], de[6]

        # Velocity from position delta at TICK_RATE Hz
        if prev_drone_pos is not None:
            vx = (dx - prev_drone_pos[0]) * TICK_RATE
            vy = (dy - prev_drone_pos[1]) * TICK_RATE
            vz = (dz - prev_drone_pos[2]) * TICK_RATE
        else:
            vx = vy = vz = 0.0
        prev_drone_pos = (dx, dy, dz)

        # Nearest wolf
        wolf_dx, wolf_dy, wolf_dz, wolf_dist = 0.0, 0.0, 0.0, 1.0
        wolf_evs = snap.get('wolf', [])
        if wolf_evs:
            nearest_wolf = min(wolf_evs, key=lambda e: (e[3]-dx)**2 + (e[5]-dz)**2)
            wx, wy, wz = nearest_wolf[3], nearest_wolf[4], nearest_wolf[5]
            wolf_dist = min(1.0, math.sqrt((wx-dx)**2 + (wy-dy)**2 + (wz-dz)**2) / 30.0)
            wolf_dx, wolf_dy, wolf_dz = norm_dir(wx-dx, wy-dy, wz-dz)

        # Nearest sheep (approximated — sheep pos not in events, use arena center heuristic)
        # Sheep spawn at (±10, 0, ±10) — use (0,0,0) as proxy until sheep state is tracked
        sheep_dx, sheep_dy, sheep_dz = norm_dir(-dx, -dy, -dz)
        sheep_dist = min(1.0, math.sqrt(dx*dx + dz*dz) / 30.0)

        # Scarer ready (we approximate: not active if scarer_activated in last 11 ticks = ~cooldown)
        scarer_ready = 1.0 if tick not in scarer_ticks else 0.0

        obs = [
            dx/30, dy/30, dz/30,
            vx/6,  vy/6,  vz/6,
            wolf_dx, wolf_dy, wolf_dz, wolf_dist,
            sheep_dx, sheep_dy, sheep_dz, sheep_dist,
            scarer_ready,
        ]

        # Actions: reconstruct from velocity + scarer event
        next_tick = ticks[i+1]
        next_drone = by_tick[next_tick].get('drone', [])
        if next_drone:
            ndx = next_drone[0][3] - dx
            ndy = next_drone[0][4] - dy
            ndz = next_drone[0][5] - dz
        else:
            ndx = ndy = ndz = 0.0

        action_scarer = 1.0 if tick in scarer_ticks else 0.0
        # forward/right relative to drone yaw
        yaw_rad = math.radians(drot)
        move_fwd = ndz * math.cos(yaw_rad) + ndx * math.sin(yaw_rad)
        move_right = ndx * math.cos(yaw_rad) - ndz * math.sin(yaw_rad)

        scale = 1.0 / max(0.01, TICK_RATE * 0.5)
        actions = [
            max(-1, min(1, move_fwd * scale)),
            max(-1, min(1, move_right * scale)),
            max(-1, min(1, ndy * scale)),
            0.0,   # yaw not recoverable from positions
            action_scarer,
        ]

        steps.append((obs, actions))

    return steps


def write_demo(steps, out_path):
    os.makedirs(os.path.dirname(out_path) or '.', exist_ok=True)
    with open(out_path, 'wb') as f:
        f.write(DEMO_MAGIC)
        f.write(struct.pack('<I', DEMO_VERSION))
        f.write(struct.pack('<I', OBS_SIZE))
        f.write(struct.pack('<I', ACTION_SIZE))
        f.write(struct.pack('<I', len(steps)))
        for obs, act in steps:
            for v in obs:
                f.write(struct.pack('<f', float(v)))
            for a in act:
                f.write(struct.pack('<f', float(a)))
    print(f"  Wrote {len(steps)} steps → {out_path}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--session')
    parser.add_argument('--all-sessions', action='store_true')
    parser.add_argument('--output', default='Assets/Demonstrations/DroneSessions/')
    args = parser.parse_args()

    conn = psycopg2.connect(DB_DSN)
    sessions = fetch_sessions(conn, args.session if args.session else None)
    if not sessions:
        print("No sessions found.")
        return

    for sid, code in sessions:
        print(f"Processing session {code} ({sid})")
        events = fetch_events(conn, sid)
        drone_events = [e for e in events if e[2] == 'drone']
        if len(drone_events) < 10:
            print(f"  Too few drone events ({len(drone_events)}), skipping.")
            continue

        steps = events_to_drone_steps(events)
        if not steps:
            print(f"  No steps generated, skipping.")
            continue

        out_path = os.path.join(args.output, f"drone_{code}.demo")
        write_demo(steps, out_path)

    conn.close()


if __name__ == '__main__':
    main()
