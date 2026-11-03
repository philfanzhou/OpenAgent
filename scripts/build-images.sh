#!/usr/bin/env bash
set -euo pipefail

# 构建应用镜像；不启动或修改任何容器。
# 用法：scripts/build-images.sh [--app all|engine|router|runner] [--env-file path/to/build.env] [--docker-mode auto|docker|wsl-docker] [--tar-dir directory]

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
env_file=""
app="all"
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

while [[ $# -gt 0 ]]; do
  case "$1" in
    --app)
      [[ $# -ge 2 ]] || die "--app requires all, engine, router, or runner"
      app="$2"
      shift 2
      ;;
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
  [[ -f "$env_file" ]] || die "environment file does not exist: $env_file"
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

case "$app" in
  all) apps=(engine router runner) ;;
  engine|router|runner) apps=("$app") ;;
  *) die "unsupported build application '$app'" ;;
esac
compose_args=(--file "$docker_root/deploy/build.compose.yml")
if [[ -n "$env_file" ]]; then
  if [[ "${docker_cmd[0]}" == "wsl.exe" ]]; then env_file="$(to_wsl_path "$env_file")"; fi
  compose_args+=(--env-file "$env_file")
fi
"${docker_cmd[@]}" compose "${compose_args[@]}" build "${apps[@]}"
if [[ -n "$docker_tar_directory" ]]; then
  for build_app in "${apps[@]}"; do
    image="$("${docker_cmd[@]}" compose "${compose_args[@]}" config --images "$build_app")"
    "${docker_cmd[@]}" save --output "$docker_tar_directory/openagent-$build_app.tar" "$image"
  done
fi
