#!/usr/bin/env bash
# Disposable Linux/OpenCode/MinIO verification. Does not use running platform services or user credentials.
set -euo pipefail
cd "$(dirname "$0")/.."

fx_id="fx-files-$(date +%s)-$$"
fx_image="${FILE_EXCHANGE_TEST_IMAGE:-openagent-file-exchange-tests:${fx_id}}"
fx_storage_image="${FILE_EXCHANGE_MINIO_IMAGE:-quay.io/minio/minio:RELEASE.2025-04-22T22-12-26Z}"
fx_minio="${fx_id}-s3"
fx_network="${fx_id}-net"
fx_version="$(tr -d '\r\n' < Backend/opencode.version)"
cleanup() {
  docker rm -f "${fx_id}-tests" "$fx_minio" >/dev/null 2>&1 || true
  docker network rm "$fx_network" >/dev/null 2>&1 || true
  if [[ -z "${FILE_EXCHANGE_TEST_IMAGE:-}" ]]; then
    docker image rm "$fx_image" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

if [[ -z "${FILE_EXCHANGE_TEST_IMAGE:-}" ]]; then
  docker build --file Backend/src/OpenAgent.Engine.Host/Dockerfile --target runtime-tests \
    --build-arg "OPENCODE_VERSION=$fx_version" --tag "$fx_image" .
fi
docker network create "$fx_network" >/dev/null
docker run --detach --name "$fx_minio" --network "$fx_network" --network-alias file-exchange-s3 \
  --publish 127.0.0.1::9000 \
  --env MINIO_ROOT_USER=fx-test-user --env MINIO_ROOT_PASSWORD=fx-test-password \
  "$fx_storage_image" server /data >/dev/null
fx_address="$(docker port "$fx_minio" 9000/tcp)"
curl --fail --silent --show-error --retry 15 --retry-delay 1 --retry-all-errors \
  "http://${fx_address}/minio/health/live" >/dev/null
docker run --rm --name "${fx_id}-tests" --network "$fx_network" \
  --env OPENAGENT_TEST_OPENCODE=/usr/local/bin/opencode \
  --env OPENAGENT_TEST_S3_URL=http://file-exchange-s3:9000 \
  --env OPENAGENT_TEST_S3_ACCESS_KEY=fx-test-user --env OPENAGENT_TEST_S3_SECRET_KEY=fx-test-password \
  --entrypoint dotnet "$fx_image" test Backend/tests/OpenAgent.Engine.Tests \
  --no-build --no-restore --nologo --filter FullyQualifiedName~OpenCodeFileExchangeTests \
  --logger 'console;verbosity=detailed'
