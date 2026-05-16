# DroneDetect Deploy Pipeline

## Topologie

```
BigOne (192.168.178.11)           u-server (192.168.178.12)
─────────────────────             ─────────────────────────
• Unity 6000.6.0a3 alpha          • app.linn.games (Laravel)
• GPU (Training+Build)            • DB, Nginx, PHP-FPM
• Docker 29.4.1                   • public/shepherd/Build/ (WebGL target)
• Docker Compose v5.1.3
• role: Swarm Manager             • role: Swarm Worker
```

## Daten-Flow

```
Unity ML-Agents Training (BigOne, GPU)
    ↓ produces
DroneCSI.onnx / DroneShepherd.onnx
    ↓ embedded in
Unity WebGL Build (BigOne, in container)
    ↓ overlay-volume → cross-node
public/shepherd/Build/*.br on u-server
    ↓ served by
nginx (app.linn.games/shepherd/play)
```

## Phasen

### Phase 1: Lokaler Build → SCP (jetzt, schnell)
Schnelltest dass WebGL überhaupt baut + lädt.
- Unity baut direkt nach `/home/nileneb/Desktop/WebDev/app.linn.games/public/shepherd/Build/`
- Local Laravel dev runs against same folder
- Production push: rsync zu u-server

### Phase 2: Containerisierter Build auf BigOne
- Unity in Docker Image gefroren (reproducible)
- Build-Job nimmt Projekt-Snapshot, baut, schreibt artifact in shared volume
- Image: `unity:6000.6.0a3-webgl` (custom built once)

### Phase 3: Docker Swarm Multi-Host
- BigOne = manager, u-server = worker
- Overlay-Network `shepherd-net`
- Shared Volume `shepherd-artifacts` (NFS/glusterfs backend)
- Build-Stack: build container on BigOne, web container on u-server, both see same volume
- Optional: WebGPU-Build-Variante via flag

## Render-Pipeline-Note (Unity 6 alpha)

- Built-In Render Pipeline (BRP): legacy, viele APIs `[Obsolete]`. Projekt nutzt BRP **nicht**.
- URP 17.6.0: aktiv. Default für neue Projekte. **In Verwendung.**
- HDRP: aktiv. Nicht verwendet.

WebGPU als nächste Iteration: `PlayerSettings.WebGL.useWasmModule=true` + Graphics-API auf WebGPU
setzen. Aktuelle Build-Pipeline (Phase 1) bleibt WebGL2/WASM.
