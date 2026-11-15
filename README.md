# DroneDetect

Unity-6 simulation for training a drone with **ML-Agents** to follow a person.
The drone perceives the world only through its own camera: **MiDaS** estimates a
depth map and **YOLOv8** detects people. Both run on-device via
**Unity Inference Engine (Sentis)**.

```
Main Camera ──► MainCameraRGB (640×360)
                   ├─► MiDaS  ──► 84×84 grayscale depth ──► policy observation
                   │                 └─► PiP "Depth" preview (full model resolution)
                   └─► YOLOv8n ─► best person box ──► policy observation + HUD
```

## Requirements

| | Version |
|---|---|
| Unity | 6000.6.1f1 |
| `com.unity.ai.inference` (Sentis) | 2.6.1 |
| `com.unity.ml-agents` | 4.2.0 |
| Python trainer | `mlagents` matching the package (see `config/`) |

## Models (download once)

The ONNX models are **not in git** (too large, see `.gitignore`). Their `.meta`
files are committed, so after downloading them into `Assets/Models/` Unity keeps
all prefab/scene references intact.

| File | Purpose | Input | Source |
|---|---|---|---|
| `model-small.onnx` | MiDaS v2.1 small – default depth model | 1×3×256×256 | [isl-org/MiDaS · release v2_1](https://github.com/isl-org/MiDaS/releases/tag/v2_1) |
| `model-f6b98070.onnx` | MiDaS v2.1 large – optional, sharper, slower | 1×3×384×384 | [isl-org/MiDaS · release v2_1](https://github.com/isl-org/MiDaS/releases/tag/v2_1) |
| `yolov8n.onnx` | YOLOv8n COCO – person detection | 1×3×640×640 | [212534/Unity-Sentis-YOLOv8](https://github.com/212534/Unity-Sentis-YOLOv8) |

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

`DroneCSI.onnx` (a trained policy from this project) is committed normally.

### Scenery assets

The Meshy-generated GLB trees and rocks (`Assets/Resources/TreesGLB/*.glb`,
`Assets/Resources/RocksGLB/*.glb`, 26–162 MB each) are also **not in git** –
GitHub rejects files over 100 MB. Copy them from an existing checkout into the
same folders; their `.meta` files are committed.

## Quickstart

1. Download the models (above), open `Assets/Scenes/Agent1.unity`.
2. Run **DroneDetect → Configure Active Scene for DroneFollow** – wires camera,
   models, depth sensor and HUD previews.
3. Press Play – Screen 2 shows the MiDaS depth, Screen 1 the YOLO person box.

Training, curriculum and Android builds:

- [docs/DRONEFOLLOW_QUICKSTART.md](docs/DRONEFOLLOW_QUICKSTART.md) – setup and starting a training run
- [docs/DRONEFOLLOW_TRAINING.md](docs/DRONEFOLLOW_TRAINING.md) – rewards, observations, Sentis perception details
- [docs/ANDROID_INFERENCE_BUILD.md](docs/ANDROID_INFERENCE_BUILD.md) – on-device inference build

## Perception notes

- **Framing:** the full 16:9 camera frame is stretched into each model's square
  input (`inputFraming = FullFrameStretch`). A square center crop would drop the
  outer ~44 % of the image, including people at the edges.
- **Depth normalization:** MiDaS outputs *relative* inverse depth with an
  arbitrary scale (small model ≈ 60–1200, large ≈ 0–6000). Each frame is mapped
  from its 2nd–98th percentile to 0–1, temporally smoothed via
  `depthRangeAdaptation`.
- **No ImageNet mean/std** on the input – an A/B test on this scene gave
  sharper depth with plain 0–1 RGB.

## Credits & licenses

- **MiDaS** – Ranftl et al., *Towards Robust Monocular Depth Estimation*, Intel ISL,
  MIT License – <https://github.com/isl-org/MiDaS>
- **YOLOv8** – Ultralytics, **AGPL-3.0**. The ONNX export comes from
  [212534/Unity-Sentis-YOLOv8](https://github.com/212534/Unity-Sentis-YOLOv8).
  Check the AGPL obligations before redistributing a build or offering it as a
  network service.
- MiDaS-in-Sentis reference implementation:
  [Yupopyoi/MiDaS_UnitySentis](https://github.com/Yupopyoi/MiDaS_UnitySentis)
