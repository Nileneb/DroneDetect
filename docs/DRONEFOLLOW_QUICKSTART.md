# DroneFollow training quickstart

## One-time scene setup

1. Pull the latest `main` and open `Assets/Scenes/Agent1.unity` in Unity.
2. Wait for Unity to finish importing/compiling the new scripts.
3. Run **DroneDetect → Configure Active Scene for DroneFollow**. The setup adds the simulation controller adapter and detector, switches the behavior to `DroneFollow`, removes the legacy `DroneAgent` and `ShepherdDroneAgent` components from this training-scene drone so the `DecisionRequester` targets the right agent, removes stale camera sensors, adds a grayscale `RenderTextureSensorComponent` on the Agent, sets a 10 Hz decision interval, and saves the scene.
4. Confirm the Console reports `19 vector observations + 84x84 grayscale depth image`. `Main Camera` supplies the main RGB view, follow detections, the bounding-box overlay, and a centered square depth crop shown in `PiP_Depth` and consumed by the policy. The vector detector uses the same center crop and excludes targets outside it. `GroundCam` remains aimed at the ground, uses a white solid-color background, and is shown in `PiP_Ground`. No separate camera is used for depth. The target prefab is used as a moving projected target proxy; replace that renderer with the humanoid/GLB later.

## Start training

Stop Play mode after configuring the scene and wait for Unity to finish compiling. Start the trainer first:

```bash
./tools/run_training.sh phasefollow
```

`DroneFollow.yaml` and the launcher explicitly use `base_port: 5004`, matching the Unity Editor's ML-Agents port. Wait until the terminal says it is listening on port 5004, then enter Play mode in Unity. Confirm the trainer prints `Connected to Unity environment` and `Connected new brain: DroneFollow?team=0`. The Unity HUD's `Trainer-Handshake aktiv (Schritte offen)` confirms only that the ML-Agents communicator initialized; it does not prove that training steps are progressing. Check the trainer's `Step` output and TensorBoard before considering training active. `BehaviorType.Default` sends actions from the connected trainer to the agent; if no trainer is connected, DroneFollow uses neutral actions rather than reading keyboard input. The launcher uses a local compatibility check based on parsed protocol-version components, avoiding the failing legacy `StrictVersion.version` access in the installed ML-Agents package. The Run button does not start the trainer. The DroneFollow Play panel does not record demonstrations.

The run uses `config/DroneFollow.yaml` with `time_scale: 1` for reliable editor stepping, and writes a new `DroneFollow_YYYYMMDD_HHMM` run. It does not auto-load older checkpoints: the current policy has 19 vector values, a centered 84x84 grayscale depth image, and hybrid actions. The existing DroneCSI policy has a different observation contract and uses ground-truth target inputs. Stop an older DroneFollow run and start a fresh run after applying this sensor change; do not resume its checkpoint.

## What this first slice tests

- 10 navdata/gyro values, 4 projected bounding-box values, 4 previous-action values, and 1 damage value, plus an 84x84 centered grayscale depth image from the Main Camera.
- Four continuous outputs in `forward, left, up, turn` order, plus one discrete land/no-land action.
- Synthetic target-box jitter, dropouts, and 100–200 ms latency; target motion includes turns, pauses, speed changes, and scale changes.
- The depth sensor and detector use the same centered square crop of the Main Camera view; camera/depth input is grayscale depth, not RGB.

This is a sim training smoke path, not a real-drone deployment. There is no concrete `RealDroneController` or YOLO hardware adapter in the current repo; implement those against `IDroneFollowController` and `IDroneTargetDetectionSource` before expecting real-hardware parity.
