#!/usr/bin/env bash
set -euo pipefail

# Run in Linux, macOS or WSL. Infrastructure is managed separately.
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
action="${1:-all}"
[[ $# -le 1 ]] || { echo 'usage: scripts/deploy.sh [init|nginx|app|all|check]' >&2; exit 1; }
case "$action" in
  init)
    umask 077
    for directory in deploy/openagent deploy/openagent/{engine,router} deploy/nginx; do
      file="$directory/.env"
      if [[ ! -e "$file" && ! -L "$file" ]]; then
        (set -o noclobber; cat "$file.example" > "$file")
        echo "Created $file"
      fi
    done
    echo 'Configure deploy/openagent/.env, its service .env files, and deploy/nginx/.env.'
    exit 0 ;;
  nginx) stacks=(nginx) ;;
  app) stacks=(openagent) ;;
  all|check) stacks=(nginx openagent) ;;
  -h|--help) echo 'usage: scripts/deploy.sh [init|nginx|app|all|check]'; exit 0 ;;
  *) echo "unknown action: $action" >&2; exit 1 ;;
esac

if [[ "$action" != app ]]; then
  [[ -f deploy/nginx/.env ]] || { echo 'Run scripts/deploy.sh init first.' >&2; exit 1; }
  # Only Nginx's shell-compatible public settings are read for certificate preflight.
  # Application env_files (connection strings and credentials) are parsed by Compose only.
  set -a
  source deploy/nginx/.env
  set +a
  cert_dir="${OPENAGENT_TLS_CERT_DIR:-$repo_root/deploy/openagent/nginx/certs}"
  [[ "$cert_dir" = /* && -s "$cert_dir/tls.crt" && -s "$cert_dir/tls.key" ]] \
    || { echo 'Set an absolute TLS directory containing tls.crt and tls.key.' >&2; exit 1; }
fi

compose() {
  local stack="$1" project=openagent-app
  shift
  [[ "$stack" != nginx ]] || project=openagent-nginx
  docker compose --project-name "$project" --env-file "deploy/$stack/.env" --file "deploy/$stack/docker-compose.yml" "$@"
}
for stack in "${stacks[@]}"; do compose "$stack" config --quiet; done
if owner="$(docker network inspect openagent --format '{{index .Labels "com.docker.compose.project"}}/{{index .Labels "com.docker.compose.network"}}' 2>/dev/null)"; then
  [[ "$owner" == openagent-nginx/openagent ]] \
    || { echo 'Migrate the existing openagent network to Nginx ownership; see docs/operations/deployment.md.' >&2; exit 1; }
elif [[ "$action" == app ]]; then
  echo 'Deploy Nginx first: scripts/deploy.sh nginx' >&2
  exit 1
fi
[[ "$action" != check ]] || { echo 'Deployment configuration is valid.'; exit 0; }
for stack in "${stacks[@]}"; do compose "$stack" up --detach --no-build --pull never --wait; done
