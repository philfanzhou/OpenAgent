"""Exercise deployment command selection without starting Docker containers."""
import json
import os
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

    def test_build_exports_all_four_images(self):
        """With a combined workspace (Frontend/OpenAgent.Chat present), all four
        images are built and exported."""
        result, calls = self.run_script('build-images.sh', '--docker-mode', 'docker',
                                       '--tar-dir', str(self.tmp / 'image exports'))
        self.assertEqual(result.returncode, 0, result.stderr)
        # The chat workbench lives in the OpenAgent-Frontend repository, so this
        # backend checkout has no Frontend/OpenAgent.Chat and the script skips
        # that image with a warning. Backend images must still all be built and
        # exported; only the chat tarball may be absent.
        built = {call[call.index('--tag') + 1] for call in calls if call[0] == 'build'}
        self.assertIn('openagent-engine:latest', built)
        self.assertIn('openagent-router:latest', built)
        self.assertIn('openagent-runner:latest', built)
        self.assertIn(len(built), (3, 4))  # 4 only when Frontend/OpenAgent.Chat exists

        saved = {call[-1] for call in calls if call[0] == 'save'}
        self.assertIn('openagent-engine:latest', saved)
        self.assertIn('openagent-router:latest', saved)
        self.assertIn('openagent-runner:latest', saved)

    def test_build_skips_missing_frontend_without_failing(self):
        """A backend-only checkout must not fail because the chat sources live
        in a separate repository."""
        has_frontend = (ROOT / 'Frontend' / 'OpenAgent.Chat' / 'Dockerfile').is_file()
        if has_frontend:
            self.skipTest('combined workspace has the frontend sources')

        result, calls = self.run_script('build-images.sh', '--docker-mode', 'docker')
        self.assertEqual(result.returncode, 0, result.stderr)
        # Either the image is already present (reused) or it is absent (warning
        # pointing at the other repository); both are non-fatal.
        output = result.stderr + result.stdout
        self.assertTrue('reusing existing image' in output or 'OpenAgent-Frontend' in output, output)
        built = [call for call in calls if call[0] == 'build']
        self.assertEqual(len(built), 3)

    def test_build_uses_repository_root_dockerfiles(self):
        """The repository was flattened from Backend/src to src; stale paths
        made the first docker build fail with 'path not found'."""
        result, calls = self.run_script('build-images.sh', '--docker-mode', 'docker')
        self.assertEqual(result.returncode, 0, result.stderr)
        dockerfiles = [call[call.index('--file') + 1] for call in calls
                       if call[0] == 'build' and '--file' in call]
        self.assertTrue(dockerfiles)
        for dockerfile in dockerfiles:
            self.assertNotIn('Backend/', dockerfile)
            self.assertTrue(Path(dockerfile).is_file(), dockerfile)

    def test_default_deployment_includes_runner(self):
        self.env['OPENAGENT_RUNNER_API_KEY'] = 'x' * 32
        result, calls = self.run_script('deploy.sh')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len([call for call in calls if call[:2] == ['image', 'inspect']]), 4)
        self.assertEqual(calls[-1].count('--file'), 1)
        self.assertEqual(calls[-1][-5:], ['up', '--detach', '--no-build', '--pull', 'never'])

    def test_runner_requires_key_before_docker(self):
        result, calls = self.run_script('deploy.sh')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Runner API key', result.stderr)
        self.assertEqual(calls, [])

    def test_runner_checks_image_and_validates_compose(self):
        self.env.update(OPENAGENT_RUNNER_API_KEY='x' * 32)
        result, calls = self.run_script('deploy.sh')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(['image', 'inspect', 'openagent-runner:latest'], calls)
        self.assertEqual(calls[-1].count('--file'), 1)
        self.assertEqual(calls[-2][-2:], ['config', '--quiet'])

    def test_missing_runner_never_starts_compose(self):
        self.env.update(OPENAGENT_RUNNER_API_KEY='x' * 32,
                        MISSING_IMAGE='openagent-runner:latest')
        result, calls = self.run_script('deploy.sh')
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any(call[0] == 'compose' for call in calls))


if __name__ == '__main__':
    unittest.main()
