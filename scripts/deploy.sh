#!/usr/bin/env bash
set -euo pipefail

# Deploy one application using its own Compose environment file. Never build images.
# Usage: scripts/deploy.sh --app engine|router|runner|chat|gateway|postgres|redis|minio|keycloak [--env-file path] [--docker-mode docker|wsl-docker]
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
app="engine"
env_file=""
docker_mode="${OPENAGENT_DOCKER_MODE:-docker}"
docker_cmd=()

die() { echo "error: $*" >&2; exit 1; }
while [[ $# -gt 0 ]]; do
  case "$1" in
    --app|--env-file|--docker-mode)
      [[ $# -ge 2 ]] || die "$1 requires a value"
      case "$1" in
        --app) app="$2" ;;
        --env-file) env_file="$2" ;;
        --docker-mode) docker_mode="$2" ;;
      esac
      shift 2 ;;
    *) die "unknown argument: $1" ;;
  esac
done
case "$app" in
  engine|router|runner|chat|gateway) directory="openagent" ;;
  postgres|redis|minio|keycloak) directory="infrastructure" ;;
  *) die "unsupported application '$app'" ;;
esac
if [[ -z "$env_file" ]]; then
  env_file="$repo_root/deploy/$directory/$app.env"
fi
[[ "$env_file" = /* ]] || env_file="$PWD/$env_file"
[[ -f "$env_file" ]] || die "environment file does not exist: $env_file; copy deploy/$directory/$app.env.example first"
case "$docker_mode" in
  docker|local) docker_cmd=(docker) ;;
  wsl-docker|wsl)
    if [[ -n "${WSL_DISTRO_NAME:-}" ]]; then docker_cmd=(docker)
    elif command -v wsl.exe >/dev/null 2>&1; then docker_cmd=(wsl.exe docker)
    else die "wsl-docker mode requires running inside WSL or having wsl.exe available"
    fi ;;
  *) die "unsupported docker mode '$docker_mode'" ;;
esac
cd "$repo_root"
compose_root="$repo_root"
if [[ "${docker_cmd[0]}" == "wsl.exe" ]]; then
  if command -v cygpath >/dev/null 2>&1; then
    compose_root="$(cygpath -w "$compose_root")"
    env_file="$(cygpath -w "$env_file")"
  fi
  compose_root="$(wsl.exe wslpath -a -u "$compose_root" | tr -d '\r')"
  env_file="$(wsl.exe wslpath -a -u "$env_file" | tr -d '\r')"
fi
compose_args=(--file "$compose_root/deploy/$directory/$app.compose.yml" --env-file "$env_file")
"${docker_cmd[@]}" compose "${compose_args[@]}" config --quiet
# Compose parses the env file; it is never executed as shell code.
images="$("${docker_cmd[@]}" compose "${compose_args[@]}" config --images)"
while IFS= read -r image; do
  [[ -z "$image" ]] && continue
  "${docker_cmd[@]}" image inspect "$image" >/dev/null 2>&1 \
    || die "required image is unavailable locally: $image; build or pull it first"
done <<< "$images"
networks="$("${docker_cmd[@]}" compose "${compose_args[@]}" config --format json | python3 -c '
import json, sys
for network in json.load(sys.stdin).get("networks", {}).values():
    if network.get("external"):
        print(network["name"])
')"
while IFS= read -r network; do
  [[ -z "$network" ]] && continue
  if ! "${docker_cmd[@]}" network inspect "$network" >/dev/null 2>&1; then
    "${docker_cmd[@]}" network create "$network" >/dev/null 2>&1 \
      || "${docker_cmd[@]}" network inspect "$network" >/dev/null
  fi
done <<< "$networks"
if [[ "$app" == "minio" ]]; then
  # Run the one-shot initializer after readiness; it must not be treated as a daemon.
  "${docker_cmd[@]}" compose "${compose_args[@]}" up --detach --no-build --pull never --wait minio
  "${docker_cmd[@]}" compose "${compose_args[@]}" run --rm --no-deps --no-build --pull never minio-init
else
  "${docker_cmd[@]}" compose "${compose_args[@]}" up --detach --no-build --pull never --wait
fi
