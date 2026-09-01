#!/usr/bin/env bash
set -euo pipefail

# 启动已经构建或拉取到本机的应用镜像；绝不触发 Docker build。
# 用法：scripts/deploy.sh [--env-file path/to/.env] [--docker-mode docker|wsl-docker]

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

  # 与构建脚本使用同一份受保护的部署变量，确保镜像标签和公开地址一致。
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

for image in "$engine_image" "$router_image" "$chat_image"; do
  "${docker_cmd[@]}" image inspect "$image" >/dev/null 2>&1 \
    || die "required application image is unavailable locally: $image; run scripts/build-images.sh first or pull the image"
done

cd "$repo_root"
compose_args=(--project-name "${OPENAGENT_COMPOSE_PROJECT:-openagent-app}" --file docker-compose.yml)
if [[ -n "$env_file" ]]; then
  compose_args+=(--env-file "$env_file")
fi

"${docker_cmd[@]}" compose "${compose_args[@]}" up --detach
