# DroneDetect — Status & Quickstart

Single-page Übersicht über alle Komponenten der ML-Agents → Standalone-Multiplayer Pipeline.

## Architektur-Pivot 2026-05-16

**Verworfen**: WebGL-Build-Deploy auf app.linn.games. Grund: Unity 6 alpha + URP + IL2CPP-WebGL
hängt bei "Compiling shader variants — Universal Render Pipeline/Lit" bei ~72 GB Variant-Output
selbst nach aggressivem Stripping. 3× Build-Versuche je 1-4h ohne Erfolg. Bug auch in Unity 6 LTS
6000.0.x reproduzierbar (laut User-Test).

**Neu**: Standalone-Clients (Linux/Win/Mac) + zentrale Koordination via app.linn.games. Server
trägt nur:
- JWT-Auth (Sanctum)
- Session-Lifecycle (create/join/start/end)
- Event-Recording (4Hz Position-Tracks)
- **NEU**: Demo-File-Upload (.demo Files für GAIL-Training)

Kein Docker Swarm nötig, keine GPU auf u-server. Training auf BigOne (RTX 4090) oder auf
User-Laptop (8 GB VGPU, 32 GB RAM → reicht für 1M-Step PPO).

## Komponenten

| Bereich | Status | Pfad / Befehl |
|---------|--------|---------------|
| Phase 1 Training (DroneCSI) | ✅ trainiert: 150k steps · reward 35.25 | `results/v8_1/DroneCSI.onnx` |
| Phase 4 Training (DroneShepherd) | ✅ konfiguriert · ⧗ Lauf ausstehend | `./tools/run_training.sh phase4` |
| Phase 4 Reward-Shaping | ✅ implementiert | `Assets/Scripts/ShepherdDroneAgent.cs` |
| Unity Compile Fix | ✅ `GetInstanceID → GetEntityId().GetHashCode()` | `Library/PackageCache/com.unity.ml-agents@.../Match3ActuatorComponent.cs` |
| Polyart-Bloat-Removal | ✅ raus (878 MB) — ohne 72 GB Shader-Reimport | `_unused_assets/Polyart/` |
| Standalone Build-Skript | ✅ Editor-Menü `Build > Linux Standalone ShepherdArena (Deploy)` | `Assets/Editor/BuildScript.cs` |
| WebGL Build | ⚠ broken in Unity 6 alpha + LTS | siehe Architektur-Pivot oben |
| Demo-Upload API (app.linn.games) | ✅ `POST /api/shepherd/demos/upload` (JWT) | `ShepherdController::uploadDemo`, branch `feature/shepherd-demo-upload` |
| Demo-Upload Unity Client | ✅ `DemoUploader.cs` hängt sich an `OnRoundEnded` | `Assets/Scripts/DemoUploader.cs` |
| Demo-Sync für Training | ✅ JWT-pull aller demos vor Trainingsstart | `tools/sync-demos-from-server.sh` |
| Shepherd Download Page | ✅ ZIP-Download statt WebGL-Embed | `views/shepherd/download.blade.php`, route `/shepherd/download` |
| Präsentation | ✅ Reveal.js-Slides + echte TB-Daten | `docs/presentation/index.html` |
| Demo Script | ✅ | `docs/presentation/demo-script.md` |
| Training Methodology Doc | ✅ | `docs/presentation/training-methodology.md` |
| Docker Swarm Setup-Docs | ⚠ obsolet (kein GPU auf u-server nötig durch Pivot) | siehe `deploy/swarm/INIT.md` — Architektur-Pivot oben |

## Quickstart-Befehle

### Training starten
```bash
# Phase 1 (DroneCSI — schon trainiert, neu nur wenn Reward-Funktion geändert wurde)
./tools/run_training.sh phase1

# Phase 4 (DroneShepherd — auto-transfer-init von neuestem DroneCSI run)
./tools/run_training.sh phase4

# Monitoring
./tools/run_training.sh tb  # → http://localhost:6006
```

### WebGL bauen
- **Editor offen**: Menü `Build > WebGL ShepherdArena (Deploy)`
- **Headless**: `./tools/build-webgl-batch.sh` (Editor muss vorher zu sein)

### Deploy auf u-server (Production)
```bash
# Lokaler Linux-Build → ZIP + rsync zur Production
./tools/zip-and-deploy-linux.sh --prod

# Windows + macOS: automatisch via GitHub Actions (siehe Build-Matrix unten)
gh workflow run cloud-build.yml          # manuell triggern
gh run watch                              # zuschauen
```

### Build-Matrix (Cross-Platform)

| Target | Wo | Wie | Output |
|--------|-----|-----|--------|
| Linux x64 | lokal BigOne | Unity-Menü `Build > Linux Standalone (Deploy)` → `tools/zip-and-deploy-linux.sh --prod` | `shepherd-linux-x64.zip` |
| Windows x64 (IL2CPP) | GitHub Actions (Ubuntu) | push auf `main` oder `gh workflow run cloud-build.yml` | `shepherd-windows-x64.zip` |
| macOS universal (Mono) | GitHub Actions (Ubuntu) | push auf `main` oder `gh workflow run cloud-build.yml` | `shepherd-macos-universal.zip` |

Alle drei landen in `app.linn.games/shepherd/downloads/` — Linux per `--prod` rsync,
Cloud-Builds via SSH-Deploy-Step im Workflow. Voraussetzung: secrets gesetzt
(siehe `.github/workflows/cloud-build.yml` Kommentar am Dateiende).

**Einmal-Setup für Cloud-Builds:**
```bash
# Unity-License (.alf → .ulf via license.unity3d.com/manual)
/home/nileneb/Unity/Hub/Editor/6000.4.7f1/Editor/Unity \
  -batchmode -nographics -quit -createManualActivationFile
# Datei hochladen → ULF zurück → als Secret setzen
gh secret set UNITY_LICENSE < Unity_v6000.4.7f1.ulf
gh secret set UNITY_EMAIL --body 'benedikt.linn@code.berlin'
gh secret set UNITY_PASSWORD --body '<dein-passwort>'

# SSH-Deploy-Key (separates Keypair nur für GH-Actions → u-server)
ssh-keygen -t ed25519 -f /tmp/deploy_key -N ''
ssh-copy-id -i /tmp/deploy_key.pub nileneb@192.168.178.12
gh secret set DEPLOY_SSH_KEY < /tmp/deploy_key
gh secret set DEPLOY_HOST --body '192.168.178.12'
gh secret set DEPLOY_USER --body 'nileneb'
gh secret set DEPLOY_PATH --body '/var/www/app.linn.games/public/shepherd/downloads'
rm /tmp/deploy_key /tmp/deploy_key.pub
```

### Präsentation öffnen
```bash
# Lokal (kein Server nötig — alles via CDN)
xdg-open docs/presentation/index.html

# Oder via dev-server: http://localhost:8000/shepherd/presentation/
```

## Architektur in einer Zeile

> `Unity (BigOne) → trains → ONNX → bundled in WebGL build → deployed to u-server → served by nginx → loaded by /shepherd/play blade → played in browser → 4Hz event-logs → fed back as GAIL demos → improves next training run`

## Open Items für vollständige End-to-End-Pipeline

1. **Phase 4 Training-Run** (ML-Agents braucht Unity Play-Mode aktiv für gRPC-Connection)
2. **WebGL Build-Verifikation** (wird in dieser Session aktiv gebaut; siehe `/tmp/dronedetect_webgl_build_status.txt`)
3. **Production-Deploy zu u-server** (rsync oder über Swarm-Stack)
4. **Docker Swarm Init** (zwei Befehle, Doku in `deploy/swarm/INIT.md`)
5. **WebGPU-Iteration** (zukünftige Visualisierungs-Verbesserung; `PlayerSettings.WebGL.useWasmModule + Graphics-API umstellen`)

## Bekannte Caveats

- Unity 6000.6.0a3 ist **Alpha** — `GetInstanceID()` als Error kann beim Package-Update wieder reinkommen. Fix in PackageCache muss eventuell neu eingespielt werden.
- DroneShepherd-Demos in `Assets/Demonstrations/DroneSessions/` sind aktuell leer (84 B). GAIL fällt zurück auf 0-Demos bis erste echte Sessions aufgezeichnet wurden.
- ithappy (21 MB) ist drin geblieben — WolfPlayer.prefab ist Variant von ithappy/Dog_001.
- Polyart ist in `_unused_assets/` und gitignored. Falls grafisches Polish gewünscht: Polyart Foliage als Standalone-Bundle re-importieren statt komplettem Asset-Pack.
