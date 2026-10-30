#!/usr/bin/env bash
set -euo pipefail

# 构建应用镜像；不启动或修改任何容器。
# 用法：scripts/build-images.sh [--env-file path/to/.env] [--docker-mode auto|docker|wsl-docker] [--tar-dir directory]

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
env_file=""
docker_mode_arg=""
tar_directory_arg=""
docker_cmd=()

die() {
  echo "error: $*" >&2
  exit 1
}

to_wsl_path() {
  local path="$1"
  local windows_path="$path"

  if command -v cygpath >/dev/null 2>&1; then
    windows_path="$(cygpath -w "$path")"
  elif [[ "$path" =~ ^/([[:alpha:]])/(.*)$ ]]; then
    windows_path="${BASH_REMATCH[1]^^}:\\${BASH_REMATCH[2]//\//\\}"
  fi

  wsl.exe wslpath -a -u "$windows_path" | tr -d '\r'
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
      [[ $# -ge 2 ]] || die "--docker-mode requires auto, docker, or wsl-docker"
      docker_mode_arg="$2"
      shift 2
      ;;
    --tar-dir)
      [[ $# -ge 2 ]] || die "--tar-dir requires a directory"
      tar_directory_arg="$2"
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

docker_mode="${docker_mode_arg:-${OPENAGENT_DOCKER_MODE:-auto}}"
tar_directory="${tar_directory_arg:-${OPENAGENT_IMAGE_TAR_DIR:-}}"

case "$docker_mode" in
  auto)
    if [[ -n "${WSL_DISTRO_NAME:-}" && -x "$(command -v docker 2>/dev/null || true)" ]]; then
      docker_cmd=(docker)
    elif command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
      docker_cmd=(docker)
    elif command -v wsl.exe >/dev/null 2>&1; then
      docker_cmd=(wsl.exe docker)
    elif command -v docker >/dev/null 2>&1; then
      docker_cmd=(docker)
    else
      die "auto docker mode could not find a usable docker or wsl.exe command"
    fi
    ;;
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
    die "unsupported docker mode '$docker_mode' (use auto, docker, or wsl-docker)"
    ;;
esac

docker_root="$repo_root"
if [[ "${docker_cmd[0]}" == "wsl.exe" ]]; then
  docker_root="$(to_wsl_path "$repo_root")"
  [[ -n "$docker_root" ]] || die "failed to convert repository path for WSL Docker"
fi

docker_tar_directory=""
if [[ -n "$tar_directory" ]]; then
  mkdir -p "$tar_directory"
  tar_directory="$(cd "$tar_directory" && pwd)"
  if [[ "${docker_cmd[0]}" == "wsl.exe" ]]; then
    docker_tar_directory="$(to_wsl_path "$tar_directory")"
  else
    docker_tar_directory="$tar_directory"
  fi
fi

# Engine 镜像内嵌的 OpenCode 版本由 Backend/opencode.version 锁定；
# OPENAGENT_OPENCODE_VERSION 仅用于升级前临时验证（latest 或严格语义化版本），提交仍以版本文件为准。
opencode_version_file="$repo_root/Backend/opencode.version"
[[ -f "$opencode_version_file" ]] || die "OpenCode version file does not exist: $opencode_version_file"
opencode_version="${OPENAGENT_OPENCODE_VERSION:-$(tr -d '[:space:]' < "$opencode_version_file")}"
if [[ ! "$opencode_version" =~ ^([0-9]+\.[0-9]+\.[0-9]+|latest)$ ]]; then
  die "invalid OpenCode version '$opencode_version' in $opencode_version_file (expected strict semver, or latest via OPENAGENT_OPENCODE_VERSION override)"
fi

engine_image="${OPENAGENT_ENGINE_IMAGE:-openagent-engine:latest}"
router_image="${OPENAGENT_ROUTER_IMAGE:-openagent-router:latest}"
chat_image="${OPENAGENT_CHAT_IMAGE:-openagent-chat:latest}"
# 本地镜像带可追溯的版本标识；Release 工作流会用正式 tag/revision 覆盖同一组 label。
platform_version="$(git -C "$repo_root" describe --tags --always --dirty 2>/dev/null || echo dev)"

"${docker_cmd[@]}" build \
  --tag "$engine_image" \
  --build-arg "OPENCODE_VERSION=$opencode_version" \
  --label "org.opencontainers.image.version=$platform_version" \
  --file "$docker_root/Backend/src/OpenAgent.Engine.Host/Dockerfile" \
  "$docker_root"

"${docker_cmd[@]}" build \
  --tag "$router_image" \
  --label "org.opencontainers.image.version=$platform_version" \
  --file "$docker_root/Backend/src/OpenAgent.Router/Dockerfile" \
  "$docker_root"

# Chat connection settings are supplied when the container starts.
"${docker_cmd[@]}" build \
  --tag "$chat_image" \
  --label "org.opencontainers.image.version=$platform_version" \
  --file "$docker_root/Frontend/OpenAgent.Chat/Dockerfile" \
  "$docker_root"

if [[ -n "$docker_tar_directory" ]]; then
  "${docker_cmd[@]}" save --output "$docker_tar_directory/openagent-engine.tar" "$engine_image"
  "${docker_cmd[@]}" save --output "$docker_tar_directory/openagent-router.tar" "$router_image"
  "${docker_cmd[@]}" save --output "$docker_tar_directory/openagent-chat.tar" "$chat_image"
fi
