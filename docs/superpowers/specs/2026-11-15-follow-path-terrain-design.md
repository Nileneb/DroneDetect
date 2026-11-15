# DroneFollow: fixed target paths on terrain, human demos, gate rewards

Status: approved by the user (2026-11-15)

## Intent

The current training run showed the drone does not learn calm, controlled flight. Root cause as
the user sees it: the target roams randomly and erratically (`SimulatedMovingTarget`), and the
behavioral-cloning/GAIL demos come from the scripted ground-truth expert, which flies worse than a
human. Clean human demos on a calm, predictable target are expected to improve learning far more
than speeding training up.

So the training scene changes to:

1. A larger, realistic terrain map (hills) with many more objects.
2. The target person walks hand-authored, fixed paths. No NavMesh.
3. Invisible trigger planes ("gates") along each path make progress measurable and drive rewards
   and episode end.
4. A start/landing pad (cube): the drone starts on it, the path is a loop that ends there.
5. The user flies the demonstrations personally with an Xbox controller (analog sticks).
6. Simulated altitude comes from a single downward ultrasound ray, as on the real AR.Drone.
7. Landing is no longer a policy decision.

## Goals

- Policy learns smooth following at 2–3 m distance and ~1.5 m altitude over uneven terrain.
- Path, speed and episode length are fully controllable and reproducible.
- Sim observations stay identical in meaning to what the real AR.Drone 2.0 delivers.
- The user can record high-quality manual demos with analog input.

## Non-goals

- Parallel/headless training builds, server builds, scene pre-generation (dropped by the user:
  better demos instead of more throughput).
- A separate waypoint-flying pre-training stage (superseded by the calm fixed-path target).
- Gesture-based landing, landing-pad detection in perception.
- Procedurally generated paths.

## Anti-goals (would count as failure even if it "works")

- The drone flies closer than ~1.5 m to the person (real safety risk for the user).
- The drone learns to fly through gates instead of following the person.
- The policy depends on anything the real drone cannot observe (map knowledge, ground truth,
  absolute world height).
- Jerky demos (keyboard bang-bang input) that the policy then imitates.

## Constraints

- RL policy only; no classic fallback controller on the real drone.
- Real drone: Parrot AR.Drone 2.0, Android ground station. Navdata altitude on the real drone IS
  the ultrasound reading (range ~6 m).
- Never edit `Assets/` while Play mode is training/recording in the editor (recompile kills the
  run); never run a second Unity instance next to the editor on this PC (froze the machine).
- No Git LFS; new >50 MB assets stay gitignored (see README download list).
- `Agent1.unity` is binary-serialized → scene changes go through editor scripts
  (`DroneFollowSetup`-style menu items), not hand edits.
- Project has `activeInputHandler: 2` (legacy Input + Input System both active).

## Approaches considered

- **A. Waypoints as scene GameObjects (chosen).** A path is a parent GameObject with ordered child
  waypoints placed by hand on the terrain. An editor menu item generates the gate trigger planes
  from them. The target walks segment by segment and is snapped to terrain height by raycast.
  Simple, fully controllable, no new dependency.
- **B. Unity Splines package.** Smoother curves and more natural walking, but a new package, gates
  must be sampled from the curve, editing is fiddlier.
- **C. Procedurally generated paths.** Rejected: contradicts "fixed and fully controllable".

## Design

### Terrain map

- New Unity Terrain (built with the Terrain tool) replacing the flat ground plane in the training
  scene: rolling hills, larger than the current area. Tagged `Ground` (crash detection uses the
  tag).
- Scatter many more rocks/trees from the existing decimated Meshy prefabs in
  `Assets/Environment/{RocksGLB,TreesGLB}`. Reuse/extend `DroneFollowVegetationTools`; its NavMesh
  bake becomes obsolete and is removed.
- Objects must not block the paths (keep a corridor of ~3 m either side clear, since the drone
  trails 2–3 m behind the target) but should stand close enough to appear in the camera image as
  distractors and obstacles.

### Paths and target walker

- 4 hand-authored paths, each 2–3 minutes of walking (demo budget: each path flown forward and
  reverse once ≈ 25 min of manual flying). Each path: a parent GameObject (e.g. component `DroneFollowPath`) with
  ordered child waypoint transforms. Each path is a loop that starts and ends at the pad.
- Per episode a path is chosen at random, traversed forward or in reverse. Small randomization of
  walking speed (calm range, e.g. 0.6–1.2 m/s) and optional short pauses at chosen waypoints. No
  random direction changes.
- New walker component replaces `SimulatedMovingTarget` and `DroneFollowDemoRoute` (both deleted,
  together with the NavMesh code). Walks straight segments between waypoints, turns to face the
  walking direction, raycasts down each frame to stay on the terrain surface.
- Target model: `PrefabBene` (Meshy Straw Hat Wanderer GLB, ships animations): play a walk clip
  while moving and idle while paused, if the clips import usable; otherwise static is acceptable.
- Curriculum (`follow_stage`) is reinterpreted as path difficulty: paths are tagged with a
  difficulty level (flat/slow → hilly/faster/tighter turns); a stage samples paths up to its level.

### Gates (trigger planes)

- Editor menu item "DroneDetect/DroneFollow/Build Path Gates" generates, for every waypoint
  (except the pad), an invisible trigger plane (BoxCollider, isTrigger, no renderer) perpendicular
  to the path direction at that point, sized wide and tall enough to cover the drone's reasonable
  trailing positions (wider than the path, from ground to ~4 m).
- Gates belong to their path; only the current episode's gates are active, in order. The drone must
  pass them in sequence.
- The target's progress along its path defines the episode clock; the drone passing gates defines
  its reward.

### Rewards

Keep the existing dense shaping (distance-band, centering, altitude, follow-hold, lost-detection,
smoothness, crash damage). Changes:

- **Gate reward (largest single reward):** when the drone passes the next gate in order, reward is
  highest if at that moment the person is detected (in view) AND the distance to the person is in
  the 2–3 m band. Reduced if only one condition holds; none if the person is not visible.
- **Too close is worse than too far:** asymmetric distance penalty, clearly stronger below ~1.5 m
  than for trailing too far.
- Existing `desiredDistanceMin/Max = 2/3 m`, `desiredAltitude = 1.5 m` stay.
- Completion reward scaled by the fraction of gates passed correctly (and damage, as today).
- Gate conditions are checked at the moment of passing, plus the person must have been detected in
  the majority of the last ~1 s (so a corner-cutting pass with a lost target earns nothing).
  *(Claude's call.)*
- Gate order: only the next expected gate counts. Out-of-order or skipped gates earn nothing and
  are not penalized; the drone can still collect the following gates. *(Claude's call.)*
- Gate reward weight is tuned so following well between gates still dominates the episode return;
  the curriculum reward thresholds in `config/DroneFollow.yaml` are retuned to the new reward scale.

### Episode flow and landing

- Episode start: drone on the pad, takes off; target starts the chosen path at the pad.
- Episode end: target reaches the end of its loop (back at the pad) and stops; the policy keeps
  control while the target stands, then the sim auto-lands the drone wherever it is (controller
  `Land()`, no scripted return-to-pad), episode ends after touchdown. Landing position relative to
  the pad is not rewarded — following at 2–3 m already puts it near the pad. *(Claude's call.)* Also ends on terminal crash (unchanged) and on
  a safety timeout (path duration × margin) if the drone never lands.
- Episode length thus follows the path length; fixed `flightDurationsSeconds` go away.
- **Landing is removed from the policy:** drop the discrete land branch
  (`DiscreteBranchSize`, `DiscreteActionCount`, `LandAction`, early-landing penalty, related demo
  heuristic key). Policy outputs 4 continuous actions only. On the real drone the operator lands via
  a land button in the Android ground station (native AR.Drone land = controlled descent;
  `ArDroneFollowController.Land()` exists — verify a button is wired in the ground-station UI, add
  it if not).
- This changes the policy contract (actions only; the 19 vector observations stay unchanged) →
  existing models and `.demo` files become incompatible; retraining, re-recording demos and
  re-exporting the ONNX for the Android ground station are expected.

### Ultrasound altitude

- `SimulatedDroneController` computes `navdata.altitude` from a single raycast straight down
  (drone-local down or world down — builder's call, real sensor is body-fixed), max range ~6 m,
  instead of `transform.position.y`. Out of range → report max/last valid value consistent with
  the real AR.Drone behaviour.
- Observation normalization `altitude / 10` stays. On slopes a body-fixed ray reads slant distance;
  that is accepted as is, because the real sensor does the same. *(Claude's call.)*
- No extra RayPerceptionSensor: the value already enters the policy through the existing altitude
  observation, so sim and real stay identical. Altitude reward uses the same ultrasound value.

### Manual demos with Xbox controller

- `Heuristic()` reads analog sticks from an Xbox controller (Input System `Gamepad`), Mode-2 layout like an RC
  transmitter: left stick = up/down (vertical) + yaw (horizontal); right stick = forward/back +
  left/right. Small deadzone. Keyboard stays as fallback.
- The scripted expert stays available but is no longer the default demo source; config
  `DroneFollow.yaml` demo paths point to the user's recordings.
- The target walks its fixed path during recording exactly as in training.

## Left to the builder

- Exact terrain size, height profile and object count (target: clearly larger and denser than
  today while keeping the editor responsive).
- Gate dimensions, exact gate reward weights and the asymmetric distance penalty curve, documented
  in `docs/DRONEFOLLOW_TRAINING.md`.
- Speed ranges and pause placement per difficulty level.
- How curriculum stages map to path difficulty in `config/DroneFollow.yaml`.
- Down-ray frame (body vs. world) and out-of-range behaviour.
- Verify the Xbox controller enumerates as an Input System `Gamepad` on Linux.
