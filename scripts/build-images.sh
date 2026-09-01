#!/usr/bin/env bash
set -euo pipefail

# 构建应用镜像；不启动或修改任何容器。
# 用法：scripts/build-images.sh [--env-file path/to/.env] [--docker-mode docker|wsl-docker]

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
env_file=""
docker_mode="${OPENAGENT_DOCKER_MODE:-docker}"
docker_cmd=()

die() {
  echo "error: $*" >&2
  exit 1
}

load_env_file() {
  local file="$1"
  [[ -f "$file" ]] || die "environment file does not exist: $file"

  # 部署环境文件由操作者维护；以 shell 方式加载可保留 Docker build 所需的完整变量值。
  set -a
  # shellcheck disable=SC1090
  source "$file"
  set +a
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --env-file)
      [[ $# -ge 2 ]] || die "--env-file requires a path"
      env_file="$2"
      shift 2
      ;;
    --docker-mode)
      [[ $# -ge 2 ]] || die "--docker-mode requires docker or wsl-docker"
      docker_mode="$2"
      shift 2
      ;;
    *)
      die "unknown argument: $1"
      ;;
  esac
done

if [[ -n "$env_file" ]]; then
  [[ "$env_file" = /* ]] || env_file="$PWD/$env_file"
  load_env_file "$env_file"
fi

case "$docker_mode" in
  docker|local)
    docker_cmd=(docker)
    ;;
  wsl-docker|wsl)
    if [[ -n "${WSL_DISTRO_NAME:-}" ]]; then
      docker_cmd=(docker)
    elif command -v wsl.exe >/dev/null 2>&1; then
      docker_cmd=(wsl.exe docker)
    else
      die "wsl-docker mode requires running inside WSL or having wsl.exe available"
    fi
    ;;
  *)
    die "unsupported docker mode '$docker_mode' (use docker or wsl-docker)"
    ;;
esac

engine_image="${OPENAGENT_ENGINE_IMAGE:-openagent-engine:latest}"
router_image="${OPENAGENT_ROUTER_IMAGE:-openagent-router:latest}"
chat_image="${OPENAGENT_CHAT_IMAGE:-openagent-chat:latest}"
router_url="${OPENAGENT_PUBLIC_ROUTER_URL:-https://localhost:8082}"
engine_url="${OPENAGENT_PUBLIC_ENGINE_URL:-https://localhost:8083}"
tenant_id="${OPENAGENT_TENANT_ID:-development}"

"${docker_cmd[@]}" build \
  --tag "$engine_image" \
  --file "$repo_root/Backend/src/OpenAgent.Engine.Host/Dockerfile" \
  "$repo_root"

"${docker_cmd[@]}" build \
  --tag "$router_image" \
  --file "$repo_root/Backend/src/OpenAgent.Router/Dockerfile" \
  "$repo_root"

"${docker_cmd[@]}" build \
  --tag "$chat_image" \
  --build-arg "VITE_OPENAGENT_ROUTER_BASE_URL=$router_url" \
  --build-arg "VITE_OPENAGENT_ENGINE_BASE_URL=$engine_url" \
  --build-arg "VITE_OPENAGENT_TENANT_ID=$tenant_id" \
  --file "$repo_root/Frontend/OpenAgent.Chat/Dockerfile" \
  "$repo_root"
