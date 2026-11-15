#!/usr/bin/env bash
# Start a DroneFollow training run.
#   tools/train.sh <run-id>                      # trains against the Unity Editor (press Play when prompted)
#   tools/train.sh <run-id> --env Builds/Training/DroneFollow.x86_64 --num-envs 2
#   tools/train.sh <run-id> --resume             # continue a run
# Extra arguments are passed to mlagents-learn unchanged.
set -euo pipefail

PROJECT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
RUN_ID="${1:?Usage: tools/train.sh <run-id> [mlagents-learn options]}"
shift

if ! command -v mlagents-learn >/dev/null 2>&1; then
  source "$HOME/miniconda3/etc/profile.d/conda.sh"
  conda activate mlagents
fi

cd "$PROJECT"
# The compat wrapper replaces ML-Agents' StrictVersion check, which rejects the Unity package's version string.
exec python tools/mlagents_learn_compat.py config/DroneFollow.yaml --run-id="$RUN_ID" "$@"
