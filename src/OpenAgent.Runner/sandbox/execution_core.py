"""Shared execution core for the one-shot wrapper and the session supervisor.

Everything emitted by user code is untrusted data; helpers here keep the
validation, limits and teardown semantics identical for both modes.
"""
import base64
import json
import os
import signal
import stat
import subprocess
import sys
import threading
import time
from pathlib import Path

LOG_LIMIT = 32768
FILE_LIMIT = 10 * 1024 * 1024
TOTAL_LIMIT = 20 * 1024 * 1024
REQUEST_LIMIT = 33 * 1024 * 1024


def drain(pipe, target):
    while True:
        chunk = pipe.read(8192)
        if not chunk:
            break
        target.extend(chunk[:max(0, LOG_LIMIT - len(target))])


def snapshot_output():
    """Deterministic pre-run snapshot of /output entry identities."""
    entries = {}
    for path in Path("/output").iterdir():
        try:
            metadata = path.lstat()
        except OSError:
            continue
        entries[path.name] = (metadata.st_ino, metadata.st_mtime_ns, metadata.st_size)
    return entries


def collect_files(changed_since=None):
    """Return validated /output files, optionally only entries that are new or
    changed relative to a `snapshot_output()` result (no clock assumptions)."""
    files = []
    total = 0
    for path in sorted(Path("/output").iterdir()):
        metadata = path.lstat()
        if not stat.S_ISREG(metadata.st_mode):
            raise ValueError("Outputs must be regular files, without directories or symbolic links.")
        if changed_since is not None and changed_since.get(path.name) == (
                metadata.st_ino, metadata.st_mtime_ns, metadata.st_size):
            continue
        name = path.name
        if not name or len(name) > 120 or not name[0].isalnum() or not all(c.isalnum() or c in "._- " for c in name):
            raise ValueError("Invalid output file name.")
        # 输出类型不在沙箱侧过滤：可入库与否由存储层（媒体类型目录/白名单）裁决并跳过报告。
        if metadata.st_size == 0 or metadata.st_size > FILE_LIMIT or len(files) >= 8:
            raise ValueError("Output file count or size exceeds the limit.")
        descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
        with os.fdopen(descriptor, "rb") as stream:
            if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
                raise ValueError("Output is not a regular file.")
            content = stream.read(FILE_LIMIT + 1)
        total += len(content)
        if len(content) > FILE_LIMIT or total > TOTAL_LIMIT:
            raise ValueError("Output files exceed the size limit.")
        files.append({"name": name, "content": base64.b64encode(content).decode("ascii")})
    return files


def default_entry(language):
    return "main.sh" if language == "shell" else "main.mjs" if language == "javascript" else "main.py"


def entry_command(language, entry, node_path="/usr/bin/node"):
    entry = entry or default_entry(language)
    if language == "javascript":
        return [node_path, f"/input/{entry}"]
    if language == "shell":
        return ["/bin/bash", f"/input/{entry}"]
    return [sys.executable, "-I", "-u", f"/input/{entry}"]


def write_input(name, content):
    """Write one request input into the persistent /input tree, rejecting traversal."""
    parts = name.split("/") if name else []
    if not parts or name.startswith("/") or any(part in ("", ".", "..") for part in parts):
        raise ValueError("Invalid input file name.")
    target = Path("/input").joinpath(*parts)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(content)


def run_child(command, env=None, timeout=120, preexec=None, disconnected=None):
    """Run the user entry in /work and kill its whole process group when done.

    Returns the wire result dictionary. `disconnected` is an optional callable
    polled while waiting; when it turns true the child group is killed early and
    the result is still produced, so the sandbox survives cancelled requests.
    """
    for directory in ("/tmp/home", "/tmp/runtime", "/tmp/matplotlib"):
        Path(directory).mkdir(mode=0o700, parents=True, exist_ok=True)
    process = subprocess.Popen(
        command, cwd="/work", env=env,
        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        start_new_session=True, preexec_fn=preexec,
    )
    output, errors = bytearray(), bytearray()
    readers = [threading.Thread(target=drain, args=(pipe, target), daemon=True)
               for pipe, target in [(process.stdout, output), (process.stderr, errors)]]
    for reader in readers:
        reader.start()
    timed_out = False
    deadline = time.monotonic() + timeout
    while True:
        try:
            process.wait(timeout=0.25)
            break
        except subprocess.TimeoutExpired:
            if disconnected is not None and disconnected():
                break
            if time.monotonic() >= deadline:
                timed_out = True
                break
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    process.wait()
    for reader in readers:
        reader.join(timeout=1)
    return {
        "exitCode": process.returncode, "timedOut": timed_out,
        "stdout": output.decode("utf-8", errors="replace"),
        "stderr": errors.decode("utf-8", errors="replace"), "files": [],
    }


def result_json(result):
    return json.dumps(result, ensure_ascii=True)
