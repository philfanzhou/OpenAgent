#!/bin/bash
set -e

IMAGE_NAME="${IMAGE_NAME:-agent-engine}"
IMAGE_TAG="${IMAGE_TAG:-latest}"
DOCKERFILE="Dockerfile"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PUBLISH_DIR="${SCRIPT_DIR}/../publish"

if [ ! -d "${PUBLISH_DIR}" ]; then
    echo "Error: Publish directory not found at ${PUBLISH_DIR}"
    echo "Please run 'dotnet publish' from Visual Studio first, then SCP the output to this server."
    exit 1
fi

if [ ! -f "${PUBLISH_DIR}/${DOCKERFILE}" ]; then
    echo "Error: ${DOCKERFILE} not found in publish directory."
    echo "Make sure the Dockerfile is included in the publish output."
    exit 1
fi

if [ ! -f "${PUBLISH_DIR}/OpenAgent.Core.Engine.Host.dll" ]; then
    echo "Error: OpenAgent.Core.Engine.Host.dll not found in publish directory."
    echo "The publish output seems incomplete."
    exit 1
fi

echo "Building Docker image: ${IMAGE_NAME}:${IMAGE_TAG}"
echo "Publish directory: ${PUBLISH_DIR}"

docker build \
    -t "${IMAGE_NAME}:${IMAGE_TAG}" \
    -f "${PUBLISH_DIR}/${DOCKERFILE}" \
    "${PUBLISH_DIR}"

echo "Image built successfully: ${IMAGE_NAME}:${IMAGE_TAG}"
echo ""
echo "Run './start.sh' to start the container."
