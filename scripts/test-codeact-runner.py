"""Smoke-test a published Bubblewrap Runner without Docker."""
import base64
import io
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
import zipfile


def request(url, payload=None, key=None):
    data = None if payload is None else json.dumps(payload).encode("utf-8")
    headers = {"Content-Type": "application/json"}
    if key:
        headers["Authorization"] = "Bearer " + key
    try:
        with urlopen(Request(url, data=data, headers=headers), timeout=10) as response:
            return response.status, response.read()
    except HTTPError as error:
        return error.code, error.read()


def unused_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def main():
    runner = os.environ.get("CODEACT_RUNNER_DLL")
    python = os.environ.get("CODEACT_TEST_PYTHON", "/opt/openagent-code/venv/bin/python")
    if not runner or not Path(runner).is_file():
        raise SystemExit("Set CODEACT_RUNNER_DLL to a published OpenAgent.Runner.dll.")

    key = secrets.token_hex(32)
    port = unused_port()
    base_url = f"http://127.0.0.1:{port}"
    with tempfile.TemporaryDirectory(prefix="codeact-smoke-") as directory, tempfile.TemporaryFile() as log:
        environment = dict(
            os.environ,
            ASPNETCORE_URLS=base_url,
            Runner__ApiKey=key,
            Runner__WorkspaceRoot=directory,
            Runner__BubblewrapPath=os.environ.get("CODEACT_TEST_BWRAP", "/usr/bin/bwrap"),
            Runner__PythonPath=python,
        )
        process = subprocess.Popen(["dotnet", runner], env=environment, stdout=log, stderr=log)
        try:
            for _ in range(60):
                try:
                    status, _ = request(base_url + "/health")
                    if status == 200:
                        break
                except URLError:
                    pass
                if process.poll() is not None:
                    raise AssertionError("Runner exited before becoming ready.")
                time.sleep(0.5)
            else:
                raise AssertionError("Runner did not become ready.")

            status, _ = request(base_url + "/v1/execute", {"code": "print(42)"})
            assert status == 401, status
            code = """
import os
from openpyxl import Workbook
from pptx import Presentation
assert os.getuid() == 65532
assert 'Runner__ApiKey' not in os.environ
assert not os.path.exists('/var/run/docker.sock')
w = Workbook()
w.active['A1'] = 42
w.save('/output/report.xlsx')
p = Presentation()
p.slides.add_slide(p.slide_layouts[0]).shapes.title.text = 'Isolated CodeAct'
p.save('/output/report.pptx')
print('isolated execution passed')
"""
            status, body = request(base_url + "/v1/execute", {"code": code}, key)
            assert status == 200, body.decode("utf-8", errors="replace")
            response = json.loads(body)
            assert response["exitCode"] == 0, response["stderr"]
            assert {file["name"] for file in response["files"]} == {"report.xlsx", "report.pptx"}
            for artifact in response["files"]:
                with zipfile.ZipFile(io.BytesIO(base64.b64decode(artifact["content"]))) as archive:
                    assert archive.testzip() is None
            assert not list(Path(directory).iterdir()), "Task input directories were not removed."
            print("PASS: Runner authentication, Bubblewrap isolation, PPT/XLSX artifacts, and cleanup.")
        finally:
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
            if process.returncode not in (0, -15):
                log.seek(0)
                print(log.read().decode("utf-8", errors="replace"))


if __name__ == "__main__":
    main()
