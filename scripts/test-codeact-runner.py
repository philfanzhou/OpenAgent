"""Smoke-test a published or already running GVisor Runner."""
import base64
import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack
import io
import json
import os
from pathlib import Path
import secrets
import shutil
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
        with urlopen(Request(url, data=data, headers=headers), timeout=150) as response:
            return response.status, response.read()
    except HTTPError as error:
        return error.code, error.read()


def unused_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--environment-file', type=Path, help='Test an already running service using its environment file.')
    parser.add_argument('--endpoint', help='Test an already running service at this URL.')
    parser.add_argument('--key', help='Runner API key used with --endpoint.')
    arguments = parser.parse_args()
    runner = os.environ.get("CODEACT_RUNNER_DLL")
    image = os.environ.get("CODEACT_TEST_GVISOR_IMAGE", "openagent-codeact:local")
    docker_path = os.environ.get("CODEACT_TEST_DOCKER", shutil.which("docker") or "/usr/bin/docker")
    remote = bool(arguments.environment_file or arguments.endpoint)
    if not remote and (not runner or not Path(runner).is_file()):
        raise SystemExit("Set CODEACT_RUNNER_DLL to a published OpenAgent.Runner.dll.")
    if arguments.environment_file and arguments.endpoint:
        raise SystemExit("Use only one of --environment-file and --endpoint.")

    with ExitStack() as stack:
        log = stack.enter_context(tempfile.TemporaryFile())
        process = None
        if arguments.environment_file:
            environment = dict(line.split('=', 1) for line in arguments.environment_file.read_text().splitlines()
                               if line and not line.startswith('#'))
            base_url = environment['ASPNETCORE_URLS'].rstrip('/')
            key = environment['Runner__ApiKey']
            directory = environment['Runner__WorkspaceRoot']
        elif arguments.endpoint:
            base_url = arguments.endpoint.rstrip('/')
            key = arguments.key or os.environ.get("CODEACT_RUNNER_KEY", "")
            if not key:
                raise SystemExit("Set --key or CODEACT_RUNNER_KEY with --endpoint.")
            directory = None
        else:
            directory = stack.enter_context(tempfile.TemporaryDirectory(prefix="codeact-smoke-"))
            key = secrets.token_hex(32)
            base_url = f"http://127.0.0.1:{unused_port()}"
            environment = dict(
                os.environ,
                ASPNETCORE_URLS=base_url,
                Runner__ApiKey=key,
                Runner__WorkspaceRoot=directory,
                Runner__DockerPath=docker_path,
                Runner__DockerHost=os.environ.get("CODEACT_TEST_DOCKER_HOST", ""),
                Runner__Runtime="runsc",
                Runner__SandboxImage=image,
                Runner__SandboxPythonPath=os.environ.get("CODEACT_TEST_SANDBOX_PYTHON", "/opt/openagent-code/venv/bin/python"),
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
                if process is not None and process.poll() is not None:
                    raise AssertionError("Runner exited before becoming ready.")
                time.sleep(0.5)
            else:
                raise AssertionError("Runner did not become ready.")

            status, _ = request(base_url + "/v1/execute", {"code": "print(42)"})
            assert status == 401, status
            code = """
import os
from pathlib import Path
from openpyxl import Workbook, load_workbook
assert os.getuid() == 65532
status = dict(line.split(':', 1) for line in Path('/proc/self/status').read_text().splitlines())
assert int(status['CapEff'], 16) == 0
assert int(status['CapPrm'], 16) == 0
assert int(status['NoNewPrivs']) == 1
assert 'Runner__ApiKey' not in os.environ
w = Workbook()
sheet = w.active
sheet.append(['地区', '数量'])
sheet.append(['华东', 42])
sheet.append(['华南', 17])
sheet.append(['华北', 29])
w.save('/output/report.xlsx')
rows = list(load_workbook('/output/report.xlsx', read_only=True).active.iter_rows(values_only=True))
assert rows == [('地区', '数量'), ('华东', 42), ('华南', 17), ('华北', 29)]
print('three-row Excel verified')
"""
            status, body = request(base_url + "/v1/execute", {"code": code}, key)
            assert status == 200, body.decode("utf-8", errors="replace")
            excel_response = json.loads(body)
            assert excel_response["exitCode"] == 0, excel_response["stderr"]
            excel = next(file for file in excel_response["files"] if file["name"] == "report.xlsx")
            with zipfile.ZipFile(io.BytesIO(base64.b64decode(excel["content"]))) as archive:
                assert archive.testzip() is None
            second_code = """
from openpyxl import load_workbook
from pptx import Presentation
rows = list(load_workbook('/input/report.xlsx', read_only=True).active.iter_rows(values_only=True))
assert rows == [('地区', '数量'), ('华东', 42), ('华南', 17), ('华北', 29)]
presentation = Presentation()
slide = presentation.slides.add_slide(presentation.slide_layouts[1])
slide.shapes.title.text = '销售汇报'
slide.placeholders[1].text = '\\n'.join(f'{region}：{quantity}' for region, quantity in rows[1:])
presentation.save('/output/report.pptx')
verified = Presentation('/output/report.pptx')
assert '华南：17' in verified.slides[0].placeholders[1].text
print('Excel upload to editable PPT verified')
"""
            status, body = request(base_url + "/v1/execute", {
                "code": second_code,
                "files": [{"name": "report.xlsx", "content": excel["content"]}],
            }, key)
            assert status == 200, body.decode("utf-8", errors="replace")
            ppt_response = json.loads(body)
            assert ppt_response["exitCode"] == 0, ppt_response["stderr"]
            ppt = next(file for file in ppt_response["files"] if file["name"] == "report.pptx")
            with zipfile.ZipFile(io.BytesIO(base64.b64decode(ppt["content"]))) as archive:
                assert archive.testzip() is None
            if directory is not None:
                assert not list(Path(directory).iterdir()), "Task input directories were not removed."

            def run_concurrent(label):
                concurrent_code = f"""
from pathlib import Path
import time
Path('/work/shared-name.txt').write_text('{label}')
time.sleep(1)
assert Path('/work/shared-name.txt').read_text() == '{label}'
Path('/output/{label}.txt').write_text('{label}')
"""
                return request(base_url + "/v1/execute", {"code": concurrent_code}, key)

            with ThreadPoolExecutor(max_workers=2) as pool:
                concurrent_results = list(pool.map(run_concurrent, ["alpha", "beta"]))
            for label, (concurrent_status, concurrent_body) in zip(["alpha", "beta"], concurrent_results):
                assert concurrent_status == 200, concurrent_body.decode("utf-8", errors="replace")
                concurrent_response = json.loads(concurrent_body)
                assert concurrent_response["exitCode"] == 0, concurrent_response["stderr"]
                artifact = next(file for file in concurrent_response["files"] if file["name"] == label + ".txt")
                assert base64.b64decode(artifact["content"]).decode() == label
            print("PASS: Runner authentication, gVisor isolation, concurrent workspaces, three-row Excel, and Excel-upload-to-PPT artifacts.")
        except BaseException:
            if process is not None:
                log.seek(0)
                print(log.read().decode("utf-8", errors="replace"))
            raise
        finally:
            if process is not None:
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)


if __name__ == "__main__":
    main()
