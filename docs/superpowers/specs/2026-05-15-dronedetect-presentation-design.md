# DroneDetect — Presentation-Ready Design
**Date:** 2026-05-15  
**Deadline:** Monday (64h from now)  
**Goal:** Fix console errors, add depth-camera pipeline, expand parcours, start training run v8_1

---

## Phase 1 — HUD Compile-Error Fix

### Problem
`DroneHud.cs` references 9 public properties on `DroneAgent` that do not exist, causing compile errors that block the entire project.

### Solution
Add readonly public properties to `DroneAgent.cs` as thin delegates:

| Property | Source |
|---|---|
| `CurrentDroneState` | `_drone?.State ?? DroneState.Landed` |
| `Altitude` | `_drone?.Navdata.altitude ?? 0f` |
| `Speed` | `sqrt(vx²+vy²+vz²)` from Navdata |
| `Battery` | `_drone?.Navdata.battery ?? 0f` |
| `TargetsCollected` | `_targetsCollected` |
| `TotalTargets` | `_totalTargets` |
| `AllTargetsCollected` | `_targetsCollected >= _totalTargets && _totalTargets > 0` |
| `StepsInEpisode` | `_stepsInEpisode` |
| `CurrentEpisodeStepLimit` | `episodeStepLimit` |

Also add `DroneHUD.CreateHUD(this)` at the end of `Initialize()` — currently the HUD is never instantiated.

---

## Phase 2 — Depth Camera Pipeline

### Architecture

```
Drone GameObject
  └── DepthCamera (child GameObject)
        ├── Camera (depthTextureMode = Depth, Culling = Everything, no audio)
        ├── DroneDepthCamera.cs       ← new script
        └── RenderTextureSensorComponent  ← ML-Agents built-in
              └── RenderTexture 32x32 RFloat (single channel)
```

### DroneDepthCamera.cs
- `public int depthUpdateInterval = 5` — update every N agent steps
- Tracks step count via `OnStepTaken()` callback (or public `NotifyStep()` called from `DroneAgent.OnActionReceived`)
- `OnRenderImage(src, dst)`: Blit with `DepthVisualize.shader` → writes linear depth [0..1] to `depthRT`
- `depthRT` is the same RT assigned to `RenderTextureSensorComponent`

### DepthVisualize.shader (minimal Unlit)
- Samples `_CameraDepthTexture`
- Outputs `Linear01Depth(depth)` as grayscale
- Single pass, no lighting

### ML-Agents integration
- `RenderTextureSensorComponent` on drone: `RenderTexture = depthRT`, `SensorName = "DepthSensor"`, grayscale
- Training config: add `vis_encode_type: simple` — triggers CNN encoder for visual inputs
- Observation space: 14 vector obs (unchanged) + 32×32×1 depth image

---

## Phase 3 — Parcours Expansion

### Level System (updated)

| Level | Targets | Obstacles | New |
|---|---|---|---|
| 0 | 1 | none | — |
| 1 | 2 | none | — |
| 2 | 3 | 1 static | — |
| 3 | all | static | — |
| 4 | all | moving | **new** |

### Position Jitter
In `ConfigureParcours()`, after selecting a layout, apply random ±offset to each target within `[Header] jitterBounds` (Vector3, default `(1.5, 0.5, 1.5)`). This gives effectively infinite layout variation without new scene objects.

### MovingObstacle.cs (new, ~30 lines)
- `public Transform pointA, pointB`
- `public float speed = 1.5f`
- `public bool startAtA = true`
- Ping-pong via `Vector3.MoveTowards` in `Update()`
- Activated only when `DroneAgent.EnableObstacles(true)` at Level ≥ 3, moving only at Level 4

### DroneAgent changes
- `GetTargetCountForLevel`: add `case 4: return targets.Count`
- `EnableObstacles`: pass level to toggle `MovingObstacle.enabled` based on level >= 4
- Add `public Vector3 targetJitterBounds = new Vector3(1.5f, 0.5f, 1.5f)`

---

## Phase 4 — Training Config + Run v8_1

### DroneCSI.yaml changes
```yaml
network_settings:
  normalize: true
  hidden_units: 256
  num_layers: 3
  vis_encode_type: simple   # ← new: CNN for depth input

max_steps: 1000000          # ← doubled from 500k
```

### Curriculum config addition
```yaml
environment_parameters:
  parcours_difficulty:
    curriculum:
      - name: Level0
        completion_criteria: {measure: reward, behavior: DroneCSI, threshold: 1.5, min_lesson_length: 100, signal_smoothing: true}
        value: 0
      - name: Level1
        completion_criteria: {measure: reward, behavior: DroneCSI, threshold: 2.5, min_lesson_length: 100, signal_smoothing: true}
        value: 1
      - name: Level2
        completion_criteria: {measure: reward, behavior: DroneCSI, threshold: 3.5, min_lesson_length: 100, signal_smoothing: true}
        value: 2
      - name: Level3
        completion_criteria: {measure: reward, behavior: DroneCSI, threshold: 5.0, min_lesson_length: 100, signal_smoothing: true}
        value: 3
      - name: Level4
        value: 4
```

### Run command
```bash
mlagents-learn config/DroneCSI.yaml --run-id=v8_1
```

---

## Files Changed / Created

| File | Change |
|---|---|
| `Assets/Scripts/DroneAgent.cs` | +9 public properties, +HUD init call, +jitter, +level-4 support |
| `Assets/Scripts/DroneHud.cs` | no change needed (already correct) |
| `Assets/Scripts/DroneDepthCamera.cs` | **new** |
| `Assets/Scripts/MovingObstacle.cs` | **new** |
| `Assets/Shaders/DepthVisualize.shader` | **new** |
| `config/DroneCSI.yaml` | +vis_encode_type, +max_steps, +curriculum |

---

## Success Criteria for Presentation
- [ ] Unity compiles without errors
- [ ] HUD visible in Play Mode
- [ ] Depth camera renders in Scene View
- [ ] Level 4 with moving obstacles works
- [ ] v8_1 training run started, TensorBoard shows learning curve
