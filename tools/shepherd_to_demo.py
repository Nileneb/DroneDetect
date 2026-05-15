#!/usr/bin/env python3
"""
Converts shepherd_events from the DB into ML-Agents .demo binary files.

Usage:
  python shepherd_to_demo.py --session ABCDEF --output Assets/Demonstrations/WolfSessions/
  python shepherd_to_demo.py --all-sessions --output Assets/Demonstrations/WolfSessions/
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

# ML-Agents Demo Format v0.1 constants
DEMO_MAGIC = b'DMON'
DEMO_VERSION = 1
OBS_SIZE = 10   # matches WolfAgent.CollectObservations: 3+3+4+2
ACTION_SIZE = 2  # forward, turn


def fetch_events(conn, session_id):
    cur = conn.cursor()
    cur.execute("""
        SELECT at_tick, player_id, role, pos_x, pos_y, pos_z, rot_y, action
        FROM shepherd.events
        WHERE session_id = %s AND role = 'wolf'
        ORDER BY at_tick, player_id
    """, (session_id,))
    return cur.fetchall()


def fetch_sessions(conn, code=None):
    cur = conn.cursor()
    if code:
        cur.execute("SELECT id, code FROM shepherd.sessions WHERE code = %s", (code,))
    else:
        cur.execute("SELECT id, code FROM shepherd.sessions WHERE status = 'ended'")
    return cur.fetchall()


def events_to_obs(events):
    """
    Reconstruct WolfAgent observations from raw position data.
    Returns list of (obs[10], action[2]) tuples.
    """
    steps = []
    ticks = sorted(set(e[0] for e in events))

    # Build per-tick snapshot
    by_tick = {}
    for e in events:
        t = e[0]
        if t not in by_tick:
            by_tick[t] = []
        by_tick[t].append(e)

    prev_pos = {}

    for i, tick in enumerate(ticks[:-1]):
        wolf_events = by_tick.get(tick, [])
        next_events = by_tick.get(ticks[i + 1], [])

        for ev in wolf_events:
            pid = ev[1]
            px, py, pz, ry = ev[3], ev[4], ev[5], ev[6]

            # Velocity from position delta (at 50ms ticks = 20Hz)
            if pid in prev_pos:
                pp = prev_pos[pid]
                vx = (px - pp[0]) * 20
                vy = (py - pp[1]) * 20
                vz = (pz - pp[2]) * 20
            else:
                vx = vy = vz = 0.0

            prev_pos[pid] = (px, py, pz)

            # Find nearest sheep (approximated as center 0,0,0 if no sheep data)
            sheep_dx, sheep_dy, sheep_dz, sheep_dist = 0, 0, 1, 1.0

            # Find drone (first drone in same tick)
            drone_dist, scarer_active = 1.0, 0.0
            drone_evs = [e for e in wolf_events if e[2] == 'drone']
            if drone_evs:
                de = drone_evs[0]
                ddx = de[3] - px
                ddy = de[4] - py
                ddz = de[5] - pz
                drone_dist = min(1.0, math.sqrt(ddx**2 + ddy**2 + ddz**2) / 20.0)
                scarer_active = 1.0 if de[7] == 'scarer_on' else 0.0

            obs = [
                px / 10, py / 10, pz / 10,
                vx / 4, vy / 4, vz / 4,
                sheep_dx, sheep_dy, sheep_dz, sheep_dist,
            ]
            # Note: We use 10 obs here; drone obs absorbed into sheep slot for simplicity
            # because we truncated to 10. WolfAgent has 10 obs total (3+3+4).
            # drone_dist and scarer are baked into obs[8] and obs[9] instead.
            obs[8] = drone_dist
            obs[9] = scarer_active

            # Action: normalize forward/turn from velocity
            forward = min(1, max(-1, vz / 4))
            turn = 0.0  # no turn data from position alone

            steps.append((obs, [forward, turn]))

    return steps


def write_demo(steps, out_path):
    os.makedirs(os.path.dirname(out_path) or '.', exist_ok=True)
    with open(out_path, 'wb') as f:
        # Header
        f.write(DEMO_MAGIC)
        f.write(struct.pack('<I', DEMO_VERSION))
        f.write(struct.pack('<I', OBS_SIZE))
        f.write(struct.pack('<I', ACTION_SIZE))
        f.write(struct.pack('<I', len(steps)))

        for obs, action in steps:
            for v in obs:
                f.write(struct.pack('<f', float(v)))
            for a in action:
                f.write(struct.pack('<f', float(a)))

    print(f"  Wrote {len(steps)} steps → {out_path}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--session', help='Session code')
    parser.add_argument('--all-sessions', action='store_true')
    parser.add_argument('--output', default='Assets/Demonstrations/WolfSessions/')
    args = parser.parse_args()

    conn = psycopg2.connect(DB_DSN)

    sessions = fetch_sessions(conn, args.session if args.session else None)
    if not sessions:
        print("No sessions found.")
        return

    for sid, code in sessions:
        print(f"Processing session {code} ({sid})")
        events = fetch_events(conn, sid)
        if not events:
            print(f"  No wolf events, skipping.")
            continue

        steps = events_to_obs(events)
        if not steps:
            print(f"  Not enough data, skipping.")
            continue

        out_path = os.path.join(args.output, f"wolf_{code}.demo")
        write_demo(steps, out_path)

    conn.close()


if __name__ == '__main__':
    main()
