#!/bin/bash
set -e

CONDA_ENV=mlagents
WOLF_DEMO_DIR=Assets/Demonstrations/WolfSessions
DRONE_DEMO_DIR=Assets/Demonstrations/DroneSessions

echo "=== ShepherdArena GAIL Training Pipeline ==="

source "$(conda info --base)/etc/profile.d/conda.sh"
conda activate $CONDA_ENV

# Collect Wolf demos from DB
echo "[1/4] Collecting Wolf demos from DB..."
python3 tools/shepherd_to_demo.py --all-sessions --output $WOLF_DEMO_DIR
echo "Wolf demos: $(ls $WOLF_DEMO_DIR 2>/dev/null | wc -l) files"

# Collect Drone demos from DB
echo "[2/4] Collecting Drone demos from DB..."
python3 tools/shepherd_to_drone_demo.py --all-sessions --output $DRONE_DEMO_DIR
echo "Drone demos: $(ls $DRONE_DEMO_DIR 2>/dev/null | wc -l) files"

# Launch Wolf training (10 parallel)
echo "[3/4] Starting WolfAgent training (10x parallel)..."
for i in {1..10}; do
    mlagents-learn config/WolfCSI.yaml \
        --run-id="wolf_shepherd_${i}" \
        --no-graphics \
        --force &
done

# Launch Drone training (3 parallel — DroneShepherd behavior)
echo "[4/4] Starting DroneShepherd training (3x parallel)..."
DRONE_DEMO_COUNT=$(ls $DRONE_DEMO_DIR 2>/dev/null | wc -l)
if [ "$DRONE_DEMO_COUNT" -gt 0 ]; then
    for i in {1..3}; do
        mlagents-learn config/DroneShepherd.yaml \
            --run-id="drone_shepherd_${i}" \
            --no-graphics \
            --force &
    done
else
    echo "  Keine Drone-Demos vorhanden — erst ShepherdArena spielen!"
fi

tensorboard --logdir results/ --port 6006 --host 0.0.0.0 &

echo ""
echo "Training gestartet!"
echo "TensorBoard: http://$(hostname -I | awk '{print $1}'):6006"
echo ""
wait
