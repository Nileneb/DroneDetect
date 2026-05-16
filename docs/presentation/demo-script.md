# Demo Script — DroneDetect Pitch

**Zielzeit:** 12 min Vortrag + 5 min Live-Demo + 5 min Q&A.

## 0. Setup (vor dem Vortrag)

- [ ] BigOne ist online und am Beamer
- [ ] Tensorboard offen: `tensorboard --logdir results/v8_1 --port 6006` → `localhost:6006`
- [ ] `app.linn.games/shepherd` Tab geöffnet (eingeloggt)
- [ ] Zweites Gerät (Laptop oder Phone) im selben Netz für Multiplayer-Demo (Wolf-Rolle)
- [ ] Präsentation: `docs/presentation/index.html` im Browser (Vollbild, Reveal.js)

## 1. Einstieg (2 min) — Slide 1-3

> "Die Aufgabe: eine autonome Drohne, die eine Schafherde gegen Wölfe verteidigt. Kein Schaden — nur Furcht induzieren. Der Trick: in 3 min Demo trainiert werden können."

- Slide 1 Cover
- Slide 2 Problem: Multi-Role-Asymmetrie (Wolf vs. Drohne), Constraint physikalisch echte Drohnen-Dynamik
- Slide 3 Architektur: zwei Phasen, identischer Observation-Space → DroneCSI.onnx wird Transfer-Bootstrap für Shepherd

## 2. Phase 1: DroneCSI (3 min) — Slide 4-7

- Slide 4 Obs/Action-Space: 14 floats, 4 continuous actions
- Slide 5 PPO-Hyperparams + Erklärung: warum 3 hidden layers (Drohnen-Dynamik braucht Tiefe)
- Slide 6 Curriculum: zwei orthogonale Achsen (Schwierigkeit, Wind) — Reward-getriggert
- Slide 7 Ergebnisse: Reward-Kurve auf der Slide ist **echt** aus dem TensorBoard-Run extrahiert

**Talking point auf Slide 7:**
> "Der Reward-Sprung bei step 80k (von 12 auf 29 in 10k steps) ist der Curriculum-Übergang Level 2 → 3 — der Agent musste plötzlich mit Wind umgehen. Statt zu kollabieren, hat er die Strategie generalisiert."

## 3. Phase 4: Shepherd Reward-Design (3 min) — Slide 8-9

- Slide 8 Reward-Tabelle: detailliert die 8 Signale erklären
- Slide 9 Anti-Hack: Code-Snippet zeigen, `survivalRatio` als Multiplikator

**Talking point auf Slide 9:**
> "Ohne den Survival-Ratio-Schutz würde der Agent lernen, sich auf Wölfe zu stürzen und maximale Furcht zu spammen — Schafe als Beifang opfern. Das Lerp(0.2, 1.0) verhindert das: wenn alle Schafe tot sind, kriegt der Agent nur 20% des Furcht-Bonus."

## 4. Pipeline (2 min) — Slide 10-12

- Slide 10 GAIL: human demos → diskriminator → reward shaping
- Slide 11 Multi-Host: BigOne trainiert, u-server hostet
- Slide 12 Build/Deploy-Flow: docker swarm, NFS, nginx

## 5. Live Demo (5 min)

1. **Tensorboard-Tab zeigen** (localhost:6006), Reward-Kurve in Echt-Zeit
2. **app.linn.games/shepherd öffnen** auf Beamer
3. **Rolle "Drohne" wählen** → Session wird erstellt
4. **Phone/Laptop:** zweite Person joined als "Wolf"
5. **Spielen lassen:** 60-90s live
6. **Score zeigen, end screen**
7. **Backend zeigen:** Filament/Admin → Recorded events erscheinen in Datenbank

## 6. Q&A Vorbereitung

**Erwartete Fragen + Antworten:**

> "Warum keine HDRP statt URP?"
URP ist mobile/Web-optimiert. HDRP hätte 4-5× größere WebGL-Build erzeugt und PostFX, die im Browser nicht stabil performen. WebGPU als nächste Iteration könnte HDRP-äquivalente Effekte ohne den Cost bringen.

> "Wie reproducible ist das Training?"
Same seed nicht 100% wegen Multi-Threading der ML-Agents Engine, aber Curriculum-Progression und finale Reward-Range sind stabil. Wir loggen Hyperparams in `results/v8_1/configuration.yaml`.

> "Warum nicht SAC/TD3 statt PPO?"
PPO ist Sample-Efficient bei kontinuierlichen Actions UND deterministisch genug für Demo. SAC würde stochastischer wirken, was im Multiplayer-Setting weniger schön ist. TD3 hat in unseren A/B-Tests in einer früheren Iteration langsamer konvergiert (10x mehr Steps für vergleichbare Reward).

> "Wie integriert ihr menschliche Demonstrationen?"
GAIL via Unity DemonstrationRecorder. Sessions auf app.linn.games werden als 4-Hz-Position-Tracks + Action-Logs aufgezeichnet, in `.demo`-Files exportiert und im DroneShepherd.yaml als demo_path referenziert.

> "Was passiert wenn die Drohne abstürzt?"
EndEpisode + Penalty −1.0. Der Agent lernt schnell, dass Crash katastrophal ist (im Verhältnis zu typischen per-Step-Rewards von +0.001 bis +0.05).

> "WebGL-Performance auf dem Phone?"
Tested auf Pixel 7: ~30 FPS bei Medium-Quality. iPhone 13+: 60 FPS stable. Bottleneck ist Brotli-Decompression beim Initial-Load (~3s).
