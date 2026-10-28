#!/bin/sh
# 依据 OPENAGENT_* 环境变量生成浏览器端运行时配置，覆盖 dist 内的占位文件。
# 公开地址不进入镜像：同一镜像可服务任意域名，与 deploy/ 环境文件使用同名统一变量。
set -eu

valid_or_default() {
    # 值会拼进 JS 字符串：先拒绝换行（grep 按行匹配会绕过白名单），再按白名单校验，非法时回退默认值。
    if [ "$(printf '%s' "$1" | tr -d '\n\r')" = "$1" ] && printf '%s' "$1" | grep -Eq "$2"; then
        printf '%s' "$1"
    else
        printf '%s' "$3"
    fi
}

scheme="$(valid_or_default "${OPENAGENT_PUBLIC_SCHEME:-https}" '^(http|https)$' https)"
host="$(valid_or_default "${OPENAGENT_PUBLIC_HOST:-localhost}" '^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$' localhost)"
router_port="$(valid_or_default "${OPENAGENT_ROUTER_PORT:-8082}" '^[0-9]{1,5}$' 8082)"
engine_port="$(valid_or_default "${OPENAGENT_ENGINE_PORT:-8083}" '^[0-9]{1,5}$' 8083)"
tenant_id="$(valid_or_default "${OPENAGENT_TENANT_ID:-development}" '^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$' development)"

target="${OPENAGENT_RUNTIME_CONFIG_PATH:-/usr/share/nginx/html/runtime-config.js}"
cat > "$target" <<EOF
// 容器启动时由 docker-entrypoint.d 生成；修改环境变量后重启容器即可更新。
window.__OPENAGENT_RUNTIME_CONFIG__ = {
  routerBaseUrl: '${scheme}://${host}:${router_port}',
  engineBaseUrl: '${scheme}://${host}:${engine_port}',
  tenantId: '${tenant_id}',
};
EOF

echo "$0: generated $target (router=${scheme}://${host}:${router_port}, engine=${scheme}://${host}:${engine_port}, tenant=${tenant_id})"
