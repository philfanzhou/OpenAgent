#!/usr/bin/env bash
# Optional official-source fallback when the pinned MinIO registry image is unavailable.
set -euo pipefail
fx_build="$(mktemp -d)"
trap 'rm -rf "$fx_build"' EXIT
fx_release=RELEASE.2025-04-22T22-12-26Z
fx_image="${FILE_EXCHANGE_MINIO_IMAGE:-openagent-file-exchange-minio:local}"
go mod download -json "github.com/minio/minio@${fx_release}" > "$fx_build/module.json"
fx_source="$(python3 - "$fx_build/module.json" <<'PY'
import json, sys
with open(sys.argv[1]) as stream:
    module = json.load(stream)
assert module['Version'] == 'v0.0.0-20250422221226-0d7408fc9969', 'Unexpected MinIO revision'
if 'Origin' in module:
    assert module['Origin']['Hash'] == '0d7408fc9969caf07de6a8c3a84f9fbb10a6739e', 'Unexpected MinIO origin'
assert module['Sum'] == 'h1:XqfWRNtM5qMJyGQ4IFsnfHb83fCaRYhrKxJJ0ajXdrQ=', 'Unexpected MinIO source checksum'
print(module['Dir'])
PY
)"
case "$(docker info --format '{{.Architecture}}')" in
  aarch64|arm64) fx_arch=arm64 ;;
  x86_64|amd64) fx_arch=amd64 ;;
  *) echo 'Unsupported Docker architecture.' >&2; exit 1 ;;
esac
(cd "$fx_source" && CGO_ENABLED=0 GOOS=linux GOARCH="$fx_arch" go build -p 4 -trimpath -ldflags='-s -w' -o "$fx_build/minio" .)
cat > "$fx_build/Dockerfile" <<'EOF'
FROM alpine:3
COPY minio /usr/local/bin/minio
LABEL org.opencontainers.image.source="https://github.com/minio/minio" \
      org.opencontainers.image.revision="0d7408fc9969caf07de6a8c3a84f9fbb10a6739e"
ENTRYPOINT ["/usr/local/bin/minio"]
EOF
docker build --tag "$fx_image" "$fx_build"
