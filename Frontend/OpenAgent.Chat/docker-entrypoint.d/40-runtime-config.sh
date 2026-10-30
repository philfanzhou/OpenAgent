#!/bin/sh
set -eu

# Serialize only public settings; never expose the container environment.
scheme="${OPENAGENT_PUBLIC_SCHEME:-https}"
host="${OPENAGENT_PUBLIC_HOST:-localhost}"
jq -n \
  --arg routerBaseUrl "${OPENAGENT_ROUTER_PUBLIC_URL:-$scheme://$host:${OPENAGENT_ROUTER_PORT:-8082}}" \
  --arg engineBaseUrl "${OPENAGENT_ENGINE_PUBLIC_URL:-$scheme://$host:${OPENAGENT_ENGINE_PORT:-8083}}" \
  --arg tenantId "${OPENAGENT_TENANT_ID:-development}" \
  '{routerBaseUrl: $routerBaseUrl, engineBaseUrl: $engineBaseUrl, tenantId: $tenantId}' \
  > /usr/share/nginx/html/runtime-config.json
