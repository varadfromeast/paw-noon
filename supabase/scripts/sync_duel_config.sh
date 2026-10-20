#!/usr/bin/env bash
# Copies the game's rules file into the Edge Functions bundle.
# The server MUST run the same duel_config.json as the shipped client (parity, SPEC_V2 §4.5).
#   supabase/scripts/sync_duel_config.sh          copy
#   supabase/scripts/sync_duel_config.sh --check  exit 1 if the copy is stale (CI)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SRC="$ROOT/Assets/PawNoon/Resources/Data/duel_config.json"
DST="$ROOT/supabase/functions/_shared/duel_config.json"
if [[ "${1:-}" == "--check" ]]; then
  if cmp -s "$SRC" "$DST"; then echo "duel_config.json in sync"; exit 0; fi
  echo "supabase/functions/_shared/duel_config.json is STALE: run supabase/scripts/sync_duel_config.sh" >&2
  exit 1
fi
cp "$SRC" "$DST"
echo "copied $SRC -> $DST"
