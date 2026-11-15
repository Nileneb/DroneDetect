# Follow Path on Terrain Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Calm, fixed target paths on a hilly terrain with gate rewards, ultrasound altitude, a
continuous-only policy and Xbox-controller demos.

**Architecture:** Scene-object paths (`DroneFollowPath` + child waypoints) walked by
`DroneFollowPathWalker`; trigger gates (`DroneFollowGate`) report to `DroneFollowAgent`; pure
reward math in `DroneFollowRewards` (EditMode-tested); one editor menu builds terrain, pad,
vegetation, paths and gates; setup wires everything into `Agent1`.

**Tech Stack:** Unity 6000.6, ML-Agents, Input System (`Gamepad`), NUnit EditMode tests.

**Spec:** `docs/superpowers/specs/2026-11-15-follow-path-terrain-design.md`

## Global Constraints

- Distance band 2–3 m, desired altitude 1.5 m, ultrasound range 6 m, altitude obs `altitude / 10`.
- 4 paths, each 2–3 min of walking at 0.6–1.2 m/s; loops start and end at the pad.
- Policy: 4 continuous actions, no discrete branch; 19 vector observations unchanged.
- No edits under `Assets/` while the editor trains/records; no second Unity instance.
- Exclude `Meshy_AI_Balanced_Rock` (contains a human figure) from scattering.

## Review Focus

1. Drone above a steep slope or over a rock: ultrasound reads the rock, landing must still finish
   (landing uses ray clearance, not world y = 0).
2. Drone never reaches a gate (lost target): episode must still end via target-finished + timeout.
3. Out-of-order gate entry or re-entering an already-passed gate: no reward, no exception.
4. Target walking up/down hills: stays on the surface, never floats or sinks.
5. No gamepad connected during manual recording: keyboard fallback still flies.

---

### Task 1: Reward math + contract

**Files:** Create `Assets/Scripts/DroneFollowRewards.cs`, `Assets/Editor/Tests/DroneFollowRewardsTests.cs`;
modify `Assets/Scripts/DroneFollowContracts.cs` (drop `DiscreteBranchSize`, `DiscreteActionCount`, `LandAction`).

**Produces:**
- `static float DroneFollowRewards.GateReward(float visibleFraction, bool visibleNow, float distance, float min, float max)` → 1 when visible now, visibleFraction ≥ 0.5 and distance in [min,max]; 0.4 when visible (both visibility conditions) but out of band; 0 otherwise.
- `static float DroneFollowRewards.DistanceError(float distance, float min, float max, float tooCloseDistance = 1.5f, float tooCloseWeight = 3f)` → 0 in band; `max - distance` style linear outside; below `tooCloseDistance` adds `tooCloseWeight * (tooCloseDistance - distance)`.

- [ ] Tests: `GateReward_InBandAndVisible_IsOne`, `GateReward_VisibleOutOfBand_IsPartial` (0.4), `GateReward_NotVisible_IsZero`, `GateReward_MostlyLostBeforePass_IsZero` (fraction 0.3 → 0), `DistanceError_TooCloseIsWorseThanTooFar` (error(1.0) > error(4.0)), `DistanceError_InBand_IsZero`.
- [ ] Implement; compile with dotnet build; run EditMode tests via Unity Test Runner.

### Task 2: Ultrasound altitude + terrain-aware landing

**Files:** modify `Assets/Scripts/SimulatedDroneController.cs`.

- `public float ultrasoundRange = 6f;` `float GroundClearance()` = downward `Physics.Raycast` from collider bottom center (body-down direction), ignoring own colliders and triggers (`QueryTriggerInteraction.Ignore`); out of range → `ultrasoundRange`.
- `navdata.altitude` = ray distance from drone origin to hit (clamped to range).
- Landing target height interpolates toward `groundY + 0.05` where `groundY` = hit point y at landing start; touchdown test uses ray clearance < 0.15.

### Task 3: Paths, walker, gates (runtime)

**Files:** create `Assets/Scripts/DroneFollowPath.cs`, `DroneFollowPathWalker.cs`, `DroneFollowGate.cs`; delete `SimulatedMovingTarget.cs`, `DroneFollowDemoRoute.cs`.

**Produces:**
- `DroneFollowPath : MonoBehaviour` { `int difficulty`; `IReadOnlyList<Vector3> Waypoints` (children in order); `IReadOnlyList<DroneFollowGate> Gates`; `float Length`; `void SetGatesActive(bool)` }.
- `DroneFollowPathWalker : MonoBehaviour` { `void Begin(DroneFollowPath path, bool reverse, float speed)`; `bool Finished`; `float Progress01`; `int WaypointsReached`; `LayerMask groundMask` }. Snaps to ground by raycast from 50 m above; faces walking direction; pauses `pauseSeconds` at the end; drives `Animator` float `Speed` / legacy `Animation` if present.
- `DroneFollowGate : MonoBehaviour` { `DroneFollowPath path`; `int index`; trigger `BoxCollider`; `OnTriggerEnter` → `GetComponentInParent<DroneFollowAgent>()` on the collider → `agent.OnGateEntered(this)` }.

### Task 4: Agent rework

**Files:** modify `Assets/Scripts/DroneFollowAgent.cs`.

- Remove discrete branch handling, `earlyLandingPenalty`, `safeLandingMinimumFlightFraction`, fixed `flightDurationsSeconds`.
- `public DroneFollowPath[] paths; public float gateReward = 0.5f;` stage = `follow_stage` → eligible paths `difficulty <= stage`; random path, random reverse, speed `Random.Range(0.6f, Mathf.Lerp(0.8f, 1.2f, stage / 3f))`.
- Spawn at path start (pad) facing the first waypoint; `_episodeDecisionLimit = ceil((path.Length / speed + 20 s) * 1.3 * 10 Hz)` safety timeout.
- `public void OnGateEntered(DroneFollowGate gate)`: only `gate.index == _nextGate` counts; reward `gateReward * DroneFollowRewards.GateReward(...)`; visibility fraction tracked over last 10 decisions.
- Walker finished → keep flying 3 s → `controller.Land()` → on Landed: completion reward `flightCompletionReward * gatesPassedFraction * damageScale`, `EndEpisode()`. Timeout → `EndEpisode()` without completion.
- Distance shaping uses `DroneFollowRewards.DistanceError`.
- Heuristic: Input System `Gamepad.current` Mode 2 (left stick y = up, x = yaw; right stick y = forward, x = left (negated)), deadzone 0.1; keyboard fallback.

### Task 5: Editor — terrain, pad, vegetation, paths, gates; setup + demo tools

**Files:** create `Assets/Editor/DroneFollowTerrainBuilder.cs`; modify `DroneFollowSetup.cs`, `DroneFollowGroundStationSetup.cs`, `DroneFollowDemoTools.cs`, `DroneFollowVegetationTools.cs` (drop NavMesh bake).

- Menu `DroneDetect/DroneFollow/Build Terrain, Paths and Gates`: 200×200 m Terrain (Perlin hills, max 8 m, flattened 4 m corridor along paths and a flat pad area), tag `Ground`; pad cube 1.5×0.1×1.5 m; 4 loop paths (difficulty 0–3, 150–200 m long so 2–3 min at 1–1.2 m/s); scatter ~300 optimized vegetation prefabs outside 4 m path corridor; gates via `BuildGates`.
- Menu `DroneDetect/DroneFollow/Rebuild Path Gates`: re-snap waypoints to terrain, regenerate gates (after hand edits).
- Gates: BoxCollider trigger 8 m wide × 4 m tall × 0.3 m deep, perpendicular to path, one per waypoint except index 0.
- Setup: walker on target, `paths` assigned, `ActionSpec.MakeContinuous(4)`; old ground plane disabled if terrain exists.

### Task 6: Config + docs

- `config/DroneFollow.yaml`: 4 lessons (path difficulty 0–3), thresholds retuned, demo path → `results/demonstrations/` manual demo file name; remove LongFlight.
- `docs/DRONEFOLLOW_TRAINING.md` and README: new flow (build terrain → configure → record 8 Xbox demos → train).
