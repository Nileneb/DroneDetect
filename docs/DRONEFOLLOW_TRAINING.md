# DroneFollow training

## Curriculum and episode rewards

`config/DroneFollow.yaml` advances the `follow_stage` environment parameter
when the smoothed mean reward passes each threshold, after at least 50 episodes.
The Unity agent applies the same stage to target motion, detector noise/latency,
and the maximum controlled-flight duration:

| Stage | Flight duration | Target/detector |
|---|---:|---|
| 0 | 12 s | Nearly stationary target, deterministic detections |
| 1 | 20 s | Gentle movement and low detector noise |
| 2 | 30 s | Moderate movement and detector noise |
| 3 | 45 s | Full configured target motion/noise |
| 4 | 60 s | Full difficulty and longest flights |

Rewards are based on improvement in distance-band, image-centering, and altitude
errors rather than repeatedly charging the full absolute error. A small reward
is paid only while the target is detected, centered, at the desired distance,
and near the target altitude while the agent is airborne. Completing the stage
duration gives a separate completion reward. A controlled landing receives its
own reward only after at least 80% of the stage flight duration. Earlier land
actions are rejected and penalized, so the agent cannot end an episode to avoid
the follow task.

Impact costs increase with impact severity and accumulated damage. Damage also
reduces control authority; reaching terminal damage or entering Emergency ends
the episode with the terminal crash penalty. Collision damage is disabled once
controlled landing has started.

## Recording scripted demonstrations

With the training scene open and the ML-Agents trainer stopped, select
**DroneDetect > DroneFollow > Record Scripted Demonstration (5000 steps)** in
Unity. The editor temporarily switches the agent to `HeuristicOnly`, then builds
a NavMesh from the Ground collider at runtime. The target starts four meters in
front of the drone and follows a repeatable loop of reachable NavMesh waypoints;
the drone's heuristic follows the target using the same detector output and
navdata exposed to the policy. The route and NavMesh are runtime-only and do
not modify the saved scene.

The recorder captures the vector, depth, and action streams, then automatically
stops after 5,000 decisions. The recording is written to
`results/demonstrations/DroneFollowDemo.demo`. The scene's previous PPO and
recorder settings are restored after Play mode. Older `DroneRouteDemo*.demo`
recordings are obsolete and should not be used for DroneFollow.

Review the demo in Unity/ML-Agents before using it for imitation learning. The
`config/DroneFollow.yaml` run is PPO-only. The migrated
`config/drone_training.yaml` additionally enables behavioral cloning and GAIL
from the current recording; use it only after confirming the scripted
controller follows the target reliably and the recorded observation/action
contract matches the DroneFollow policy. Start it with:

```sh
mlagents-learn config/drone_training.yaml --run-id=drone_follow_demo_v1
```

Wait until the terminal reports that it is listening on port `5004`, then
press Play in the training scene. The trainer waits up to 60 seconds for Unity
to connect; if Play was already active before the trainer started, stop and
restart Play after the trainer is listening. Do not start the scripted-demo
recording menu during training: it deliberately switches to `HeuristicOnly`
and requires the PPO trainer to be stopped.
