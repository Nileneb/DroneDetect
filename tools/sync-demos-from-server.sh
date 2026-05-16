#!/usr/bin/env bash
# Pull all .demo files from app.linn.games into Assets/Demonstrations/DroneSessions/.
# Use before kicking off a GAIL training run on BigOne or laptop.
#
# Required env:
#   SHEPHERD_JWT  — bearer token; mint via app.linn.games/user/api-tokens
#
# Optional:
#   SHEPHERD_API     — defaults to https://app.linn.games/api/shepherd
#   AGENT_TYPE       — filter, defaults to DroneShepherd
#   ROLE             — filter, defaults to drone
#   OUT_DIR          — defaults to Assets/Demonstrations/DroneSessions
set -euo pipefail

API="${SHEPHERD_API:-https://app.linn.games/api/shepherd}"
TOKEN="${SHEPHERD_JWT:-}"
AGENT="${AGENT_TYPE:-DroneShepherd}"
ROLE="${ROLE:-drone}"
OUT="${OUT_DIR:-Assets/Demonstrations/DroneSessions}"

if [[ -z "$TOKEN" ]]; then
  echo "Missing SHEPHERD_JWT env var" >&2
  echo "Create a token at app.linn.games (user settings → API tokens)" >&2
  exit 1
fi

mkdir -p "$OUT"

echo "Fetching demo list from $API/demos?agent_type=$AGENT&role=$ROLE..."
DEMOS=$(curl -fsS -H "Authorization: Bearer $TOKEN" -H "Accept: application/json" \
    "$API/demos?agent_type=$AGENT&role=$ROLE&limit=200")

COUNT=$(echo "$DEMOS" | jq '.demos | length')
echo "Found $COUNT demos on server"

DOWNLOADED=0; SKIPPED=0; FAILED=0
echo "$DEMOS" | jq -r '.demos[] | "\(.id) \(.sha256)"' | while read -r ID SHA; do
  TARGET="$OUT/${SHA}.demo"
  if [[ -f "$TARGET" ]]; then
    SKIPPED=$((SKIPPED+1))
    continue
  fi
  echo "  ↓ $SHA"
  if curl -fsS -H "Authorization: Bearer $TOKEN" "$API/demos/$ID/download" -o "$TARGET"; then
    DOWNLOADED=$((DOWNLOADED+1))
  else
    rm -f "$TARGET"
    FAILED=$((FAILED+1))
  fi
done

echo ""
echo "Sync summary: $DOWNLOADED new, skip (already local), failures shown above"
echo "→ Demos available at: $OUT"
echo "→ Training: ./tools/run_training.sh phase4"
