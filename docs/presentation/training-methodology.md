# Training Methodology — DroneDetect

Vollständige Dokumentation der zweiphasigen ML-Agents-Pipeline.

## Phase 1: DroneCSI (Flight Foundation)

### Ziel
Agent lernt grundlegende 3D-Drohnen-Navigation mit realistischer Physik (AR-Drone 2.0 Digital Twin).

### Setup

| Parameter | Wert | Begründung |
|-----------|------|-----------|
| Algorithm | PPO | Sample-efficient für continuous actions |
| Hidden Units | 256 | Drohnen-Dynamik ist mehrdimensional |
| Layers | 3 | Tiefe ggü. Breite — kompakte features |
| Batch Size | 512 | Stabilität bei kleinen RTX-VRAMs |
| Buffer Size | 10240 | 20× batch → 20 PPO-Updates pro rollout |
| LR | 3e-4 (linear decay) | Standard für PPO continuous |
| Beta | 5e-3 | Entropy bonus für Exploration |
| Epsilon | 0.2 | PPO clipping ratio |
| Lambda | 0.95 | GAE-Λ |
| Num Epochs | 3 | Trade-off Stabilität vs. sample efficiency |
| Gamma | 0.995 | Long-horizon credit assignment (Drohne braucht Vorausplanung) |
| Time Horizon | 256 | Lange Episode-Bootstrap-Distance |
| Max Steps | 1,000,000 | Reichlich Headroom (tatsächlich konvergent bei 150k) |

### Curriculum Design

Zwei orthogonale Schwierigkeitsachsen, beide mit reward-basierten Progressions-Thresholds:

| Lesson | Parcours | Wind | Threshold (Reward) |
|--------|----------|------|---------------------|
| 0 | Level 0 (Tutorial) | 0.0 (still) | 1.5 |
| 1 | Level 1 (basic obstacles) | 0.3 | 2.5 |
| 2 | Level 2 (medium) | 0.6 | 3.5 |
| 3 | Level 3 (complex) | 0.85 | 5.0 |
| 4 | Level 4 (full) | 1.0 (storm) | terminal |

**Trick**: signal_smoothing=true verhindert Lesson-Hopping bei Rauschen.

### Observation Space (14 floats)

```
obs[0]    = altitude / 10            ∈ [0,1]
obs[1..3] = (vx, vy, vz) / [5,5,1]   ∈ [-1,1]
obs[4..6] = rotXYZ / [180,180,360]   ∈ [-1,1]
obs[7..9] = angVel / 5               ∈ [-1,1]
obs[10..12] = unit(target_dir local) ∈ [-1,1]^3
obs[13]   = clamp(dist/50, 0, 1)     ∈ [0,1]
```

Normalisierung ist wichtig — Range-Sprünge führen zu instabilen Gradients.

### Action Space (4 continuous)

```
action[0] = forward         ∈ [-1,1]
action[1] = strafe          ∈ [-1,1]
action[2] = vertical (up/down) ∈ [-1,1]
action[3] = yaw rotation    ∈ [-1,1]
```

### Reward Shaping (Phase 1)

Implementiert in `DroneAgent.cs`:
- Pro Target erreicht: **+1.0**
- Pro Step näher am Target: **+0.001 × Δdistance**
- Crash (collision): **−1.0** + EndEpisode
- Erfolg (alle Targets): **+5.0** + EndEpisode

### Results (real run, v8_1)

```
Final reward:           35.25 (random baseline: −4.24)
Final episode length:   297 steps (max: 300)
Steps trained:          149,994
Final lesson:           4 (max)
Wall time:              ~3h on RTX 4090
mlagents version:       1.1.0
PyTorch:                2.8.0+cu128
```

**Lesson transitions (visible in TensorBoard):**
- L0 → L1 at step 30k (reward ~2.1)
- L1 → L2 at step 50k (reward ~2.4)
- L2 → L3 at step 80k (reward ~13)
- L3 → L4 at step 140k (reward ~35)

## Phase 4: DroneShepherd (Protective Behavior)

### Ziel
Agent lernt, Schafe zu beschützen indem er Wölfe per Scarer-Pulse einschüchtert (statt Kollision).

### Setup Differences vs. Phase 1

```yaml
behaviors:
  DroneShepherd:
    trainer_type: ppo
    network_settings:
      hidden_units: 256
      num_layers: 3              # gleich wie Phase 1 → Transfer
    reward_signals:
      extrinsic:
        gamma: 0.99              # kürzerer Horizon als Phase 1
        strength: 1.0
      gail:
        strength: 0.5            # NEU: GAIL-Imitation
        demo_path: Assets/Demonstrations/DroneSessions/
        use_actions: true
    max_steps: 1_500_000         # 1.5x Phase 1 (komplexeres Verhalten)
    time_horizon: 128            # kürzer (taktischer)
environment_parameters:
  shepherd_round_duration: 300.0  # 5-min Episodes (User-Spec)
  wind_strength: 0.4
```

### Reward Function (Phase 4 NEU)

Implementiert in `ShepherdDroneAgent.cs` + `ShepherdGameManager.cs`:

| Signal | Formula | Trigger |
|--------|---------|---------|
| Fear-Delta-Gain | `+4 × max(0, fear[t] - fear[t-1])` | per FixedUpdate, while wolf alive |
| Panic-Latch-Bonus | `+3` | one-shot per panic-trigger |
| Sheep-Alive-Per-Step | `+0.0003 × N_active_sheep` | per FixedUpdate |
| Sheep-Caught-Penalty | `−3` | immediate, in OnSheepCaughtEvent |
| Drone-Crash-Penalty | `−1` + EndEpisode | when state == Emergency |
| Survived-Sheep-Bonus | `+5 × N_saved` | OnRoundEnded |
| Perfect-Defense-Multiplier | `× 1.5` | if N_saved == N_initial |
| Fast-Win-Bonus | `+0.05 × (300 - elapsed)` | if perfect defense |
| Survival-Ratio-Shaping | `endBonus ×= Lerp(0.2, 1.0, ratio)` | applied to final reward |

### Anti-Exploit: Survival-Ratio-Shaping

Naive Belohnungsverteilung wäre für den Agent ausnutzbar: ohne Survival-Ratio-Faktor könnte der Agent lernen, sich auf Wölfe zu stürzen und Furcht-Spam zu produzieren — Schafe sterben dabei, aber die Furcht-Rewards überwiegen.

Das Lerp(0.2, 1.0, ratio) macht das wirtschaftlich unattraktiv:
- 0% Schafe gerettet → 20% des End-Bonus
- 50% gerettet → 60% des End-Bonus
- 100% gerettet → 100% des End-Bonus × 1.5 (Perfect-Defense-Multiplier)

### GAIL (Generative Adversarial Imitation Learning)

Damit der Agent sinnvolle Strategien schneller findet, mixen wir extrinsic + GAIL-rewards:

```
total_reward = extrinsic(env) + 0.5 × discriminator(state, action vs. human_demos)
```

**Demo-Erfassung-Pipeline:**
1. User spielt im WebGL-Build via `app.linn.games/shepherd/play`
2. ShepherdGameManager loggt 4-Hz-Position-Tracks (`RecordEvent`)
3. Batch-Upload zu `app.linn.games/api/shepherd/sessions/:code/events`
4. Backend exportiert nach `Assets/Demonstrations/DroneSessions/*.demo` (Unity-Format)
5. Training-Run pickt sie via `demo_path` auf

**Transfer Init:** Phase-4-Training startet mit `DroneCSI.onnx` als Initial-Policy (`--initialize-from results/v8_1`). Spart 50%+ Konvergenz-Zeit.

## Running a Training Job

### Setup (one-time)

```bash
conda activate mlagents   # /home/nileneb/miniconda3/envs/mlagents
mlagents-learn --help    # sanity check
```

### Phase 1 (DroneCSI)

```bash
cd /home/nileneb/DroneDetect
mlagents-learn config/DroneCSI.yaml \
    --run-id=DroneCSI_v9_$(date +%Y%m%d) \
    --time-scale=10 \
    --num-envs=4 \
    --no-graphics
# Unity läuft separat, connected via gRPC port 5004 default.
# Editor: Edit > Project Settings > Editor > "Enter Play Mode"
```

### Phase 4 (DroneShepherd, mit Transfer-Init)

```bash
mlagents-learn config/DroneShepherd.yaml \
    --run-id=DroneShepherd_v1 \
    --initialize-from=results/v8_1 \
    --time-scale=10 \
    --no-graphics
```

### Monitoring

```bash
tensorboard --logdir results/ --port 6006
# → http://localhost:6006
```

Wichtige Charts zu beobachten:
- `Environment/Cumulative Reward` (sollte steigen)
- `Environment/Lesson Number/parcours_difficulty` (Curriculum-Progression)
- `Policy/Entropy` (sollte langsam abnehmen — wenn zu schnell: Exploration kollabiert)
- `Losses/Value Loss` (sollte sich stabilisieren)
- `Policy/Extrinsic Reward` vs `Policy/GAIL Reward` (Balance check)

### ONNX-Export

Nach Training-Ende erzeugt mlagents automatisch:
```
results/<run-id>/<BehaviorName>.onnx
```

Move to `Assets/Models/` for inference im WebGL-Build.

## Reproducibility Notes

- **GPU-Determinism**: nicht 100% reproducible wegen CuDNN-Heuristics
- **Seed**: ML-Agents nutzt `--seed=42` als default; set explizit für stabile Runs
- **Curriculum-Threshold-Sensitivity**: signal_smoothing=true ist hier essentiell, sonst flackert Lesson hin und her bei rauschigen Rewards
- **Demo-Path-Constraint**: GAIL braucht `.demo`-Files mit gleichem Action-Space wie Training-Agent — kein Mix-and-Match
