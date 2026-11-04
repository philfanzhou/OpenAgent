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

to_wsl_path() {
  local path="$1"
  local windows_path="$path"

  # 用正斜杠形式的 Windows 路径（D:/...）：反斜杠路径经 wsl.exe 转发时会被中间 shell 当转义符吃掉。
  if command -v cygpath >/dev/null 2>&1; then
    windows_path="$(cygpath -m "$path")"
  elif [[ "$path" =~ ^/([[:alpha:]])/(.*)$ ]]; then
    windows_path="${BASH_REMATCH[1]^^}:/${BASH_REMATCH[2]}"
  fi

  wsl.exe wslpath -a -u "$windows_path" | tr -d '\r'
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

# Git Bash 会把传给 wsl.exe 的 /mnt/... 参数改写为 Windows 路径，必须关闭该转换。
if [[ "${docker_cmd[0]:-docker}" == "wsl.exe" ]]; then
  export MSYS_NO_PATHCONV=1
fi

engine_image="${OPENAGENT_ENGINE_IMAGE:-openagent-engine:latest}"
router_image="${OPENAGENT_ROUTER_IMAGE:-openagent-router:latest}"
chat_image="${OPENAGENT_CHAT_IMAGE:-openagent-chat:latest}"

runner_key="${OPENAGENT_RUNNER_API_KEY:-}"
[[ ${#runner_key} -ge 32 ]] || die "Runner API key must contain at least 32 characters"
images=("$engine_image" "$router_image" "$chat_image" "${OPENAGENT_RUNNER_IMAGE:-openagent-runner:latest}")
for image in "${images[@]}"; do
  if ! "${docker_cmd[@]}" image inspect "$image" >/dev/null 2>&1; then
    hint="run scripts/build-images.sh first or pull the image"
    # 独立后端检出会跳过 chat 构建；chat 镜像需要前端源码树或既有镜像。
    if [[ "$image" == "$chat_image" ]]; then
      hint="build it from a source tree containing Frontend/OpenAgent.Chat (backend-only checkouts skip it), or pull it from your registry"
    fi
    die "required application image is unavailable locally: $image; $hint"
  fi
done

cd "$repo_root"
compose_args=(--project-name "${OPENAGENT_COMPOSE_PROJECT:-openagent-app}" --file deploy/openagent/docker-compose.yml)
if [[ -n "$env_file" ]]; then
  # wsl.exe 模式下把宿主机路径转换为 WSL 路径，避免 compose 在 WSL 内读不到。
  if [[ "${docker_cmd[0]}" == "wsl.exe" ]]; then
    compose_args+=(--env-file "$(to_wsl_path "$env_file")")
  else
    compose_args+=(--env-file "$env_file")
  fi
fi

"${docker_cmd[@]}" compose "${compose_args[@]}" config --quiet
"${docker_cmd[@]}" compose "${compose_args[@]}" up --detach --no-build --pull never
