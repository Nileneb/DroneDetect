# DroneFollow training quickstart

## One-time scene setup

1. Pull the latest `main` and open `Assets/Scenes/Agent1.unity` in Unity.
2. Wait for Unity to finish importing/compiling the new scripts.
3. Run **DroneDetect → Configure Active Scene for DroneFollow**. The setup adds the simulation controller adapter and detector, switches the behavior to `DroneFollow`, disables the legacy `DroneAgent`, disables child ML-Agents sensors, sets the decision interval to 10 Hz, and saves the scene.
4. Confirm the Console reports that the scene was configured. The current scene's first waypoint sphere is used as a moving projected target proxy; replace that renderer with the humanoid/GLB later.

## Start training

With the Unity Editor open on `Agent1.unity` and ready for ML-Agents to connect, run:

```bash
./tools/run_training.sh phasefollow
```

The run uses `config/DroneFollow.yaml` and writes a new `DroneFollow_YYYYMMDD_HHMM` run. It does not auto-load DroneCSI: the new policy has 18 observations, while the old DroneCSI policy has 14 and uses ground-truth target inputs.

## What this first slice tests

- 10 navdata/gyro values, 4 projected bounding-box values, and 4 previous-action values.
- Four continuous outputs in `forward, left, up, turn` order.
- Synthetic target-box jitter, dropouts, and 100–200 ms latency; target motion includes turns, pauses, speed changes, and scale changes.
- No camera/depth pixels are passed to the `DroneFollow` policy.

This is a sim training smoke path, not a real-drone deployment. There is no concrete `RealDroneController` or YOLO hardware adapter in the current repo; implement those against `IDroneFollowController` and `IDroneTargetDetectionSource` before expecting real-hardware parity.
