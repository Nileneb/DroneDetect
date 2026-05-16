# DroneDetect — Status & Quickstart

Single-page Übersicht über alle Komponenten der ML-Agents → WebGL-Deploy Pipeline.

## Komponenten

| Bereich | Status | Pfad / Befehl |
|---------|--------|---------------|
| Phase 1 Training (DroneCSI) | ✅ trainiert: 150k steps · reward 35.25 | `results/v8_1/DroneCSI.onnx` |
| Phase 4 Training (DroneShepherd) | ✅ konfiguriert · ⧗ Lauf ausstehend | `./tools/run_training.sh phase4` |
| Phase 4 Reward-Shaping | ✅ implementiert | `Assets/Scripts/ShepherdDroneAgent.cs` |
| Unity Compile Fix | ✅ `GetInstanceID → GetEntityId().GetHashCode()` | `Library/PackageCache/com.unity.ml-agents@.../Match3ActuatorComponent.cs` |
| Polyart-Bloat-Removal | ✅ raus (878 MB) — ohne 72 GB Shader-Reimport | `_unused_assets/Polyart/` |
| WebGL Build-Skript | ✅ Editor-Menü + batchmode | `Assets/Editor/BuildScript.cs`, `tools/build-webgl-batch.sh` |
| WebGL Deploy-Pfad | ✅ direkt nach `app.linn.games/public/shepherd/Build/` | siehe `deploy/swarm/shepherd-stack.yml` |
| Shepherd Blade Routes | ✅ vorhanden in app.linn.games | `Route::get('shepherd/play', ...)` |
| Präsentation | ✅ Reveal.js-Slides + echte TB-Daten | `docs/presentation/index.html` |
| Demo Script | ✅ | `docs/presentation/demo-script.md` |
| Training Methodology Doc | ✅ | `docs/presentation/training-methodology.md` |
| Docker Swarm Setup-Docs | ✅ | `deploy/swarm/INIT.md`, `deploy/swarm/shepherd-stack.yml` |
| Docker Swarm Initialisierung | ⧗ erfordert User-SSH-Zugang | `docker swarm init` auf BigOne + Worker-Join auf u-server |

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
# Phase 1 (manuell, jetzt):
rsync -avz --delete \
    /home/nileneb/Desktop/WebDev/app.linn.games/public/shepherd/ \
    nileneb@192.168.178.12:/var/www/app.linn.games/public/shepherd/

# Phase 3 (Swarm, später): Build-Container schreibt direkt ins NFS-Volume
# das auf u-server gemounted ist — kein rsync mehr nötig.
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
