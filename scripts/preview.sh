#!/usr/bin/env bash
set -euo pipefail

# OpenAgent 并行预览实例管理脚本
#
# 用法（需在 WSL 中、worktree 根目录执行）:
#   wsl -e bash -lc 'cd <worktree> && bash scripts/preview.sh up <slug>'
#   wsl -e bash -lc 'cd <worktree> && bash scripts/preview.sh down <slug>'
#   wsl -e bash -lc 'cd <worktree> && bash scripts/preview.sh status'
#   wsl -e bash -lc 'cd <worktree> && bash scripts/preview.sh cleanup'
#
# 每个预览只起 engine + router + chat，复用主 openagent 栈的 postgres/redis/minio
# （经 host.docker.internal 直连，宿主端口以运行中容器为准）。
# 端口与 Redis DB 序号经 flock 原子分配，支持多个 agent 并发开预览不冲突。

PREVIEW_DIR=".preview"
LOCK_FILE="$PREVIEW_DIR/lock"
ALLOC_FILE="$PREVIEW_DIR/allocations"
COMPOSE_FILE="docker/preview.compose.yml"

# 端口区间（避开主实例 engine 5208 / router 5001 / chat 8081）
ENGINE_BASE=5210
ROUTER_BASE=5010
CHAT_BASE=8090
ENGINE_MAX=5299
ROUTER_MAX=5099
CHAT_MAX=8199
REDIS_DB_MAX=15

# 共享基础设施宿主端口（由 read_infra_ports 覆盖，仅 up 需要）
pg_port=5432
redis_port=6379
minio_port=9000

die() { echo "error: $*" >&2; exit 1; }

validate_slug() {
  local slug="$1"
  [[ -z "$slug" ]] && die "missing slug. usage: preview.sh <up|down> <slug>"
  [[ "$slug" =~ ^[a-z0-9][a-z0-9-]*$ ]] || die "invalid slug '$slug' (use [a-z0-9-])"
}

ensure_preview_dir() { mkdir -p "$PREVIEW_DIR"; }

# 从运行中的共享基础设施容器读取实际宿主端口，不依赖任何 .env
read_infra_ports() {
  for pair in "openagent-postgres-1:5432" "openagent-redis-1:6379" "openagent-minio-1:9000"; do
    local cname="${pair%%:*}"
    docker inspect -f "{{.State.Health.Status}}" "$cname" >/dev/null 2>&1 \
      || die "共享基础设施容器 $cname 未运行。请先在主 openagent 栈运行 \`docker compose up -d\` 后再开预览。"
  done
  pg_port="$(docker port openagent-postgres-1 5432/tcp 2>/dev/null | sed -E 's/.*:([0-9]+)$/\1/' | head -1)"
  redis_port="$(docker port openagent-redis-1 6379/tcp 2>/dev/null | sed -E 's/.*:([0-9]+)$/\1/' | head -1)"
  minio_port="$(docker port openagent-minio-1 9000/tcp 2>/dev/null | sed -E 's/.*:([0-9]+)$/\1/' | head -1)"
  [[ -n "$pg_port" && -n "$redis_port" && -n "$minio_port" ]] \
    || die "无法读取共享基础设施的宿主端口(postgres/redis/minio)"
}

write_env_file() {
  local line
  local slug engine_port router_port chat_port redis_db
  IFS= read -r line
  IFS=$'\t' read -r slug engine_port router_port chat_port redis_db <<<"$line"
  cat > "$PREVIEW_DIR/$slug.env" <<EOF
PREVIEW_SLUG=$slug
PREVIEW_ENGINE_PORT=$engine_port
PREVIEW_ROUTER_PORT=$router_port
PREVIEW_CHAT_PORT=$chat_port
PREVIEW_REDIS_DB=$redis_db
OPENAGENT_POSTGRES_PORT=$pg_port
OPENAGENT_REDIS_PORT=$redis_port
OPENAGENT_MINIO_PORT=$minio_port
OPENAGENT_OTLP_ENDPOINT=http://host.docker.internal:4317
EOF
}

host_port_free() { ! ss -tlnH 2>/dev/null | grep -q ":$1[[:space:]]"; }

pick_next() {
  local base="$1" max="$2"
  local used="$3"
  local p
  for ((p = base; p <= max; p++)); do
    if ! grep -qw "$p" <<<"$used" && host_port_free "$p"; then
      echo "$p"
      return 0
    fi
  done
  return 1
}

used_ports() { awk -F'\t' -v c="$1" 'NR>0 {print $c}' "$ALLOC_FILE" 2>/dev/null || true; }

cmd_up() {
  local slug="$2"
  validate_slug "$slug"
  ensure_preview_dir
  read_infra_ports

  local line
  line="$(grep -P "^${slug}\t" "$ALLOC_FILE" 2>/dev/null || true)"
  if [[ -n "$line" ]]; then
    echo "预览 '$slug' 已分配，复用现有槽位。"
    write_env_file <<<"$line"
  else
    exec 9>"$LOCK_FILE"
    flock 9
    line="$(grep -P "^${slug}\t" "$ALLOC_FILE" 2>/dev/null || true)"
    if [[ -z "$line" ]]; then
      local engine_port router_port chat_port redis_db
      engine_port="$(pick_next "$ENGINE_BASE" "$ENGINE_MAX" "$(used_ports 2)")" \
        || die "无可用 engine 端口($ENGINE_BASE-$ENGINE_MAX)"
      router_port="$(pick_next "$ROUTER_BASE" "$ROUTER_MAX" "$(used_ports 3)")" \
        || die "无可用 router 端口($ROUTER_BASE-$ROUTER_MAX)"
      chat_port="$(pick_next "$CHAT_BASE" "$CHAT_MAX" "$(used_ports 4)")" \
        || die "无可用 chat 端口($CHAT_BASE-$CHAT_MAX)"
      redis_db="$(pick_next 1 "$REDIS_DB_MAX" "$(used_ports 5)")" \
        || die "无可用 Redis DB 序号(1-$REDIS_DB_MAX)"
      printf '%s\t%s\t%s\t%s\t%s\n' "$slug" "$engine_port" "$router_port" "$chat_port" "$redis_db" >>"$ALLOC_FILE"
      line="$(grep -P "^${slug}\t" "$ALLOC_FILE")"
    fi
    flock -u 9
    write_env_file <<<"$line"
  fi

  echo "==> 构建并启动预览 '$slug' (engine=$engine_port router=$router_port chat=$chat_port redis_db=$redis_db)"
  docker compose -f "$COMPOSE_FILE" --env-file "$PREVIEW_DIR/$slug.env" \
    -p "openagent-preview-$slug" up -d --build

  local i
  for i in $(seq 1 90); do
    curl -sf -o /dev/null "http://localhost:$chat_port/" && break
    sleep 2
  done
  curl -sf -o /dev/null "http://localhost:$chat_port/" \
    || die "预览 chat 未就绪: http://localhost:$chat_port"

  cat <<EOF

==> 预览就绪: '$slug'
  预览(前端): http://localhost:${chat_port}
  Router:     http://localhost:${router_port}
  Engine:     http://localhost:${engine_port}
  Ready 检查: http://localhost:${engine_port}/health/ready
  如需销毁:   scripts/preview.sh down $slug
EOF
}

cmd_down() {
  local slug="$2"
  validate_slug "$slug"
  ensure_preview_dir

  local line engine_port router_port chat_port redis_db
  line="$(grep -P "^${slug}\t" "$ALLOC_FILE" 2>/dev/null || true)"
  if [[ -z "$line" ]]; then
    echo "预览 '$slug' 没有分配记录。"
    return 0
  fi
  IFS=$'\t' read -r slug engine_port router_port chat_port redis_db <<<"$line"

  if docker ps -a --format '{{.Names}}' | grep -q "^openagent-preview-$slug-engine-1$"; then
    if [[ -f "$PREVIEW_DIR/$slug.env" ]]; then
      docker compose -f "$COMPOSE_FILE" --env-file "$PREVIEW_DIR/$slug.env" \
        -p "openagent-preview-$slug" down -v --rmi local
    else
      docker compose -f "$COMPOSE_FILE" -p "openagent-preview-$slug" down -v --rmi local
    fi
  fi

  exec 9>"$LOCK_FILE"
  flock 9
  grep -vP "^${slug}\t" "$ALLOC_FILE" >"$ALLOC_FILE.tmp" 2>/dev/null || true
  mv "$ALLOC_FILE.tmp" "$ALLOC_FILE" 2>/dev/null || true
  flock -u 9
  rm -f "$PREVIEW_DIR/$slug.env"

  echo "预览 '$slug' 已销毁，槽位已释放 (engine=$engine_port router=$router_port chat=$chat_port redis_db=$redis_db)。"
}

cmd_status() {
  ensure_preview_dir
  if [[ ! -s "$ALLOC_FILE" ]]; then
    echo "暂无预览实例。"
    return 0
  fi
  echo -e "SLUG\tENGINE\tROUTER\tCHAT\tREDIS_DB\t状态"
  local slug engine_port router_port chat_port redis_db
  while IFS=$'\t' read -r slug engine_port router_port chat_port redis_db; do
    local state="-"
    docker ps --format '{{.Names}}' | grep -q "^openagent-preview-$slug-chat-1$" && state="running"
    printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$slug" "$engine_port" "$router_port" "$chat_port" "$redis_db" "$state"
  done <"$ALLOC_FILE"
}

cmd_cleanup() {
  ensure_preview_dir
  if [[ ! -s "$ALLOC_FILE" ]]; then
    echo "暂无预览实例。"
    return 0
  fi
  local lines removed=0
  lines="$(cat "$ALLOC_FILE")"
  local slug engine_port router_port chat_port redis_db
  while IFS=$'\t' read -r slug engine_port router_port chat_port redis_db; do
    if ! docker ps -a --format '{{.Names}}' | grep -q "^openagent-preview-$slug-engine-1$"; then
      echo "清理孤儿分配 '$slug' (容器已消失)"
      exec 9>"$LOCK_FILE"
      flock 9
      grep -vP "^${slug}\t" "$ALLOC_FILE" >"$ALLOC_FILE.tmp" 2>/dev/null || true
      mv "$ALLOC_FILE.tmp" "$ALLOC_FILE" 2>/dev/null || true
      flock -u 9
      rm -f "$PREVIEW_DIR/$slug.env"
      removed=1
    fi
  done <<<"$lines"
  [[ "$removed" == "0" ]] && echo "没有需要清理的孤儿分配。"
}

case "${1:-}" in
  up) cmd_up "$@" ;;
  down) cmd_down "$@" ;;
  status) cmd_status ;;
  cleanup) cmd_cleanup ;;
  *) die "usage: preview.sh <up|down|status|cleanup> [slug]" ;;
esac
