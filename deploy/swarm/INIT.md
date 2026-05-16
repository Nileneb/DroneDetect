# Docker Swarm Initialization

Diese Schritte einmalig ausführen. Danach ist der Swarm persistent.

## 1. BigOne (Manager) — auf 192.168.178.11

```bash
# Init swarm, advertise-addr ist eth0/wlan0 IP
docker swarm init --advertise-addr 192.168.178.11

# Capture worker join token (für nächsten Schritt)
docker swarm join-token worker
# → kopiere den ganzen `docker swarm join --token SWMTKN-1-... 192.168.178.11:2377` Befehl
```

## 2. u-server (Worker) — auf 192.168.178.12

```bash
# Mit dem Token aus Schritt 1
docker swarm join --token SWMTKN-1-<token> 192.168.178.11:2377
```

## 3. Verifikation (auf BigOne)

```bash
docker node ls
# Erwartet:
# ID    HOSTNAME      STATUS    AVAILABILITY    MANAGER STATUS    ENGINE VERSION
# xxxx  BigOne        Ready     Active          Leader            29.4.1
# yyyy  u-server      Ready     Active                            <version>
```

## 4. Overlay-Network erstellen (auf BigOne)

```bash
docker network create --driver overlay --attachable shepherd-net
docker network ls | grep shepherd-net
```

## 5. Shared Volume für WebGL-Artefakte

Option A — NFS (recommended, simpel):
```bash
# Auf u-server (wo app.linn.games läuft, dort soll das public/shepherd/Build/ landen):
sudo apt install -y nfs-kernel-server
sudo mkdir -p /srv/shepherd-artifacts
sudo chown nobody:nogroup /srv/shepherd-artifacts
echo "/srv/shepherd-artifacts 192.168.178.0/24(rw,sync,no_subtree_check,no_root_squash)" | sudo tee -a /etc/exports
sudo exportfs -ra
sudo systemctl restart nfs-kernel-server
```

Option B — GlusterFS (für HA, später).

## 6. Symlink WebGL artifacts → app.linn.games public

Auf u-server:
```bash
sudo ln -s /srv/shepherd-artifacts /var/www/app.linn.games/public/shepherd/Build
# Oder mit bind-mount im docker-compose (sauberer)
```

## Constraint-Labels für Node-Targeting

```bash
# BigOne hat GPU + ist der Build/Trainer-Node
docker node update --label-add role=builder --label-add gpu=true BigOne

# u-server ist der Web/Hosting-Node
docker node update --label-add role=web u-server
```

Im Stack-File später: `placement.constraints: [node.labels.role==builder]`
