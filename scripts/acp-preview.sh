#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
project="${ACP_PREVIEW_PROJECT:-openagent-acp-preview}"
compose=(docker compose -p "$project" -f "${ACP_PREVIEW_COMPOSE_FILE:-deploy/acp-preview/compose.yml}")
case "${1:-up}" in
  up)
    "${compose[@]}" up -d --build
    python3 scripts/test-acp-preview.py --seed
    ;;
  test) python3 scripts/test-acp-preview.py ;;
  down) "${compose[@]}" down ;;
  status) "${compose[@]}" ps ;;
  *) echo 'Usage: scripts/acp-preview.sh [up|test|status|down]' >&2; exit 1 ;;
esac
