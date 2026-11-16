"""Exercise deployment command selection without starting Docker containers."""
import json
import os
import shutil
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class DeploymentScriptsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.tmp = Path(self.directory.name)
        self.log = self.tmp / "calls.jsonl"
        docker = self.tmp / "docker"
        docker.write_text('''#!/usr/bin/env python3
import json, os, sys
with open(os.environ['DOCKER_TEST_LOG'], 'a') as log:
    log.write(json.dumps(sys.argv[1:]) + '\\n')
if sys.argv[1:3] == ['image', 'inspect'] and sys.argv[3] == os.environ.get('MISSING_IMAGE'):
    sys.exit(1)
''')
        docker.chmod(0o755)
        self.env = {key: value for key, value in os.environ.items()
                    if not key.startswith('OPENAGENT_')}
        self.env.update(PATH=str(self.tmp) + os.pathsep + os.environ['PATH'],
                        DOCKER_TEST_LOG=str(self.log))

    def run_script(self, name, *args):
        result = subprocess.run(['bash', str(ROOT / 'scripts' / name), *args],
                                env=self.env, cwd=self.tmp, capture_output=True, text=True)
        calls = [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []
        return result, calls

    def test_build_exports_all_images(self):
        result, calls = self.run_script('build-images.sh', '--docker-mode', 'docker',
                                       '--tar-dir', str(self.tmp / 'image exports'))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len([call for call in calls if call[0] == 'build']), 3)
        self.assertEqual({call[-1] for call in calls if call[0] == 'save'},
                         {'openagent-' + name + ':latest' for name in ('engine', 'router', 'chat')})

    def engine_build(self, calls):
        matches = [call for call in calls
                   if call[0] == 'build' and any('OpenAgent.Engine.Host' in arg for arg in call)]
        self.assertEqual(len(matches), 1, calls)
        return matches[0]

    def build_arg(self, call, name):
        for index, arg in enumerate(call[:-1]):
            if arg == '--build-arg' and call[index + 1].startswith(name + '='):
                return call[index + 1][len(name) + 1:]
        self.fail(f'{name} build arg missing from {call}')

    def test_engine_build_pins_opencode_version(self):
        version = (ROOT / 'Backend' / 'opencode.version').read_text(encoding='utf-8').strip()
        self.assertRegex(version, r'^\d+\.\d+\.\d+$')
        result, calls = self.run_script('build-images.sh', '--docker-mode', 'docker')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.build_arg(self.engine_build(calls), 'OPENCODE_VERSION'), version)
        # 版本参数只属于 Engine 镜像，其他镜像不携带。
        engine = self.engine_build(calls)
        for call in calls:
            if call[0] == 'build' and call != engine:
                self.assertFalse(any('OPENCODE_VERSION' in arg for arg in call), call)

    def test_engine_build_accepts_opencode_version_override(self):
        # 升级前临时验证通道：环境变量覆盖版本文件，latest 显式放行。
        self.env['OPENAGENT_OPENCODE_VERSION'] = 'latest'
        result, calls = self.run_script('build-images.sh', '--docker-mode', 'docker')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.build_arg(self.engine_build(calls), 'OPENCODE_VERSION'), 'latest')

    def test_engine_build_rejects_invalid_opencode_version(self):
        self.env['OPENAGENT_OPENCODE_VERSION'] = '1.x-beta'
        result, calls = self.run_script('build-images.sh', '--docker-mode', 'docker')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('invalid OpenCode version', result.stderr)
        self.assertEqual(calls, [])

    def prepare_preview(self):
        preview = self.tmp / '.preview'
        preview.mkdir()
        (preview / 'allocations').write_text('test\t5210\t5010\t8090\t0\n')
        docker = self.tmp / 'docker'
        docker.write_text(docker.read_text() + "\nif sys.argv[1] == 'port': print('0.0.0.0:5432')\n")
        curl = self.tmp / 'curl'
        curl.write_text('#!/usr/bin/env bash\nexit 0\n')
        curl.chmod(0o755)
        # preview.sh targets WSL/GNU grep; adapt its tab-only PCRE usage on macOS test hosts.
        grep_executable = shutil.which('grep')
        self.assertIsNotNone(grep_executable)
        grep = self.tmp / 'grep'
        grep.write_text(f"""#!/usr/bin/env python3
import os, sys
args = [arg.replace('-P', '-E').replace(r'\\t', '\t') for arg in sys.argv[1:]]
os.execv({grep_executable!r}, ['grep', *args])
""")
        grep.chmod(0o755)

    def test_preview_build_pins_version_before_starting_without_rebuild(self):
        self.prepare_preview()
        version = (ROOT / 'Backend' / 'opencode.version').read_text().strip()
        for override in (None, '1.19.3', 'latest'):
            with self.subTest(override=override):
                if override is None:
                    self.env.pop('OPENAGENT_OPENCODE_VERSION', None)
                else:
                    self.env['OPENAGENT_OPENCODE_VERSION'] = override
                self.log.unlink(missing_ok=True)
                result, calls = self.run_script('preview.sh', 'up', 'test')
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn("复用现有槽位", result.stdout)
                self.assertEqual((self.tmp / ".preview" / "allocations").read_text().splitlines(),
                                 ["test\t5210\t5010\t8090\t0"])
                builds = [call for call in calls if 'build' in call]
                self.assertEqual(len(builds), 1, calls)
                self.assertEqual(self.build_arg(builds[0], 'OPENCODE_VERSION'), override or version)
                starts = [call for call in calls if 'up' in call]
                self.assertEqual(len(starts), 1, calls)
                self.assertIn('--no-build', starts[0])
                self.assertNotIn('--build', starts[0])
                self.assertLess(calls.index(builds[0]), calls.index(starts[0]))

    def test_preview_rejects_invalid_version_before_docker_or_allocation(self):
        self.env['OPENAGENT_OPENCODE_VERSION'] = '1.x-beta'
        result, calls = self.run_script('preview.sh', 'up', 'test')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('invalid OpenCode version', result.stderr)
        self.assertEqual(calls, [])
        self.assertFalse((self.tmp / '.preview').exists())

    def test_default_deployment_checks_images_and_starts_compose(self):
        result, calls = self.run_script('deploy.sh')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len([call for call in calls if call[:2] == ['image', 'inspect']]), 3)
        self.assertEqual(calls[-1].count('--file'), 1)
        self.assertEqual(calls[-1][-5:], ['up', '--detach', '--no-build', '--pull', 'never'])

    def test_deploy_validates_compose_before_start(self):
        result, calls = self.run_script('deploy.sh')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(['image', 'inspect', 'openagent-engine:latest'], calls)
        self.assertEqual(calls[-1].count('--file'), 1)
        self.assertEqual(calls[-2][-2:], ['config', '--quiet'])

    def test_missing_image_never_starts_compose(self):
        self.env.update(MISSING_IMAGE='openagent-engine:latest')
        result, calls = self.run_script('deploy.sh')
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any(call[0] == 'compose' for call in calls))

    def test_deploy_accepts_auto_docker_mode(self):
        result, calls = self.run_script('deploy.sh', '--docker-mode', 'auto')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(any(call[:2] == ['image', 'inspect'] for call in calls))

    def test_deploy_reads_docker_mode_from_env_file(self):
        env_file = self.tmp / 'deployment.env'
        env_file.write_text('OPENAGENT_DOCKER_MODE=auto\n')
        result, calls = self.run_script('deploy.sh', '--env-file', str(env_file))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(any(call[:2] == ['image', 'inspect'] for call in calls))

    def test_deploy_rejects_unknown_docker_mode(self):
        result, calls = self.run_script('deploy.sh', '--docker-mode', 'bogus')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('unsupported docker mode', result.stderr)
        self.assertEqual(calls, [])


if __name__ == '__main__':
    unittest.main()
