# DroneFollow training quickstart

## One-time setup per clone

```bash
git config core.hooksPath tools/git-hooks   # auto-push after every commit
```

## One-time scene setup

1. Pull `main`, open `Assets/Scenes/Agent1.unity` and let Unity finish importing.
2. Run **DroneDetect → Configure Active Scene for DroneFollow**. It wires the
   MiDaS/YOLO perception on `Main Camera`, aligns that camera with the flight
   axis at the AR.Drone's 53.8° vertical FOV, enables detection randomization,
   connects the 84×84 depth sensor, removes leftover prototype objects and
   saves the scene.
3. The Console must report `19 vector observations + 84x84 image`.

## Start training (Editor)

```bash
tools/train.sh drone_follow_real_v1
```

Wait for `Listening on port 5004`, then press Play in Unity. The trainer prints
`Connected new brain: DroneFollow` and step summaries every 10k steps.
Monitor with `tensorboard --logdir results`.

Checkpoints (`.onnx` + `.pt`) are written every 25k steps to
`results/<run-id>/DroneFollow/`. Continue an interrupted run with
`tools/train.sh <run-id> --resume`.

## Training set-up at a glance

| | |
|---|---|
| Observations | 10 flight-state values (altitude, body velocity, roll/pitch, heading relative to takeoff, attitude rates), 4 YOLO box values, 4 previous actions, 1 damage, 84×84 MiDaS depth |
| Actions | `forward, left, up, turn` in [-1, 1] (landing: operator / sim after the walk) |
| Curriculum | `follow_stage` 0–4: target speed, roam radius and flight length grow with reward |
| Sim-to-real | AR.Drone camera FOV/orientation, 50–250 ms detection latency, 8 % detection dropout, box jitter, randomized motor response, random start heading |
| Speed | `time_scale: 4` (15 rendered frames per simulated second) |
