#!/bin/bash
set -e

IMAGE_NAME="${IMAGE_NAME:-agent-engine}"
IMAGE_TAG="${IMAGE_TAG:-latest}"
CONTAINER_NAME="${CONTAINER_NAME:-agent-engine}"
HOST_PORT="${HOST_PORT:-8080}"
NETWORK="${NETWORK:-}"

REDIS_HOST="${REDIS_HOST:-host.docker.internal}"
REDIS_PORT="${REDIS_PORT:-6379}"
REDIS_PASSWORD="${REDIS_PASSWORD:-}"

ENGINE_FRAMEWORK="${ENGINE_FRAMEWORK:-Mock}"
ASPNETCORE_ENV="${ASPNETCORE_ENV:-Production}"

OTEL_ENDPOINT="${OTEL_ENDPOINT:-}"
AUTH_AUTHORITY="${AUTH_AUTHORITY:-}"
AUTH_AUDIENCE="${AUTH_AUDIENCE:-agent-api}"

if [ -n "$(docker ps -q --filter "name=^/${CONTAINER_NAME}$")" ]; then
    echo "Container '${CONTAINER_NAME}' is already running, stopping it..."
    docker stop "$CONTAINER_NAME"
fi

if [ -n "$(docker ps -aq --filter "name=^/${CONTAINER_NAME}$")" ]; then
    echo "Removing old container..."
    docker rm "$CONTAINER_NAME"
fi

DOCKER_ARGS=(
    --name "$CONTAINER_NAME"
    --restart unless-stopped
    --add-host=host.docker.internal:host-gateway
    -e TZ=UTC
    -p "${HOST_PORT}:80"
    -e "ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENV}"
    -e "Engine__Framework=${ENGINE_FRAMEWORK}"
    -e "OpenTelemetry__OtlpEndpoint=${OTEL_ENDPOINT}"
    -e "Authentication__Authority=${AUTH_AUTHORITY}"
    -e "Authentication__Audience=${AUTH_AUDIENCE}"
    --memory="1g"
    --cpus="1.0"
)

if [ -n "$REDIS_HOST" ]; then
    if [ -n "$REDIS_PASSWORD" ]; then
        DOCKER_ARGS+=(-e "ConnectionStrings__Redis=${REDIS_HOST}:${REDIS_PORT},password=${REDIS_PASSWORD}")
    else
        DOCKER_ARGS+=(-e "ConnectionStrings__Redis=${REDIS_HOST}:${REDIS_PORT}")
    fi
fi

if [ -n "$NETWORK" ]; then
    DOCKER_ARGS+=(--network "$NETWORK")
fi

echo "Starting container: ${CONTAINER_NAME}"
echo "  Image:  ${IMAGE_NAME}:${IMAGE_TAG}"
echo "  Port:   ${HOST_PORT} -> 80"
echo "  Env:    ${ASPNETCORE_ENV}"
echo "  Engine: ${ENGINE_FRAMEWORK}"
echo "  Auth:   ${AUTH_AUTHORITY}"

docker run -d "${DOCKER_ARGS[@]}" "${IMAGE_NAME}:${IMAGE_TAG}"

echo ""
echo "Container '${CONTAINER_NAME}' started."
echo "  Health check: curl http://localhost:${HOST_PORT}/health"
echo "  API base:     curl http://localhost:${HOST_PORT}/api/v1/agent"
echo ""
echo "=== Real-time Logs ==="
docker logs -f -t "$CONTAINER_NAME"
