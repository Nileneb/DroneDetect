# DroneFollow training quickstart

## One-time scene setup

1. Pull the latest `main` and open `Assets/Scenes/Agent1.unity` in Unity.
2. Wait for Unity to finish importing/compiling the new scripts.
3. Run **DroneDetect → Configure Active Scene for DroneFollow**. The setup adds the simulation controller adapter and detector, switches the behavior to `DroneFollow`, disables the legacy `DroneAgent`, disables child ML-Agents sensors, sets the decision interval to 10 Hz, and saves the scene.
4. Confirm the Console reports that the scene was configured. `Main Camera` supplies the main RGB view, follow detections and a grayscale depth imprint in `PiP_Depth`; its bounding box is drawn over the main Game view. `GroundCam` remains aimed at the ground, uses a white solid-color background, and is shown in `PiP_Ground`. No separate camera is used for depth. The target prefab is used as a moving projected target proxy; replace that renderer with the humanoid/GLB later.

## Start training

Stop Play mode after configuring the scene and wait for Unity to finish compiling. Start the trainer first:

```bash
./tools/run_training.sh phasefollow
```

Wait until the terminal says it is listening on port 5004, then enter Play mode in Unity. Confirm the trainer prints `Connected to Unity environment` and `Connected new brain: DroneFollow?team=0`. The launcher uses a local compatibility check based on parsed protocol-version components, avoiding the failing legacy `StrictVersion.version` access in the installed ML-Agents package. The Run button does not start the trainer. The DroneFollow Play panel does not record demonstrations.

The run uses `config/DroneFollow.yaml` and writes a new `DroneFollow_YYYYMMDD_HHMM` run. It does not auto-load DroneCSI: the new policy has 19 observations and hybrid actions, while the old DroneCSI policy has 14 observations and uses ground-truth target inputs.

## What this first slice tests

- 10 navdata/gyro values, 4 projected bounding-box values, 4 previous-action values, and 1 damage value.
- Four continuous outputs in `forward, left, up, turn` order, plus one discrete land/no-land action.
- Synthetic target-box jitter, dropouts, and 100–200 ms latency; target motion includes turns, pauses, speed changes, and scale changes.
- No camera/depth pixels are passed to the `DroneFollow` policy.

This is a sim training smoke path, not a real-drone deployment. There is no concrete `RealDroneController` or YOLO hardware adapter in the current repo; implement those against `IDroneFollowController` and `IDroneTargetDetectionSource` before expecting real-hardware parity.
