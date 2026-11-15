#!/usr/bin/env bash
# Startet TensorBoard für alle DroneFollow-Runs unter results/.
#   tools/tensorboard.sh                 # results/ auf Port 6006
#   tools/tensorboard.sh --port 6007     # anderer Port
#   tools/tensorboard.sh v8_1            # nur einen Run anzeigen
# Weitere Argumente werden unverändert an tensorboard übergeben.
set -euo pipefail

PROJECT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
RESULTS="$PROJECT/results"
PORT=6006
RUN=""
EXTRA=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --port) PORT="${2:?--port braucht einen Wert}"; shift 2 ;;
    --port=*) PORT="${1#--port=}"; shift ;;
    -h|--help)
      sed -n '2,6p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    -*) EXTRA+=("$1"); shift ;;
    *) RUN="$1"; shift ;;
  esac
done

LOGDIR="$RESULTS"
if [[ -n "$RUN" ]]; then
  LOGDIR="$RESULTS/$RUN"
  [[ -d "$LOGDIR" ]] || { echo "Fehler: Run '$RUN' nicht gefunden unter $RESULTS" >&2; exit 1; }
fi

if ! command -v tensorboard >/dev/null 2>&1; then
  source "$HOME/miniconda3/etc/profile.d/conda.sh"
  conda activate mlagents
fi

echo "TensorBoard: logdir=$LOGDIR  url=http://localhost:$PORT"
exec tensorboard --logdir "$LOGDIR" --port "$PORT" "${EXTRA[@]}"
