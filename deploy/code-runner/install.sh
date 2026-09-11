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
if ! command -v dotnet >/dev/null; then
  echo ".NET 8 SDK is required to publish the Runner." >&2
  exit 1
fi

apt-get update
apt-get install --yes --no-install-recommends \
  bubblewrap apparmor-profiles apparmor-utils python3 python3-venv \
  libreoffice-impress libreoffice-calc fonts-noto-cjk openssl curl

if [[ $(sysctl -n kernel.apparmor_restrict_unprivileged_userns 2>/dev/null || true) == 1 ]]; then
  apparmor_profile=/etc/apparmor.d/bwrap-userns-restrict
  if [[ ! -e ${apparmor_profile} ]]; then
    packaged_profile=/usr/share/apparmor/extra-profiles/bwrap-userns-restrict
    if [[ ! -f ${packaged_profile} ]]; then
      echo "AppArmor restricts user namespaces, but the packaged Bubblewrap profile is unavailable." >&2
      exit 1
    fi
    install -m 0644 "${packaged_profile}" "${apparmor_profile}"
  fi
  apparmor_parser --replace "${apparmor_profile}"
fi

if ! id openagent-runner >/dev/null 2>&1; then
  useradd --system --home-dir /var/lib/openagent-runner --no-create-home --shell /usr/sbin/nologin openagent-runner
fi

install -d -m 0755 /opt/openagent-runner/app /opt/openagent-code
install -d -o openagent-runner -g openagent-runner -m 0700 /var/lib/openagent-runner /var/lib/openagent-runner/workspaces
python3 -m venv /opt/openagent-code/venv
/opt/openagent-code/venv/bin/pip install --disable-pip-version-check --no-cache-dir \
  --requirement "${repository_root}/Backend/src/OpenAgent.Runner/sandbox/requirements.txt"

case $(uname -m) in
  x86_64) runtime=linux-x64 ;;
  aarch64|arm64) runtime=linux-arm64 ;;
  *) echo "Unsupported CPU architecture: $(uname -m)" >&2; exit 1 ;;
esac
dotnet publish "${repository_root}/Backend/src/OpenAgent.Runner/OpenAgent.Runner.csproj" \
  --configuration Release --runtime "${runtime}" --self-contained true --output /opt/openagent-runner/app
chown -R root:root /opt/openagent-runner /opt/openagent-code
chmod -R go-w /opt/openagent-runner /opt/openagent-code

install -m 0644 "${repository_root}/deploy/code-runner/openagent-runner.service" \
  /etc/systemd/system/openagent-runner.service
if [[ ! -f /etc/openagent-runner.env ]]; then
  umask 077
  runner_key=$(openssl rand -hex 32)
  {
    echo 'ASPNETCORE_URLS=http://127.0.0.1:5088'
    echo "Runner__ApiKey=${runner_key}"
    echo 'Runner__WorkspaceRoot=/var/lib/openagent-runner/workspaces'
    echo 'Runner__BubblewrapPath=/usr/bin/bwrap'
    echo 'Runner__PythonPath=/opt/openagent-code/venv/bin/python'
  } > /etc/openagent-runner.env
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
