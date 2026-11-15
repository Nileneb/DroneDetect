# DroneFollow training quickstart

## One-time scene setup

1. Pull the latest `main` and open `Assets/Scenes/Agent1.unity` in Unity.
2. Wait for Unity to finish importing/compiling the new scripts.
3. Run **DroneDetect → Configure Active Scene for DroneFollow**. The Drone prefab contains one `DroneFollowSentisPerception` component on `Main Camera`; setup assigns the imported MiDaS and YOLO `ModelAsset`s, wires the depth RenderTexture and Agent sensor, removes missing script components across the training scene, switches the behavior to `DroneFollow`, removes legacy agents and camera sensors, then saves the scene.
4. Confirm the Console reports `19 vector observations + 84x84 grayscale depth image`. The Game view has one full-screen Screen 1: the Main Camera image is the background, with the detection box, centered crosshair, and live curriculum HUD over it. Screen 2 (MiDaS depth) and Screen 3 (GroundCam) are small picture-in-picture panels in the upper-right of Screen 1; they are not additional full-screen views. Setup hides the legacy `DroneControlPanel`, which is replaced by the DroneFollow HUD. Main Camera renders once to the 640x360 `MainCameraRGB` RenderTexture shared by the display and MiDaS/YOLO. MiDaS runs at 15 Hz and writes the full-frame 84x84 grayscale depth output (2nd–98th percentile normalized per frame) to the RenderTexture consumed by the policy; YOLO runs at the 10 Hz decision rate. YOLO uses the model's `[1,84,8400]` output with person-class confidence filtering and per-person non-maximum suppression (NMS) before the highest retained box is sent to Screen 1 and the policy. Its output decoder accepts both channel-first and channel-last tensors, and the Screen 1 HUD reports whether inference has returned a result and its best person score. The curriculum checklist marks earlier stages complete as the active stage advances and marks the final challenge complete only after it has been passed. No post-render image callback, second depth camera, or Unity-depth-buffer shader is used.

## Start training

Stop Play mode after configuring the scene and wait for Unity to finish compiling. Start the trainer first:

```bash
./tools/run_training.sh phasefollow
```

`DroneFollow.yaml` and the launcher explicitly use `base_port: 5004`, matching the Unity Editor's ML-Agents port. Wait until the terminal says it is listening on port 5004, then enter Play mode in Unity. Confirm the trainer prints `Connected to Unity environment` and `Connected new brain: DroneFollow?team=0`. The Unity HUD's `Trainer-Handshake aktiv (Schritte offen)` confirms only that the ML-Agents communicator initialized; it does not prove that training steps are progressing. Check the trainer's `Step` output and TensorBoard before considering training active. `BehaviorType.Default` sends actions from the connected trainer to the agent; if no trainer is connected, DroneFollow uses neutral actions rather than reading keyboard input. The launcher uses a local compatibility check based on parsed protocol-version components, avoiding the failing legacy `StrictVersion.version` access in the installed ML-Agents package. The Run button does not start the trainer. The DroneFollow Play panel does not record demonstrations.

The run configs use `time_scale: 1.2`, a modest 20% increase over real-time simulation. They write new runs and do not auto-load older checkpoints: the current policy has 19 vector values, a full-frame 84x84 grayscale depth image, and hybrid actions. The existing DroneCSI policy has a different observation contract and uses ground-truth target inputs. Stop an older DroneFollow run and start a fresh run after applying this sensor change; do not resume its checkpoint.

## What this first slice tests

- 10 navdata/gyro values, 4 projected bounding-box values, 4 previous-action values, and 1 damage value, plus an 84x84 full-frame grayscale depth image from the Main Camera.
- Four continuous outputs in `forward, left, up, turn` order, plus one discrete land/no-land action.
- Target motion includes turns, pauses, speed changes, and scale changes. YOLO detections are model outputs; there is no simulated detector fallback.
- MiDaS and YOLO use the same Main Camera frame, stretched into each model's square input (`inputFraming`). MiDaS turns its model output into the 84x84 grayscale depth observation; YOLO detects COCO persons from RGB.

This is a sim training smoke path, not a real-drone deployment. There is no concrete `RealDroneController`; implement one against `IDroneFollowController` before expecting real-hardware parity. The bundled YOLO ONNX metadata identifies the model as AGPL-3.0; see the licensing caution in [DRONEFOLLOW_TRAINING.md](./DRONEFOLLOW_TRAINING.md) before redistribution.
