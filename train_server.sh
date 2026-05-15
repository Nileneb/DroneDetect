#!/bin/bash
set -e

CONDA_ENV=mlagents
OUTPUT_DIR=Assets/Demonstrations/WolfSessions

echo "=== ShepherdArena GAIL Training Pipeline ==="

# Activate mlagents env
source "$(conda info --base)/etc/profile.d/conda.sh"
conda activate $CONDA_ENV

# Collect demos from DB
echo "[1/3] Collecting demos from app.linn.games DB..."
python3 tools/shepherd_to_demo.py --all-sessions --output $OUTPUT_DIR
echo "Demos in $OUTPUT_DIR:"
ls -la $OUTPUT_DIR 2>/dev/null || echo "(none yet)"

# Launch 10 parallel headless training instances
echo "[2/3] Starting 10 parallel WolfAgent training runs..."
for i in {1..10}; do
    mlagents-learn config/WolfCSI.yaml \
        --run-id="wolf_v1_${i}" \
        --no-graphics \
        --force &
done

# TensorBoard
echo "[3/3] Starting TensorBoard on port 6006..."
tensorboard --logdir results/ --port 6006 --host 0.0.0.0 &

echo ""
echo "Training gestartet!"
echo "TensorBoard: http://$(hostname -I | awk '{print $1}'):6006"
echo "Runs: wolf_v1_1 .. wolf_v1_10"
echo ""
echo "Warte auf alle Trainer..."
wait
