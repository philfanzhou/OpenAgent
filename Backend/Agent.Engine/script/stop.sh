#!/bin/bash
set -e

CONTAINER_NAME="${CONTAINER_NAME:-agent-engine}"

if [ -z "$(docker ps -q --filter "name=^/${CONTAINER_NAME}$")" ]; then
    echo "Container '${CONTAINER_NAME}' is not running."
    exit 0
fi

echo "Stopping container: ${CONTAINER_NAME}"
docker stop "$CONTAINER_NAME"

echo "Removing container: ${CONTAINER_NAME}"
docker rm "$CONTAINER_NAME"

echo "Container '${CONTAINER_NAME}' stopped and removed."
