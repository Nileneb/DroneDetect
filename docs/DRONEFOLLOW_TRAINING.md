# DroneFollow training

## Curriculum and episode rewards

`config/DroneFollow.yaml` advances the `follow_stage` environment parameter
when the smoothed mean reward passes each threshold, after at least 50 episodes.
The Unity agent applies the same stage to target motion and the maximum
controlled-flight duration:

| Stage | Flight duration | Target motion |
|---|---:|---|
| 0 | 60 s | Target roams within a 5 m radius |
| 1 | 90 s | Wider target movement with model-based person detection |
| 2 | 120 s | Moderate target motion with model-based person detection |
| 3 | 180 s | Full configured target motion/noise |
| 4 | 240 s | Full difficulty and longest flights |

Rewards are based on improvement in distance-band, image-centering, and altitude
errors rather than repeatedly charging the full absolute error. In addition,
the detected box earns up to `centeredTargetReward` when its center overlaps the
Main Camera crosshair, with the maximum at exact alignment. A smaller hold
reward is paid only while the target is detected, centered, at the desired
distance, and near the target altitude while the agent is airborne. Completing
the stage duration gives a separate completion reward. A controlled landing
receives its own reward only after the full stage flight duration. Earlier land
actions are rejected and penalized, so the agent cannot end an episode early to
avoid the follow task. At the duration limit, the training episode resets
directly to takeoff rather than waiting on the ground.

## Sentis depth and object detection

The scene setup attaches one `DroneFollowSentisPerception` to `Main Camera` and
selects MiDaS small by default, with the large model available as an inspector
alternative. Its source is the actual camera render (`OnRenderImage`), not the
preview `RawImage`; it passes the camera frame unchanged to the Game view,
stretches the full 16:9 frame into each model's square input (`inputFraming`;
`CenterCrop` drops the outer ~44% of the frame), and writes MiDaS output
(box-averaged) to the 84x84 grayscale RenderTexture used by the policy; the
`PiP_Depth` preview shows the same normalization at full model resolution
(256 small / 384 large), re-aspected to the camera frame. YOLOv8n
runs on the same framing at up to 10 Hz and supplies the highest-confidence
COCO `person` box to the existing four-value detector contract. There is no
simulated detection or Unity depth-buffer fallback: setup reports missing model
assets as errors instead of quietly training with different observations.

The model assets are expected at `Assets/Models/model-small.onnx`,
`Assets/Models/model-f6b98070.onnx`, and `Assets/Models/yolov8n.onnx`.
`DroneFollowSentisPerception` exposes inference rate, MiDaS output
range/inversion/normalization, and person confidence threshold for tuning. Model inference
and the ML-Agents RenderTexture observation transfer are separate costs; take
a fresh Unity profiler/timer capture before comparing training throughput.

MiDaS outputs scale-ambiguous relative inverse depth (Agent1: small ~60..1200,
large ~0..6000), so the default `PerFramePercentile` normalization maps the 2nd..98th
percentile of each frame to 0..1, smoothed over frames by `depthRangeAdaptation`.
The old fixed 0..4096 range left the small model almost uniformly white.
Models trained before this change saw different (and vertically flipped) depth
observations and need retraining.

The YOLO ONNX metadata identifies that model as AGPL-3.0. It is being used here
for local experiments; check the model's applicable license obligations before
redistributing the project or offering it as a network service. This is a
model-license notice, not legal advice.

The Play HUD overlays a center crosshair and a curriculum checklist. Lessons
below the current `follow_stage` are marked complete, the active lesson shows
its flight-time progress, and future lessons remain pending. The final long
flight is checked off after its duration is survived. GroundCam and depth
preview UI are scaled down for display; their render textures and the
84x84 policy observation are unchanged.

Impact costs increase with impact severity and accumulated damage. Damage also
reduces control authority; reaching terminal damage or entering Emergency ends
the episode with the terminal crash penalty. Collision damage is disabled once
controlled landing has started.

## Recording a manual demonstration

With the training scene open and the ML-Agents trainer stopped, select
**DroneDetect > DroneFollow > Record Manual Demonstration** in Unity. The editor
switches the agent to `HeuristicOnly`; this mode is keyboard-controlled, not an
automatic follower. The target wanders around its spawn point with varied
directions, a 5–12 m radius, and infrequent short pauses. The recorder runs
until Play mode is stopped.

Controls (focus the Game view): **WASD/arrows** to move, **Space/Left Shift** to change altitude,
**Q/E** to yaw, and **G** to request a controlled landing. Takeoff is automatic
at the start of each episode. Early landing requests are rejected until the
current episode's flight duration is complete. Stop Play mode when the
demonstration is long enough. The new recording replaces
`results/demonstrations/DroneFollowDemo.demo` only after a recording file has
been created; the previous training demo remains available if recording or
publishing fails. This is the path already configured for behavioral cloning.

After recording, start a new trainer run to load the updated demo; a trainer
that is already running does not reload demonstration files. `config/DroneFollow.yaml` warm-starts PPO with behavioral cloning from the
recording. Start it with:

```sh
tools/train.sh drone_follow_real_v1
```

Wait until the terminal reports that it is listening on port `5004`, then
press Play in the training scene. The trainer waits up to 60 seconds for Unity
to connect; if Play was already active before the trainer started, stop and
restart Play after the trainer is listening. Do not start the manual-demo
recording menu during training: it deliberately switches to `HeuristicOnly`
and requires the PPO trainer to be stopped.

## Vegetation collision and NavMesh setup

With training and Play mode stopped, select
**DroneDetect > DroneFollow > Optimize Vegetation Prefabs and Bake NavMesh**.
The tool replaces tagged vegetation instances that use MeshColliders with
shared prefabs under `Assets/Prefabs/DroneFollow/Vegetation`. Visual GLB meshes
are kept intact; collision is changed to a small capsule for tall vegetation
and a box for low vegetation. These simple colliders reduce per-instance
triangle collision work. The tool creates the `NavMeshGeometry` user layer,
places the Ground collider and vegetation proxies on it, then bakes a
`NavMeshSurface` from physics colliders on that layer.

`ProjectSettings/ProjectSettings.asset` enables Unity's collision-mesh
prebaking for any MeshColliders that remain elsewhere in the project. This is
an import/build-time tradeoff: it can reduce runtime mesh cooking but increases
stored data and memory use. The current simulated target still moves through
`SimulatedMovingTarget`; baking the NavMesh does not by itself make that target
follow NavMesh paths.

The ML-Agents RenderTexture sensor currently reads pixels synchronously from
the GPU into a CPU `Texture2D` before sending observations to the trainer.
In the latest capture (8m 22s, 1,331 observations), sensor writes took 63.5s
in total (47.7ms per observation). The visible Main Camera must remain enabled
for the Game view. Observation-driven camera rendering only applies to a
separate offscreen sensor camera with a `targetTexture`; the current shared
Main Camera is not throttled. GPU inference would not remove the required CPU
observation transfer to the Python trainer. Compare a fresh ML-Agents timer and
Unity CPU Profiler capture before replacing the sensor path with asynchronous
readback, which would also introduce observation latency.
