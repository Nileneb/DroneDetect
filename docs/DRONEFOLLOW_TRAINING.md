# DroneFollow training

## Curriculum and episode rewards

`config/DroneFollow.yaml` advances the `follow_stage` environment parameter
when the smoothed mean reward passes each threshold, after at least 50 episodes.
The Unity agent applies the same stage to target motion, detector noise/latency,
and the maximum controlled-flight duration:

| Stage | Flight duration | Target/detector |
|---|---:|---|
| 0 | 60 s | Target roams within a 5 m radius |
| 1 | 90 s | Wider target movement and low detector noise |
| 2 | 120 s | Moderate target motion and detector noise |
| 3 | 180 s | Full configured target motion/noise |
| 4 | 240 s | Full difficulty and longest flights |

Rewards are based on improvement in distance-band, image-centering, and altitude
errors rather than repeatedly charging the full absolute error. A small reward
is paid only while the target is detected, centered, at the desired distance,
and near the target altitude while the agent is airborne. Completing the stage
duration gives a separate completion reward. A controlled landing receives its
own reward only after the full stage flight duration. Earlier land actions are
rejected and penalized, so the agent cannot end an episode early to avoid the
follow task. At the duration limit, the
training episode resets directly to takeoff rather than waiting on the ground.

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
publishing fails. This is the path already configured for both behavioral
cloning and GAIL.

After recording, start a new trainer run to load the updated demo; a trainer
that is already running does not reload demonstration files. The
`config/DroneFollow.yaml` run is PPO-only. The
`config/drone_training.yaml` additionally enables behavioral cloning and GAIL
from the current manual recording. Start it with:

```sh
mlagents-learn config/drone_training.yaml --run-id=drone_follow_demo_v1
```

Wait until the terminal reports that it is listening on port `5004`, then
press Play in the training scene. The trainer waits up to 60 seconds for Unity
to connect; if Play was already active before the trainer started, stop and
restart Play after the trainer is listening. Do not start the manual-demo
recording menu during training: it deliberately switches to `HeuristicOnly`
and requires the PPO trainer to be stopped.
