#!/bin/sh
# Offline, fixed-code diagnostic using only Bubblewrap 0.4.0-era options.
# This is not the PR80 production policy and must not execute untrusted input.
set -eu

if [ "${1:-}" = --help ]; then
  printf '%s\n' 'Usage: sh test-bubblewrap-basic.sh' \
    'Run as the intended non-root user on Linux. No installs or policy changes.' \
    'Tests shell execution, optional system Python, file round trip and basic isolation.' \
    'Exit: 0=basic tests passed (check Python PASS/SKIP), 1=execution failed, 2=missing tool, 3=unsupported context.'
  exit 0
fi
[ "$#" -eq 0 ] || { printf 'Use --help for usage.\n' >&2; exit 2; }
[ "$(uname -s)" = Linux ] || { printf 'UNSUPPORTED: Linux required.\n' >&2; exit 3; }
[ "$(id -u)" -ne 0 ] || { printf 'UNSUPPORTED: run as the intended non-root user, without sudo.\n' >&2; exit 3; }
for tool in bwrap timeout mktemp mkdir rm cat readlink env; do
  command -v "$tool" >/dev/null 2>&1 || { printf 'MISSING: %s; execution remains untested.\n' "$tool" >&2; exit 2; }
done
printf 'Context: kernel=%s arch=%s uid=%s\n' "$(uname -r)" "$(uname -m)" "$(id -u)"
bwrap --version
bwrap_bin=$(command -v bwrap)
printf '%s\n' 'INFO: basic compatibility probe; does not require --disable-userns, --clearenv, --size or --perms.'

umask 077
probe_dir=$(mktemp -d /tmp/bwrap-basic.XXXXXXXX)
trap 'rm -rf -- "$probe_dir"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
mkdir "$probe_dir/input" "$probe_dir/output"
printf '21\n' > "$probe_dir/input/number.txt"
printf 'host-only\n' > "$probe_dir/host-marker"

# /usr and the distro library directories supply shell/Python dependencies.
# Avoid mounting the host home, /etc or its service credentials.
set -- --unshare-user --unshare-pid --unshare-ipc --unshare-net --unshare-uts \
  --new-session --die-with-parent
for runtime in /usr /bin /sbin /lib /lib64; do
  if [ -d "$runtime" ]; then
    set -- "$@" --ro-bind "$runtime" "$runtime"
  fi
done
if [ -f /etc/ld.so.cache ]; then
  set -- "$@" --ro-bind /etc/ld.so.cache /etc/ld.so.cache
fi
set -- "$@" --ro-bind "$probe_dir/input" /input \
  --bind "$probe_dir/output" /output --tmpfs /tmp \
  --proc /proc --dev /dev --chdir /output --remount-ro /

# Clean the environment before launching bwrap: --clearenv is not in 0.4.0.
set +e
env -i PATH=/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin LANG=C \
  timeout --kill-after=5s 30s "$bwrap_bin" "$@" -- /bin/sh -ec '
    echo "CHECK: shell and process isolation"
    test "$(id -u)" -ne 0
    test "$(readlink /proc/self/ns/net)" != "$2"
    test "$(readlink /proc/self/ns/pid)" != "$3"
    test "$(readlink /proc/self/ns/user)" != "$4"
    caps=0
    nnp=0
    while read -r field value rest; do
      case "$field" in
        CapEff:|CapPrm:)
          case "$value" in ""|*[!0]*) exit 1 ;; esac
          caps=$((caps + 1)) ;;
        NoNewPrivs:) test "$value" = 1; nnp=1 ;;
      esac
    done < /proc/self/status
    test "$caps" = 2
    test "$nnp" = 1
    echo "CHECK: read-only input/runtime, hidden host marker, writable output"
    test ! -e "$1"
    if (printf changed > /input/number.txt) 2>/dev/null; then exit 1; fi
    if (printf changed > /usr/bwrap-write-test) 2>/dev/null; then exit 1; fi
    if (printf changed > /root-write-test) 2>/dev/null; then exit 1; fi
    value=$(cat /input/number.txt)
    test "$value" = 21
    printf "%s\n" "$((value * 2))" > /output/shell-result.txt
    echo "PASS: shell calculated 21 * 2 = 42 inside Bubblewrap."
    python_bin=
    for candidate in /usr/bin/python3 /usr/local/bin/python3 /usr/libexec/platform-python; do
      if [ -x "$candidate" ]; then python_bin=$candidate; break; fi
    done
    if [ -n "$python_bin" ]; then
      "$python_bin" -I -B -c "from pathlib import Path; n=int(Path(\"/input/number.txt\").read_text()); Path(\"/output/python-result.txt\").write_text(str(n*2)); print(\"PASS: Python calculated 21 * 2 = 42 inside Bubblewrap.\")"
    else
      echo "SKIP: no system Python found; only shell execution was tested."
    fi
  ' probe "$probe_dir/host-marker" \
    "$(readlink /proc/self/ns/net)" "$(readlink /proc/self/ns/pid)" \
    "$(readlink /proc/self/ns/user)" > "$probe_dir/probe.log" 2>&1
probe_status=$?
set -e
cat "$probe_dir/probe.log"
if [ "$probe_status" -ne 0 ]; then
  printf 'FAIL: sandbox/code exited %s. Read the original error above.\n' "$probe_status" >&2
  printf '%s\n' 'Permission denied / Operation not permitted: check namespaces, SELinux/AppArmor and container policy.' \
    'Unknown option / missing interpreter or library: dependency incompatibility, not proof of permission denial.' \
    'No host-code fallback or permission changes were performed.' >&2
  exit 1
fi
if [ "$(cat "$probe_dir/output/shell-result.txt")" != 42 ]; then
  printf 'FAIL: shell output round trip.\n' >&2; exit 1
fi
if [ -f "$probe_dir/output/python-result.txt" ] && [ "$(cat "$probe_dir/output/python-result.txt")" != 42 ]; then
  printf 'FAIL: Python output round trip.\n' >&2; exit 1
fi
printf '%s\n' 'PASS: basic Bubblewrap execution and file round trip in this user/context.' \
  'LIMIT: not a production sandbox acceptance test; nested userns blocking, resource quotas, Office and the full PR80 Runner remain unverified.'
