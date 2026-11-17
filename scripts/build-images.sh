#!/usr/bin/env bash
set -euo pipefail

# Build the dev services. Optionally export all three application images to one archive.
[[ $# -le 1 ]] || { echo 'usage: scripts/build-images.sh [output-directory]' >&2; exit 1; }
case "${1:-}" in
  -h|--help) echo 'usage: scripts/build-images.sh [output-directory]'; exit 0 ;;
  -*) echo "unknown option: $1" >&2; exit 1 ;;
esac
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
output_directory="${1:-}"
[[ -z "$output_directory" || "$output_directory" = /* ]] || output_directory="$PWD/$output_directory"
cd "$repo_root"
opencode_version="$(tr -d '[:space:]' < Backend/opencode.version)"
[[ "$opencode_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] \
  || { echo 'Backend/opencode.version must contain a strict semantic version.' >&2; exit 1; }
platform_version="$(git describe --tags --always --dirty 2>/dev/null || echo dev)"
for service in engine router chat; do
  args=(--label "org.opencontainers.image.version=$platform_version")
  case "$service" in
    engine) dockerfile=Backend/src/OpenAgent.Engine.Host/Dockerfile; args+=(--build-arg "OPENCODE_VERSION=$opencode_version") ;;
    router) dockerfile=Backend/src/OpenAgent.Router/Dockerfile ;;
    chat) dockerfile=Frontend/OpenAgent.Chat/Dockerfile ;;
  esac
  docker build --tag "openagent-$service:latest" "${args[@]}" --file "$dockerfile" .
done
if [[ -n "$output_directory" ]]; then
  mkdir -p "$output_directory"
  docker save --output "$output_directory/openagent-images.tar" \
    openagent-engine:latest openagent-router:latest openagent-chat:latest
fi
