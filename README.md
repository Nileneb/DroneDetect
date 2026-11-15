# DroneDetect – a drone that follows you, trained in simulation

A reinforcement-learning policy, trained entirely in a Unity simulation, flies a
real **Parrot AR.Drone 2.0** behind a person. An Android phone is the ground
station: it receives the drone's video over Wi-Fi, runs all neural networks
on-device with **Unity Inference Engine (Sentis)**, and sends the flight
commands back.

The policy never sees ground truth. It perceives the world the way the real
drone does – through its front camera:

```
                 ┌─────────────── simulation (training) ───────────────┐
                 │  hilly terrain + AR.Drone physics twin + path walker│
                 └──────────────────────────┬──────────────────────────┘
                                            │ same observation contract
front camera 640×360 ──┬─► MiDaS v2.1 small ─► 84×84 relative depth ──────────┐
                       └─► YOLOv8n ─► person boxes ─► target tracker ─► box ──┤
navdata (altitude, velocity, attitude, rates) ────────────────────────────────┤
                                                                               ▼
                         PPO policy (+ BC/GAIL from manual gamepad demos)  ──► forward, left, up, turn
                                                                               │
                         safety envelope (shadow mode, lost-target hover, ceiling, battery, link)
                                                                               ▼
                                                              AT*PCMD over Wi-Fi → AR.Drone 2.0
```

## What is in here

| Part | Where |
|---|---|
| Policy agent, rewards, curriculum, deployment mode | `Assets/Scripts/DroneFollowAgent.cs` |
| Perception: MiDaS depth, YOLO persons, re-identification | `Assets/Scripts/DroneFollowSentisPerception.cs`, `PersonTargetTracker.cs` |
| AR.Drone 2.0 physics twin (simulator) | `Assets/Scripts/SimulatedDroneController.cs` |
| AR.Drone 2.0 driver: AT commands, navdata, H.264 video | `Assets/Scripts/ArDrone/` |
| Android ground-station UI and MediaCodec decoder | `Assets/Scripts/ArDrone/GroundStationUI.cs`, `Assets/Plugins/Android/` |
| Terrain, paths and gates, scene setup, demo recording, builds | `Assets/Editor/` |
| Target walking loops and gate triggers | `Assets/Scripts/DroneFollowPath*.cs`, `DroneFollowGate.cs` |
| Training configuration | `config/DroneFollow.yaml`, `tools/train.sh` |
| Tests (wire format, sign conventions, tracker) | `Assets/Editor/Tests/` |

## Sim-to-real: what keeps the trained policy valid on hardware

- **Same sensors, same conventions.** Sim and drone implement one
  `IDroneController`; navdata is mapped to the simulator's axes and signs
  (unit-tested). Attitude rates are derived from navdata in both worlds because
  the AR.Drone's demo navdata has no gyro; heading is relative to takeoff.
- **Same camera.** The sim camera sits at the drone's nose, looks along the
  flight axis and uses the AR.Drone's 53.8° vertical FOV at 640×360.
- **Perception is pre-trained, not learned from sim pixels.** The policy sees
  MiDaS depth and a YOLO box – abstractions that look alike for rendered and
  real images – instead of raw RGB.
- **Imperfections during training.** 50–250 ms detection latency (Wi-Fi video +
  phone inference), 8 % missed detections, box jitter, randomized motor response,
  random start heading, wind.
- **Same altimeter.** Sim altitude is a single downward ray capped at 6 m, like
  the AR.Drone's ultrasound sensor, so over hills both report height above
  what is below.
- **Matched limits.** The physics twin uses the AR.Drone's defaults (12° tilt,
  0.7 m/s climb, 100°/s yaw), and the driver configures the real drone to them.

## Who to follow: target identity

YOLO finds every person; the drone must follow one. On the phone the operator
taps the person once. From then on `PersonTargetTracker` picks, every frame, the
detection that matches both the target's recent position and its appearance
gallery: up to 40 **OSNet** re-identification embeddings collected from the
different views seen while tracking was certain. If the target disappears behind
someone, it is re-acquired by appearance rather than by position. Embeddings are
a few hundred floats per view, so the gallery is a plain in-memory array
compared by cosine similarity – no vector database needed.

## Requirements

| | Version |
|---|---|
| Unity | 6000.6.1f1 with Android Build Support |
| `com.unity.ai.inference` (Sentis) | 2.6.1 |
| `com.unity.ml-agents` | 4.2.0, Python `mlagents` 1.1 (conda env `mlagents`) |
| Hardware | Parrot AR.Drone 2.0, Android phone (ARM64, Vulkan) |

## Models

`osnet_reid.onnx` (OSNet x1.0, Market-1501, MIT, from
[Qualcomm AI Hub](https://huggingface.co/qualcomm/OSNet), merged into one file)
and the trained policy `DroneFollow.onnx` are committed. MiDaS and YOLO are too
large for git; download them once into `Assets/Models/`:

| File | Purpose | Input | Source |
|---|---|---|---|
| `model-small.onnx` | MiDaS v2.1 small – depth | 1×3×256×256 | [isl-org/MiDaS · v2_1](https://github.com/isl-org/MiDaS/releases/tag/v2_1) |
| `model-f6b98070.onnx` | MiDaS v2.1 large – optional | 1×3×384×384 | [isl-org/MiDaS · v2_1](https://github.com/isl-org/MiDaS/releases/tag/v2_1) |
| `yolov8n.onnx` | YOLOv8n COCO – persons | 1×3×640×640 | [212534/Unity-Sentis-YOLOv8](https://github.com/212534/Unity-Sentis-YOLOv8) |

```bash
cd Assets/Models
curl -LO https://github.com/isl-org/MiDaS/releases/download/v2_1/model-small.onnx
curl -LO https://github.com/isl-org/MiDaS/releases/download/v2_1/model-f6b98070.onnx
curl -L -o yolov8n.onnx https://github.com/212534/Unity-Sentis-YOLOv8/raw/main/Assets/Models/yolov8n.onnx
sha256sum -c <<'EOF'
2d8c6cb8f415229daf1eb041024208e2608c9f98e17c81cc7c6ecb449c56fd58  model-small.onnx
360583846cc3d0bac9c1735313a32140956b1badf20badc6ab9caf662136587d  model-f6b98070.onnx
0314eb248ba918efc3789b9d6782843f111b3dc210b94bdf317e7181a0e408ea  yolov8n.onnx
EOF
```

The Meshy-generated scenery (`Assets/Environment/`) came with 0.5–3 million
triangles per object, which made rendering – not learning – the training
bottleneck. It is committed decimated to ~30k triangles each
(`tools/scenery/`: Blender collapse-decimation, then only the geometry is
spliced back into the original GLB so materials, textures and Unity's prefab
references stay untouched).

## Getting started

```bash
git config core.hooksPath tools/git-hooks   # optional: push after every commit
```

- Training: [docs/DRONEFOLLOW_QUICKSTART.md](docs/DRONEFOLLOW_QUICKSTART.md),
  details in [docs/DRONEFOLLOW_TRAINING.md](docs/DRONEFOLLOW_TRAINING.md)
- Phone app and flying the real drone: [docs/ANDROID_INFERENCE_BUILD.md](docs/ANDROID_INFERENCE_BUILD.md)

## Perception notes

- **Framing:** the full 16:9 frame is stretched into each model's square input;
  a center crop would drop the outer ~44 % where people often are.
- **Depth normalization:** MiDaS outputs relative inverse depth with arbitrary
  scale; each frame is mapped from its 2nd–98th percentile to 0–1 with temporal
  smoothing.

## Credits & licenses

- **MiDaS** – Ranftl et al., Intel ISL, MIT – <https://github.com/isl-org/MiDaS>
- **YOLOv8** – Ultralytics, **AGPL-3.0**; ONNX export from
  [212534/Unity-Sentis-YOLOv8](https://github.com/212534/Unity-Sentis-YOLOv8).
  Check the AGPL obligations before redistributing a build.
- **OSNet** – Zhou et al., *Omni-Scale Feature Learning for Person
  Re-Identification*, MIT; ONNX export by Qualcomm AI Hub.
- MiDaS-in-Sentis reference: [Yupopyoi/MiDaS_UnitySentis](https://github.com/Yupopyoi/MiDaS_UnitySentis)
