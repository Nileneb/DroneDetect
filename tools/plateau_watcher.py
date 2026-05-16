#!/usr/bin/env python3
"""
Monitors a running mlagents-learn training for reward plateau.
When plateau is detected:
  1. Sends SIGTERM to mlagents-learn (triggers clean shutdown + .onnx export)
  2. Waits for the .onnx to appear
  3. Copies it to Assets/Models/DroneCSI.onnx for ShepherdArena Phase 4

Usage (run in mlagents conda env alongside training):
  python tools/plateau_watcher.py --run-id v8_1 --behavior DroneCSI

Plateau criteria (all must be true):
  - At least --min-samples reward points observed
  - Relative improvement over last --window steps < --threshold %
  - Current lesson >= --min-level (default: 3, don't export too early)
"""
import argparse
import glob
import os
import shutil
import signal
import subprocess
import time
import struct

PROJECT_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def find_tfevents(run_id: str, behavior: str) -> list[str]:
    pattern = os.path.join(PROJECT_ROOT, "results", run_id, behavior,
                           "events.out.tfevents.*")
    return sorted(glob.glob(pattern))


def read_scalar(path: str, tag: str) -> list[tuple[int, float]]:
    """Parse TFEvents file — reads scalar summaries without tensorflow."""
    from tensorboard.backend.event_processing.event_accumulator import (
        EventAccumulator, SCALARS)
    ea = EventAccumulator(path, size_guidance={SCALARS: 0})
    ea.Reload()
    if tag not in ea.Tags().get("scalars", []):
        return []
    return [(e.step, e.value) for e in ea.Scalars(tag)]


def find_mlagents_pid() -> int | None:
    try:
        out = subprocess.check_output(
            ["pgrep", "-f", "mlagents-learn"], text=True).strip()
        pids = [int(p) for p in out.splitlines() if p.strip()]
        return pids[0] if pids else None
    except subprocess.CalledProcessError:
        return None


def is_plateau(rewards: list[float], window: int, threshold_pct: float) -> bool:
    if len(rewards) < window:
        return False
    recent = rewards[-window:]
    mn, mx = min(recent), max(recent)
    if mn <= 0:
        return False
    relative_range = (mx - mn) / abs(mn)
    return relative_range < (threshold_pct / 100.0)


def wait_for_new_onnx(run_id: str, behavior: str, before: set, timeout: int = 120) -> str | None:
    onnx_dir = os.path.join(PROJECT_ROOT, "results", run_id, behavior)
    deadline  = time.time() + timeout
    while time.time() < deadline:
        files = set(glob.glob(os.path.join(onnx_dir, "*.onnx")))
        new   = files - before
        if new:
            return max(new, key=os.path.getmtime)
        time.sleep(2)
    return None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--run-id",   default="v8_1")
    parser.add_argument("--behavior", default="DroneCSI")
    parser.add_argument("--window",   type=int,   default=5,
                        help="Number of summary points to check for plateau")
    parser.add_argument("--threshold", type=float, default=2.0,
                        help="Max relative reward range %% to call plateau")
    parser.add_argument("--min-samples", type=int, default=10,
                        help="Minimum reward points before plateau check starts")
    parser.add_argument("--min-level",   type=int, default=3,
                        help="Only export if curriculum lesson >= this level")
    parser.add_argument("--poll",    type=int, default=30,
                        help="Poll interval in seconds")
    parser.add_argument("--dry-run", action="store_true",
                        help="Print plateau info but don't kill/copy")
    args = parser.parse_args()

    dest = os.path.join(PROJECT_ROOT, "Assets", "Models", "DroneCSI.onnx")
    reward_tag  = "Environment/Cumulative Reward"
    lesson_tag  = "Environment/Lesson"

    print(f"[plateau_watcher] Monitoring {args.run_id}/{args.behavior}")
    print(f"  Plateau: window={args.window} summaries, threshold={args.threshold}%")
    print(f"  Min curriculum level: {args.min_level}")
    print(f"  Poll: every {args.poll}s")
    print()

    while True:
        event_files = find_tfevents(args.run_id, args.behavior)
        if not event_files:
            print("[plateau_watcher] No tfevents found yet — waiting…")
            time.sleep(args.poll)
            continue

        # Merge all event files (resumed runs create new files)
        rewards: list[tuple[int, float]] = []
        lessons: list[tuple[int, float]] = []
        for f in event_files:
            try:
                rewards += read_scalar(f, reward_tag)
                lessons += read_scalar(f, lesson_tag)
            except Exception as e:
                print(f"[plateau_watcher] Warning reading {f}: {e}")

        rewards.sort(key=lambda x: x[0])
        lessons.sort(key=lambda x: x[0])

        reward_vals = [v for _, v in rewards]
        current_lesson = lessons[-1][1] if lessons else 0

        print(f"[plateau_watcher] step={rewards[-1][0] if rewards else '?'}  "
              f"reward={reward_vals[-1]:.3f if reward_vals else '?'}  "
              f"lesson={current_lesson:.0f}  samples={len(reward_vals)}")

        if (len(reward_vals) >= args.min_samples
                and current_lesson >= args.min_level
                and is_plateau(reward_vals, args.window, args.threshold)):

            print(f"\n[plateau_watcher] PLATEAU DETECTED at Level {current_lesson:.0f}!")
            print(f"  Last {args.window} rewards: "
                  f"{[f'{r:.2f}' for r in reward_vals[-args.window:]]}")

            if args.dry_run:
                print("  --dry-run: would send SIGTERM now")
                time.sleep(args.poll)
                continue

            # Record existing .onnx files before stopping
            onnx_dir   = os.path.join(PROJECT_ROOT, "results", args.run_id, args.behavior)
            before     = set(glob.glob(os.path.join(onnx_dir, "*.onnx")))
            latest_existing = max(before, key=os.path.getmtime) if before else None

            pid = find_mlagents_pid()
            if pid:
                print(f"  Sending SIGTERM to mlagents-learn (PID {pid})…")
                os.kill(pid, signal.SIGTERM)
                # Wait for new .onnx (clean shutdown exports it)
                new_onnx = wait_for_new_onnx(args.run_id, args.behavior, before)
            else:
                print("  mlagents-learn not found — using latest existing .onnx")
                new_onnx = latest_existing

            if new_onnx:
                os.makedirs(os.path.dirname(dest), exist_ok=True)
                shutil.copy2(new_onnx, dest)
                print(f"\n[plateau_watcher] Exported: {os.path.basename(new_onnx)}")
                print(f"  → {dest}")
                print(f"\nShepherdArena Phase 4 ist bereit!")
                print(f"Starte Training: mlagents-learn config/DroneShepherd.yaml "
                      f"--run-id=shepherd_v1 --initialize-from=results/{args.run_id}")
            else:
                print("  No .onnx found after shutdown. Check results/ manually.")
            return

        time.sleep(args.poll)


if __name__ == "__main__":
    main()
