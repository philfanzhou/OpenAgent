"""Sandbox entry point. Everything emitted by user code is untrusted data."""
import base64
import json
import os
from pathlib import Path
import signal
import stat
import subprocess
import sys
import threading

LOG_LIMIT = 32768
FILE_LIMIT = 10 * 1024 * 1024
TOTAL_LIMIT = 20 * 1024 * 1024
ALLOWED = {".pptx", ".xlsx", ".png", ".jpg", ".jpeg", ".pdf", ".csv", ".json", ".md", ".txt"}


def drain(pipe, target):
    while True:
        chunk = pipe.read(8192)
        if not chunk:
            break
        target.extend(chunk[:max(0, LOG_LIMIT - len(target))])


def collect_files():
    files = []
    total = 0
    for path in sorted(Path("/output").iterdir()):
        metadata = path.lstat()
        if not stat.S_ISREG(metadata.st_mode):
            raise ValueError("Outputs must be regular files, without directories or symbolic links.")
        name = path.name
        if not name or len(name) > 120 or not name[0].isalnum() or not all(c.isalnum() or c in "._- " for c in name):
            raise ValueError("Invalid output file name.")
        if path.suffix.lower() not in ALLOWED:
            raise ValueError("Unsupported output file extension.")
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


def main():
    for directory in ("/tmp/home", "/tmp/runtime", "/tmp/matplotlib"):
        Path(directory).mkdir(mode=0o700, parents=True, exist_ok=True)
    output, errors = bytearray(), bytearray()
    process = subprocess.Popen(
        [sys.executable, "-I", "-u", "/input/main.py"], cwd="/work",
        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        start_new_session=True,
    )
    readers = [threading.Thread(target=drain, args=(pipe, target), daemon=True)
               for pipe, target in [(process.stdout, output), (process.stderr, errors)]]
    for reader in readers:
        reader.start()
    timed_out = False
    try:
        process.wait(timeout=int(os.environ["EXECUTION_TIMEOUT"]))
    except subprocess.TimeoutExpired:
        timed_out = True
    finally:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait()
    for reader in readers:
        reader.join(timeout=1)
    result = {
        "exitCode": process.returncode, "timedOut": timed_out,
        "stdout": output.decode("utf-8", errors="replace"),
        "stderr": errors.decode("utf-8", errors="replace"), "files": [],
    }
    if timed_out:
        result["stderr"] = "Execution exceeded its deadline."
    elif process.returncode == 0:
        try:
            result["files"] = collect_files()
        except (ValueError, OSError) as error:
            result["exitCode"] = 1
            result["stderr"] = str(error)[:LOG_LIMIT]
    print(json.dumps(result, ensure_ascii=True), flush=True)


if __name__ == "__main__":
    main()
