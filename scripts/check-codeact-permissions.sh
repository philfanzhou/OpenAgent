#!/usr/bin/env bash
# Offline preflight for PR80. Never installs packages or relaxes host policy.
set -euo pipefail

usage() {
  printf '%s\n' \
    'Usage: bash check-codeact-permissions.sh [--host]' \
    '       bash check-codeact-permissions.sh --image LOCAL_IMAGE' \
    '       bash check-codeact-permissions.sh --container RUNNING_CONTAINER' \
    '' \
    '--host: run as the intended non-root Runner user; requires bwrap and coreutils.' \
    '--image: test a local image with a non-root, read-only, non-privileged Docker baseline.' \
    '--container: test the existing container using its configured default user and policy.' \
    'No pulls, package installs, policy changes, or Docker socket mounts are performed.' \
    'Exit codes: 0=preflight passed, 1=probe failed, 2=dependency/access missing, 3=unsupported context.' \
    'PASS covers namespaces/mounts/basic limits only, not Python/Office/HTTP or all Runner paths.'
}

stop() { printf '%s\n' "$2" >&2; exit "$1"; }
mode=${1:---host}
case "$mode" in
  -h|--help) usage; exit 0 ;;
  --image|--container)
    [[ $# == 2 && -n $2 && $2 != -* ]] || { usage; exit 2; }
    command -v docker >/dev/null || stop 2 'MISSING: Docker CLI.'
    docker info --format 'Docker: {{.OSType}}/{{.Architecture}}' || stop 2 'UNAVAILABLE: Docker daemon access.'
    if [[ $mode == --image ]]; then
      docker image inspect "$2" --format 'Image: {{.Id}} {{.Os}}/{{.Architecture}}' \
        || stop 2 'MISSING: local image; import an offline image first. No pull attempted.'
      # The daemon never mounts a host path; the script travels through stdin.
      docker run --rm -i --pull=never --network=none --read-only \
        --user 65532:65532 --cap-drop=ALL --security-opt=no-new-privileges:true \
        --pids-limit=256 --memory=2g --cpus=2 \
        --tmpfs /tmp:rw,nosuid,nodev,noexec,size=128m,mode=1777 \
        --entrypoint /bin/bash "$2" -s -- --host < "${BASH_SOURCE[0]}"
    else
      docker container inspect "$2" --format \
        'Container: {{.Name}} User={{.Config.User}} Privileged={{.HostConfig.Privileged}} SecurityOpt={{json .HostConfig.SecurityOpt}}' \
        || stop 2 'MISSING: container.'
      docker exec -i "$2" /bin/bash -s -- --host < "${BASH_SOURCE[0]}"
    fi
    exit $? ;;
  --host) [[ $# -le 1 ]] || { usage; exit 2; } ;;
  *) usage; exit 2 ;;
esac

printf 'Context: kernel=%s arch=%s uid=%s\n' "$(uname -r)" "$(uname -m)" "$EUID"
[[ $(uname -s) == Linux ]] || stop 3 'UNSUPPORTED: Linux is required; use --image for a Linux Docker environment.'
[[ $EUID != 0 ]] || stop 3 'UNSUPPORTED: root does not test the non-root Runner identity. Use sudo -u openagent-runner bash SCRIPT --host.'
for dependency in bwrap timeout mktemp readlink awk rm mkdir cat id; do
  command -v "$dependency" >/dev/null || stop 2 "MISSING: $dependency. Prepare offline dependencies; permissions remain untested."
done
[[ -x /usr/bin/prlimit && -x /usr/bin/unshare && -x /bin/sh ]] || stop 2 'MISSING: /usr/bin/prlimit, /usr/bin/unshare or /bin/sh.'
bwrap --version
for setting in /proc/sys/user/max_user_namespaces /proc/sys/kernel/unprivileged_userns_clone /proc/sys/kernel/apparmor_restrict_unprivileged_userns; do
  if [[ -r $setting ]]; then
    IFS= read -r value < "$setting" || true
    printf 'Policy: %s=%s\n' "${setting##*/}" "$value"
  fi
done

umask 077
probe_dir=$(mktemp -d /tmp/openagent-permissions.XXXXXXXX) || stop 2 'UNAVAILABLE: cannot create a private test directory under /tmp.'
cleanup() { rm -rf -- "$probe_dir"; }
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
mkdir "$probe_dir/input"
printf 'probe-input\n' > "$probe_dir/input/data.txt"
printf 'host-only\n' > "$probe_dir/host-marker"

# Same namespace and procfs flags as BubblewrapCodeExecutor.BuildProbeArguments,
# extended with read-only input/root, bounded tmpfs and the default prlimit values.
arguments=(
  --unshare-user --unshare-ipc --unshare-pid --unshare-net --unshare-uts
  --unshare-cgroup-try --disable-userns --new-session --die-with-parent
  --uid 65532 --gid 65532 --hostname openagent-sandbox --clearenv
  --ro-bind /usr /usr --symlink usr/bin /bin --symlink usr/lib /lib
  --symlink usr/lib64 /lib64 --symlink usr/sbin /sbin
  --ro-bind-try /etc/ld.so.cache /etc/ld.so.cache
  --ro-bind "$probe_dir/input" /input
  --size 134217728 --perms 1777 --tmpfs /work
  --size 33554432 --perms 1777 --tmpfs /output
  --size 67108864 --perms 1777 --tmpfs /tmp
  --proc /proc --dev /dev --chdir /work --setenv PATH /usr/bin:/bin
  --remount-ro /
  -- /usr/bin/prlimit --as=1610612736:1610612736 --cpu=125:125
  --nproc=64:64 --nofile=256:256 --fsize=20971520:20971520 --core=0:0
  -- /bin/sh -ec '
    printf "CHECK: sandbox identity and capabilities\n"
    test "$(id -u)" = 65532
    test "$(id -g)" = 65532
    test "$(uname -n)" = openagent-sandbox
    awk "/^CapEff:|^CapPrm:/ { if (\$2 !~ /^0+$/) exit 1; caps++ }
         /^NoNewPrivs:/ { if (\$2 != 1) exit 1; nnp++ }
         END { if (caps != 2 || nnp != 1) exit 1 }" /proc/self/status
    test -z "${OPENAGENT_PROBE_SENTINEL+x}"
    printf "CHECK: input and namespace isolation\n"
    test "$(cat /input/data.txt)" = probe-input
    test ! -e "$1"
    test "$(readlink /proc/self/ns/net)" != "$2"
    test "$(readlink /proc/self/ns/pid)" != "$3"
    test "$(readlink /proc/self/ns/user)" != "$4"
    if (printf x > /input/data.txt) 2>/dev/null; then exit 1; fi
    if (printf x > /root-write-test) 2>/dev/null; then exit 1; fi
    if /usr/bin/unshare --user /bin/true 2>/dev/null; then exit 1; fi
    printf "CHECK: writable temporary mounts\n"
    printf output > /output/result.txt
    printf work > /work/result.txt
    printf temp > /tmp/result.txt
    test "$(cat /output/result.txt)" = output
    printf "PASS: non-root, zero capabilities, no-new-privileges, isolated namespaces, cleared environment, read-only input/root, writable temporary mounts and prlimit launch.\n"
  ' probe "$probe_dir/host-marker"
  "$(readlink /proc/self/ns/net)" "$(readlink /proc/self/ns/pid)" "$(readlink /proc/self/ns/user)"
)

set +e
OPENAGENT_PROBE_SENTINEL=must-not-enter-sandbox timeout --kill-after=5s 30s \
  bwrap "${arguments[@]}" > "$probe_dir/result.log" 2>&1
probe_status=$?
set -e
cat "$probe_dir/result.log"
if [[ $probe_status != 0 ]]; then
  printf 'FAIL: Bubblewrap probe exited %s. No permissions were changed.\n' "$probe_status" >&2
  printf '%s\n' \
    'Operation not permitted / Permission denied: inspect user namespace, seccomp, AppArmor/SELinux and mount restrictions.' \
    'Unknown option / executable missing: check Bubblewrap and image runtime versions.' \
    'An assertion failure can mean a broken isolation boundary; do not treat it as a pass.' \
    'Exit 124/137 can indicate a timeout. Inspect the original error above; do not automatically enable privileged mode.' >&2
  exit 1
fi
printf '%s\n' \
  'PASS: permission preflight only, for this identity and execution context.' \
  'Next: run the installed Runner HTTP/Office smoke test under its final systemd/container policy.'
