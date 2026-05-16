#!/usr/bin/env bash
# DroneDetect training-run launcher. Use via:
#   ./tools/run_training.sh phase1                          # train DroneCSI from scratch
#   ./tools/run_training.sh phase4                          # train DroneShepherd + GAIL
#   ./tools/run_training.sh phase4 --resume                 # continue last run
#   ./tools/run_training.sh phase4 --transfer results/v8_1  # transfer-init from a prior run
#
# Prereq: conda env `mlagents` activated OR /home/nileneb/miniconda3/envs/mlagents/bin in PATH.
# Unity Editor must be running with the right scene loaded + Play-on-connect ready.
set -euo pipefail

PROJECT=/home/nileneb/DroneDetect
CONDA_ENV=mlagents

# Activate conda env
if ! command -v mlagents-learn >/dev/null 2>&1; then
  source /home/nileneb/miniconda3/etc/profile.d/conda.sh
  conda activate "$CONDA_ENV"
fi

PHASE="${1:-}"
shift || true

case "$PHASE" in
  phase1)
    RUN_ID="DroneCSI_$(date +%Y%m%d_%H%M)"
    echo "=== Phase 1 (DroneCSI) — run_id=$RUN_ID ==="
    cd "$PROJECT"
    mlagents-learn config/DroneCSI.yaml \
      --run-id="$RUN_ID" \
      --time-scale=10 \
      --no-graphics \
      "$@"
    ;;

  phase4)
    RUN_ID="DroneShepherd_$(date +%Y%m%d_%H%M)"
    echo "=== Phase 4 (DroneShepherd + GAIL) — run_id=$RUN_ID ==="
    cd "$PROJECT"

    # Auto-default: transfer from latest v8_x run unless overridden
    TRANSFER_FLAG=""
    if [[ "${*}" != *"--initialize-from"* && "${*}" != *"--resume"* ]]; then
      LATEST=$(ls -dt results/v8_*/DroneCSI.onnx 2>/dev/null | head -1 | xargs -r dirname || true)
      if [[ -n "$LATEST" ]]; then
        echo "Auto-transfer-init from: $LATEST"
        TRANSFER_FLAG="--initialize-from=$LATEST"
      fi
    fi

    mlagents-learn config/DroneShepherd.yaml \
      --run-id="$RUN_ID" \
      --time-scale=10 \
      --no-graphics \
      $TRANSFER_FLAG \
      "$@"
    ;;

  tb|tensorboard)
    tensorboard --logdir "$PROJECT/results/" --port 6006 --bind_all
    ;;

  *)
    echo "Usage: $0 {phase1|phase4|tb} [extra mlagents flags]"
    echo ""
    echo "Examples:"
    echo "  $0 phase1                  # train DroneCSI from scratch"
    echo "  $0 phase4                  # train Shepherd, auto-transfer from latest CSI run"
    echo "  $0 phase4 --resume         # continue last Shepherd run"
    echo "  $0 phase1 --resume --keep-checkpoints 20"
    echo "  $0 tb                      # serve tensorboard on :6006"
    exit 1
    ;;
esac
