#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

CONDA_ENV="${CONDA_ENV:-mlagents}"
BASE_PORT="${BASE_PORT:-5004}"
TENSORBOARD_PORT="${TENSORBOARD_PORT:-6006}"
WOLF_DEMO_DIR="Assets/Demonstrations/WolfSessions"
DRONE_DEMO_DIR="Assets/Demonstrations/DroneSessions"

usage() {
    cat <<'USAGE'
Usage:
  ./train_server.sh [trainer] [run-id]

With no trainer argument, an interactive menu is shown.

Trainers:
  follow         DroneFollow PPO (18 observations; no demos required)
  dronecsi       Legacy DroneCSI PPO
  droneshepherd  DroneShepherd PPO + GAIL demos from DB
  wolfcsi        WolfCSI PPO + GAIL demos from DB

Environment overrides:
  CONDA_ENV=mlagents BASE_PORT=5004 TENSORBOARD_PORT=6006

Examples:
  ./train_server.sh
  ./train_server.sh follow
  ./train_server.sh droneshepherd shepherd_test_01
  BASE_PORT=5004 ./train_server.sh follow
USAGE
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    usage
    exit 0
fi
if (( $# > 2 )); then
    usage >&2
    exit 2
fi

MODE="${1:-}"
RUN_ID="${2:-${RUN_ID:-}}"

if [[ -z "$MODE" ]]; then
    cat <<'MENU'
=== DroneDetect ML-Agents Trainer ===
  1) DroneFollow   - PPO, 18-value vector, no demos
  2) DroneCSI      - legacy PPO
  3) DroneShepherd - PPO + GAIL demos
  4) WolfCSI       - PPO + GAIL demos
  0) Exit
MENU
    read -r -p "Select trainer [1-4, 0]: " MODE
fi

case "$MODE" in
    0|exit|quit)
        echo "Cancelled."
        exit 0
        ;;
    1|follow|DroneFollow)
        CONFIG="config/DroneFollow.yaml"
        RUN_PREFIX="dronefollow"
        BEHAVIOR_NAME="DroneFollow"
        DEMO_SOURCE="none"
        ;;
    2|dronecsi|DroneCSI)
        CONFIG="config/DroneCSI.yaml"
        RUN_PREFIX="dronecsi"
        BEHAVIOR_NAME="DroneCSI"
        DEMO_SOURCE="none"
        ;;
    3|droneshepherd|DroneShepherd)
        CONFIG="config/DroneShepherd.yaml"
        RUN_PREFIX="droneshepherd"
        BEHAVIOR_NAME="DroneShepherd"
        DEMO_SOURCE="drone"
        ;;
    4|wolfcsi|WolfCSI)
        CONFIG="config/WolfCSI.yaml"
        RUN_PREFIX="wolfcsi"
        BEHAVIOR_NAME="WolfAgent"
        DEMO_SOURCE="wolf"
        ;;
    *)
        echo "Unknown trainer: $MODE" >&2
        echo "Run './train_server.sh --help' to see available trainers." >&2
        exit 2
        ;;
esac

if [[ -z "$RUN_ID" ]]; then
    RUN_ID="${RUN_PREFIX}_$(date +%Y%m%d_%H%M%S)"
fi

if [[ ! -f "$CONFIG" ]]; then
    echo "Trainer config not found: $CONFIG" >&2
    exit 1
fi

if ! command -v conda >/dev/null 2>&1; then
    echo "conda was not found in PATH." >&2
    exit 1
fi
CONDA_BASE="$(conda info --base)"
source "$CONDA_BASE/etc/profile.d/conda.sh"
conda activate "$CONDA_ENV"

if ! command -v mlagents-learn >/dev/null 2>&1; then
    echo "mlagents-learn was not found in Conda environment '$CONDA_ENV'." >&2
    exit 1
fi

# These two legacy configs use GAIL and therefore need demonstrations.
case "$DEMO_SOURCE" in
    wolf)
        echo "Collecting Wolf demos from DB..."
        python3 tools/shepherd_to_demo.py --all-sessions --output "$WOLF_DEMO_DIR"
        DEMO_COUNT="$(find "$WOLF_DEMO_DIR" -maxdepth 1 -type f -name '*.demo' 2>/dev/null | wc -l | tr -d ' ')"
        if [[ "$DEMO_COUNT" -eq 0 ]]; then
            echo "No Wolf .demo files found in $WOLF_DEMO_DIR; GAIL cannot start." >&2
            exit 1
        fi
        echo "Wolf demos: $DEMO_COUNT"
        ;;
    drone)
        echo "Collecting DroneShepherd demos from DB..."
        python3 tools/shepherd_to_drone_demo.py --all-sessions --output "$DRONE_DEMO_DIR"
        DEMO_COUNT="$(find "$DRONE_DEMO_DIR" -maxdepth 1 -type f -name '*.demo' 2>/dev/null | wc -l | tr -d ' ')"
        if [[ "$DEMO_COUNT" -eq 0 ]]; then
            echo "No Drone .demo files found in $DRONE_DEMO_DIR; GAIL cannot start." >&2
            exit 1
        fi
        echo "Drone demos: $DEMO_COUNT"
        ;;
    none)
        echo "No demo import needed for $RUN_PREFIX."
        ;;
esac

TENSORBOARD_PID=""
cleanup() {
    if [[ -n "$TENSORBOARD_PID" ]]; then
        kill "$TENSORBOARD_PID" 2>/dev/null || true
    fi
}
trap cleanup EXIT

if command -v tensorboard >/dev/null 2>&1; then
    tensorboard --logdir results/ --port "$TENSORBOARD_PORT" --host 0.0.0.0 &
    TENSORBOARD_PID=$!
    echo "TensorBoard: http://$(hostname -I 2>/dev/null | awk '{print $1}'):$TENSORBOARD_PORT"
else
    echo "TensorBoard not found; continuing without it."
fi

echo ""
echo "Starting trainer: $RUN_PREFIX"
echo "Config: $CONFIG"
echo "Run ID: $RUN_ID"
echo "Expected Unity behavior name: $BEHAVIOR_NAME"
echo "Unity trainer port: $BASE_PORT"
echo "When mlagents-learn is listening, press Play in the matching Unity scene."
echo ""

mlagents-learn "$CONFIG" \
    --run-id="$RUN_ID" \
    --base-port="$BASE_PORT" \
    --no-graphics
