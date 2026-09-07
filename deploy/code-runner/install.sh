#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo "Run this installer as root." >&2
  exit 1
fi

repository_root=${1:-$(pwd)}
if [[ ! -f "${repository_root}/Backend/src/OpenAgent.Runner/OpenAgent.Runner.csproj" ]]; then
  echo "Usage: sudo deploy/code-runner/install.sh <repository-root>" >&2
  exit 1
fi
if ! command -v dotnet >/dev/null || ! command -v docker >/dev/null; then
  echo ".NET 8 SDK and Docker CLI are required to publish the Runner." >&2
  exit 1
fi

apt-get update
apt-get install --yes --no-install-recommends \
  apt-transport-https ca-certificates curl gnupg openssl
curl -fsSL https://gvisor.dev/archive.key | gpg --dearmor --yes -o /usr/share/keyrings/gvisor-archive-keyring.gpg
echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/gvisor-archive-keyring.gpg] https://storage.googleapis.com/gvisor/releases release main" > /etc/apt/sources.list.d/gvisor.list
apt-get update
apt-get install --yes --no-install-recommends runsc
runsc install
systemctl restart docker
docker_host=${OPENAGENT_RUNNER_DOCKER_HOST:-}
if [[ -n ${docker_host} ]]; then
  export DOCKER_HOST=${docker_host}
fi
if ! docker info >/dev/null 2>&1; then
  echo "The configured Docker daemon is unavailable." >&2
  exit 1
fi
sandbox_image=${OPENAGENT_CODEACT_IMAGE:-openagent-codeact:local}
docker build --tag "${sandbox_image}" --file "${repository_root}/docker/codeact/Dockerfile" "${repository_root}"

if ! id openagent-runner >/dev/null 2>&1; then
  useradd --system --home-dir /var/lib/openagent-runner --no-create-home --shell /usr/sbin/nologin openagent-runner
fi

install -d -m 0755 /opt/openagent-runner/app
install -d -o openagent-runner -g openagent-runner -m 0700 /var/lib/openagent-runner /var/lib/openagent-runner/workspaces

case $(uname -m) in
  x86_64) runtime=linux-x64 ;;
  aarch64|arm64) runtime=linux-arm64 ;;
  *) echo "Unsupported CPU architecture: $(uname -m)" >&2; exit 1 ;;
esac
dotnet publish "${repository_root}/Backend/src/OpenAgent.Runner/OpenAgent.Runner.csproj" \
  --configuration Release --runtime "${runtime}" --self-contained true --output /opt/openagent-runner/app
chown -R root:root /opt/openagent-runner
chmod -R go-w /opt/openagent-runner

install -m 0644 "${repository_root}/deploy/code-runner/openagent-runner.service" \
  /etc/systemd/system/openagent-runner.service
if [[ ! -f /etc/openagent-runner.env ]]; then
  umask 077
  runner_key=$(openssl rand -hex 32)
  {
    echo 'ASPNETCORE_URLS=http://127.0.0.1:5088'
    echo "Runner__ApiKey=${runner_key}"
    echo 'Runner__WorkspaceRoot=/var/lib/openagent-runner/workspaces'
    echo 'Runner__DockerPath=/usr/bin/docker'
    echo "Runner__DockerHost=${docker_host}"
    echo 'Runner__Runtime=runsc'
    echo "Runner__SandboxImage=${sandbox_image}"
    echo 'Runner__SandboxPythonPath=/opt/openagent-code/venv/bin/python'
  } > /etc/openagent-runner.env
fi

if ! runuser -u openagent-runner -- env "DOCKER_HOST=${docker_host}" docker info >/dev/null 2>&1; then
  echo "openagent-runner cannot access the configured Docker daemon; fix socket ownership or OPENAGENT_RUNNER_DOCKER_HOST." >&2
  exit 1
fi

systemctl daemon-reload
systemctl enable openagent-runner.service
systemctl restart openagent-runner.service
for attempt in {1..30}; do
  if curl --fail --silent --output /dev/null http://127.0.0.1:5088/health; then
    echo "Runner healthy. Configure Engine with the endpoint and API key in /etc/openagent-runner.env."
    exit 0
  fi
  sleep 1
done
echo "Runner did not become healthy. Check journalctl -u openagent-runner.service." >&2
exit 1
