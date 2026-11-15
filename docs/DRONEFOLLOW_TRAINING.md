# DroneFollow training

## World, paths and gates

**DroneDetect > DroneFollow > Build Terrain, Paths and Gates** (Play mode stopped) builds
`DroneFollowWorld` in the open scene and disables the old flat ground, walls and `Vegetation` root:

- a 200 x 200 m terrain (`Ground` tag) with a flat area around the pad and hills up to 8 m that grow
  steeper per quadrant (data in `Assets/Environment/DroneFollowTerrain/`);
- `LandingPad`, a 1.5 x 0.1 x 1.5 m cube the drone starts from;
- four closed walking loops `Paths/Path<i>_Difficulty<i>` (difficulty 0 = flat and gentle ... 3 = hilly
  with tight turns, roughly 130–190 m, 2–3 min of walking). Waypoint `W00` sits 4 m from the pad;
- one invisible trigger gate (8 m wide, 4 m tall, layer *Ignore Raycast*) per waypoint except `W00`;
- ~220 optimized rocks/trees, at least 5 m from every path and 12 m from the pad, 40 % of them just
  beside the paths so they appear in the follow camera. `Balanced_Rock` is never used (it contains a
  human figure YOLO would detect).

Paths are plain scene objects: drag the children of `Waypoints` around, add or delete some, then run
**DroneDetect > DroneFollow > Rebuild Path Gates**, which snaps the waypoints onto the ground and
regenerates the gates. Then run **DroneDetect > Configure Active Scene for DroneFollow**: it adds
`DroneFollowPathWalker` to the target, assigns all paths to the agent, places the target at the first
waypoint and the drone on the pad, and sets the 4-action continuous contract.

## Episodes, curriculum and rewards

Each episode picks a path with `difficulty <= follow_stage`, randomly forward or reverse, and a
walking speed between 0.6 m/s and up to 1.2 m/s (the upper bound grows with the stage). The target
walks the loop at that speed, sometimes pausing 1–3 s at a waypoint, and stays on the ground by a
downward ray. The drone takes off from the pad. When the target is back at `W00`, the drone keeps
following for 3 s, then the simulation lands it where it is and the episode ends after touchdown.
A safety timeout (walk time + margin) ends episodes in which that never happens; it pays no
completion reward.

| Stage | Paths used | Target speed |
|---|---|---|
| 0 | flat loop | 0.6–0.9 m/s |
| 1 | + gentle hills | up to ~1.0 m/s |
| 2 | + hills and tighter turns | up to ~1.1 m/s |
| 3 | all four loops | up to 1.2 m/s |

`config/DroneFollow.yaml` advances the stage when the smoothed mean episode return passes 40 / 45 / 50
after at least 100 episodes. These thresholds are estimates (see the comment there); check them in
TensorBoard after the first run.

Rewards:

- **Gates (largest single reward, `gateReward` 0.5):** the next gate in walking order pays fully when
  the drone flies through it with the person detected now and in at least half of the last 10
  decisions, at 2–3 m distance; 40 % if visible but outside that distance; nothing if the person was
  lost. A gate only counts after the target has walked past it (the drone trails), skipped gates
  earn nothing, earlier gates are ignored.
- **Dense shaping:** improvement of distance-band, image-centering and altitude errors, a centered
  bonus, a hold bonus while detected, centered, at distance and altitude, a small lost-detection
  penalty and an action-smoothness penalty. The distance error counts triple below 1.5 m, so flying
  at the person is clearly worse than trailing too far.
- **Completion:** `flightCompletionReward` on touchdown after the walk, scaled by the share of gates
  passed well and reduced by damage.

Landing is not a policy action anymore. On the real drone the operator presses **LAND** in the
ground station, which sends the AR.Drone's native controlled descent.

## Ultrasound altitude

`SimulatedDroneController` reports `navdata.altitude` like the AR.Drone 2.0: the distance along the
drone's body-down axis to the nearest surface, capped at `ultrasoundRange` (6 m). Over hills and
rocks this is height above what is below, not world height; on a slope it is the slant distance,
exactly as the real sensor reads. The policy still sees `altitude / 10`; the altitude reward uses
the same value. Sim landing descends to the ground found by a downward ray, not to world y = 0.

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
flight time and gates passed well, and future lessons remain pending. The final
stage is checked off once a full-course episode passes at least 75 % of its gates. GroundCam and depth
preview UI are scaled down for display; their render textures and the
84x84 policy observation are unchanged.

Impact costs increase with impact severity and accumulated damage. Damage also
reduces control authority; reaching terminal damage or entering Emergency ends
the episode with the terminal crash penalty. Collision damage is disabled once
controlled landing has started.

## Recording manual demonstrations (Xbox controller)

Connect an Xbox controller, open the training scene with the trainer stopped and select
**DroneDetect > DroneFollow > Record Manual Demonstration**. The agent switches to `HeuristicOnly`
and reads the controller through the Input System:

| Control | Action |
|---|---|
| Left stick | move: forward/back, sideways |
| Right stick (left/right) | look: yaw |
| R2 / L2 | climb / descend (analog) |

A 10 % deadzone filters stick drift. Without a controller the keyboard still works (WASD/arrows,
Space/Left Shift, Q/E), but its commands are only full or zero, which the policy would imitate.
Each episode is one path: fly smoothly behind the person at 2–3 m and ~1.5 m altitude; the
simulation lands after the walk and starts the next path. Stop Play mode to keep the recording.
Recordings accumulate as separate files in `results/demonstrations/manual/`, and training loads all
of them. Plan: each of the four paths once forward and once reverse (~25 min). Delete a bad flight's
file from that folder before training.

Recordings made before the 4-action contract (with the land branch) are incompatible and must not
be in that folder.

## Recording demonstrations with the scripted expert

**DroneDetect > DroneFollow > Record Scripted Expert Demonstration** still flies the ground-truth PD
follower through random stages at training speed and publishes `results/demonstrations/DroneFollowDemo.demo`.
It is no longer used by the training config; point `demo_path` at it only for comparison runs.

After recording, start a new trainer run to load the updated demo; a trainer
that is already running does not reload demonstration files. `config/DroneFollow.yaml` warm-starts PPO with behavioral cloning and GAIL from the
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

## Vegetation collision

**DroneDetect > DroneFollow > Rebuild Vegetation Prefabs (Mesh Colliders)** regenerates the shared
prefabs under `Assets/Prefabs/DroneFollow/Vegetation` from `Assets/Prefabs/Vegetation`: every mesh
gets a non-convex `MeshCollider` on its decimated (~30k triangle) geometry, tagged `Obstacle`, so the
drone collides with branches and overhangs exactly where they are, like the real drone would. Scene
instances keep their prefab links and update in place. Hitting `Obstacle`, `Ground` or `Wall` costs
impact damage. There is no NavMesh anymore; **Configure Active Scene** deletes old NavMesh objects
and components.

`ProjectSettings/ProjectSettings.asset` enables Unity's collision-mesh
prebaking for any MeshColliders that remain elsewhere in the project. This is
an import/build-time tradeoff: it can reduce runtime mesh cooking but increases
stored data and memory use.

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
