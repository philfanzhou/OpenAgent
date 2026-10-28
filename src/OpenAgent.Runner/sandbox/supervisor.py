"""Long-lived session sandbox supervisor.

Serves one execution request per Unix-socket connection on /channel/supervisor.sock.
Files under /work, /tmp, /input and /output persist across requests; each request
still runs in its own process group that is fully killed when the request ends.
Exits (and thereby tears down the whole PID namespace) once MAX_IDLE elapses
without a request; the host-side reaper is the authoritative owner of that.
"""
import base64
import json
import os
import resource
import socket
import sys
import threading
import time
from pathlib import Path

# The entry runs with python -I, which drops the script directory from sys.path.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import execution_core as core

CHANNEL = Path("/channel")
SOCKET_PATH = CHANNEL / "supervisor.sock"
TIMEOUT_SECONDS = int(os.environ.get("SANDBOX_TIMEOUT", "120"))
MAX_IDLE_SECONDS = int(os.environ.get("SANDBOX_MAX_IDLE_SECONDS", "7200"))


def limits_preexec():
    """Per-request resource limits, equivalent to the one-shot prlimit wrapper."""
    address_space = int(os.environ["SANDBOX_AS_BYTES"])
    nproc = int(os.environ["SANDBOX_NPROC"])
    nofile = int(os.environ["SANDBOX_NOFILE"])
    fsize = int(os.environ["SANDBOX_FSIZE"])
    cpu = TIMEOUT_SECONDS + 5

    def apply():
        resource.setrlimit(resource.RLIMIT_AS, (address_space, address_space))
        resource.setrlimit(resource.RLIMIT_CPU, (cpu, cpu))
        resource.setrlimit(resource.RLIMIT_NPROC, (nproc, nproc))
        resource.setrlimit(resource.RLIMIT_NOFILE, (nofile, nofile))
        resource.setrlimit(resource.RLIMIT_FSIZE, (fsize, fsize))
        resource.setrlimit(resource.RLIMIT_CORE, (0, 0))

    return apply


def read_request(connection):
    data = bytearray()
    while b"\n" not in data:
        chunk = connection.recv(65536)
        if not chunk:
            return None
        data.extend(chunk)
        if len(data) > core.REQUEST_LIMIT:
            raise ValueError("Request exceeds the wire limit.")
    return json.loads(bytes(data.split(b"\n", 1)[0]))


def disconnect_watcher(connection):
    """Flags the peer going away so a running request can be killed early."""
    state = {"gone": False}

    def watch():
        try:
            while True:
                if not connection.recv(4096):
                    break
        except OSError:
            pass
        state["gone"] = True

    threading.Thread(target=watch, daemon=True).start()
    return state


def handle(connection):
    try:
        request = read_request(connection)
        if request is None:
            return
        language = str(request.get("language") or "python")
        entry = str(request["entry"]) if request.get("entry") else core.default_entry(language)
        core.write_input(entry, str(request.get("code") or "").encode("utf-8"))
        for item in request.get("files") or []:
            core.write_input(str(item["name"]), base64.b64decode(item["content"]))
    except (KeyError, ValueError, TypeError, json.JSONDecodeError) as error:
        respond(connection, {"exitCode": 1, "timedOut": False, "stdout": "",
                             "stderr": f"Invalid request: {error}"[:core.LOG_LIMIT], "files": []})
        return

    command = core.entry_command(language, entry, os.environ.get("EXECUTION_NODE", "/usr/bin/node"))
    env = dict(os.environ)
    env["EXECUTION_LANGUAGE"] = language
    env["EXECUTION_ENTRY"] = entry
    env["EXECUTION_TIMEOUT"] = str(TIMEOUT_SECONDS)
    peer = disconnect_watcher(connection)
    before = core.snapshot_output()
    result = core.run_child(command, env=env, timeout=TIMEOUT_SECONDS,
                            preexec=limits_preexec(), disconnected=lambda: peer["gone"])
    if peer["gone"]:
        return
    if result["timedOut"]:
        result["stderr"] = "Execution exceeded its deadline."
    elif result["exitCode"] == 0:
        try:
            result["files"] = core.collect_files(changed_since=before)
        except (ValueError, OSError) as error:
            result["exitCode"] = 1
            result["stderr"] = str(error)[:core.LOG_LIMIT]
    respond(connection, result)


def respond(connection, result):
    try:
        connection.sendall(core.result_json(result).encode("utf-8") + b"\n")
    except OSError:
        pass


def main():
    os.umask(0o077)
    CHANNEL.mkdir(mode=0o700, parents=True, exist_ok=True)
    if SOCKET_PATH.exists():
        SOCKET_PATH.unlink()
    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    server.bind(str(SOCKET_PATH))
    server.listen(4)
    last_used = time.time()
    try:
        while True:
            idle_left = MAX_IDLE_SECONDS - (time.time() - last_used)
            if idle_left <= 0:
                break
            server.settimeout(max(idle_left, 0.05))
            try:
                connection, _ = server.accept()
            except socket.timeout:
                break
            except ConnectionError:
                continue  # A transient aborted connection must not kill the sandbox.
            except OSError:
                break
            last_used = time.time()
            try:
                handle(connection)
            except Exception:
                pass
            finally:
                try:
                    connection.close()
                except OSError:
                    pass
    finally:
        try:
            server.close()
        except OSError:
            pass
        SOCKET_PATH.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
