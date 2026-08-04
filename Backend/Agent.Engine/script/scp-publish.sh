#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

PUBLISH_DIR="${1:-}"
REMOTE_HOST="${2:-}"
REMOTE_PATH="${3:-/opt/agent-engine}"

if [ -z "$PUBLISH_DIR" ] || [ -z "$REMOTE_HOST" ]; then
    echo "Usage: ./scp-publish.sh <publish_dir> <remote_host> [remote_path]"
    echo ""
    echo "Example:"
    echo "  ./scp-publish.sh ./publish user@your-server.example.com /opt/agent-engine"
    echo ""
    echo "After SCP completes, SSH into the server and run:"
    echo "  cd ${REMOTE_PATH} && chmod +x script/*.sh && ./script/build.sh && ./script/start.sh"
    exit 1
fi

if [ ! -d "$PUBLISH_DIR" ]; then
    echo "Error: Publish directory '${PUBLISH_DIR}' not found."
    echo "Please run 'dotnet publish' from Visual Studio first."
    exit 1
fi

if [ ! -f "${PUBLISH_DIR}/OpenAgent.Core.Engine.Host.dll" ]; then
    echo "Error: OpenAgent.Core.Engine.Host.dll not found in publish directory."
    echo "The publish output seems incomplete."
    exit 1
fi

echo "Creating remote directory structure..."
ssh "${REMOTE_HOST}" "mkdir -p ${REMOTE_PATH}/publish ${REMOTE_PATH}/script"

echo "Uploading publish output to ${REMOTE_HOST}:${REMOTE_PATH}/publish ..."
scp -r "${PUBLISH_DIR}"/* "${REMOTE_HOST}:${REMOTE_PATH}/publish/"

DOCKERFILE_SRC="${SCRIPT_DIR}/../src/Host/Dockerfile"
if [ -f "${DOCKERFILE_SRC}" ]; then
    echo "Uploading Dockerfile to ${REMOTE_HOST}:${REMOTE_PATH}/publish ..."
    scp "${DOCKERFILE_SRC}" "${REMOTE_HOST}:${REMOTE_PATH}/publish/Dockerfile"
fi

echo "Uploading build scripts to ${REMOTE_HOST}:${REMOTE_PATH}/script ..."
scp "${SCRIPT_DIR}/build.sh" "${REMOTE_HOST}:${REMOTE_PATH}/script/"
scp "${SCRIPT_DIR}/start.sh" "${REMOTE_HOST}:${REMOTE_PATH}/script/"
scp "${SCRIPT_DIR}/stop.sh" "${REMOTE_HOST}:${REMOTE_PATH}/script/"

echo ""
echo "Upload complete! SSH into the server and run:"
echo "  ssh ${REMOTE_HOST}"
echo "  cd ${REMOTE_PATH}"
echo "  chmod +x script/*.sh"
echo "  ./script/build.sh"
echo "  ./script/start.sh"
