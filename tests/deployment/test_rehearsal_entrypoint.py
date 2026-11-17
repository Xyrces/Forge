import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'deploy/k3s/rehearsal/entrypoint.sh'
SHELL = shutil.which('sh') if os.name != 'nt' else 'C:/Program Files/Git/bin/bash.exe'

class RehearsalEntrypointTests(unittest.TestCase):
    def run_wrapper(self, mode):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for name, body in {'sleep': 'echo SLEEP:$*', 'dotnet': 'echo DOTNET:$*'}.items():
                path = root / name
                path.write_text('#!/bin/sh\n' + body + '\n', encoding='utf-8')
                path.chmod(0o755)
            env = dict(os.environ, PATH=str(root) + os.pathsep + os.environ['PATH'])
            if mode is None:
                env.pop('Controller__Mode', None)
            else:
                env['Controller__Mode'] = mode
            stub_path = str(root).replace('\\', '/')
            if os.name == 'nt':
                stub_path = '/' + stub_path[0].lower() + stub_path[2:]
            return subprocess.run([SHELL, '-c', 'export PATH="' + stub_path + '":$PATH; exec sh "$1" --check', 'rehearsal', str(SCRIPT)], env=env, capture_output=True, text=True, timeout=10)

    def test_initializer_fixed_wait_then_unchanged_binary(self):
        result = self.run_wrapper('initialize')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('SLEEP:60', result.stdout)
        self.assertIn('DOTNET:/app/Forge.Core.dll --check', result.stdout)
        self.assertLess(result.stdout.index('SLEEP:60'), result.stdout.index('DOTNET:'))

    def test_all_non_initializer_modes_fail_without_dotnet_or_delay(self):
        for mode in (None, 'standby', 'active', 'worker'):
            with self.subTest(mode=mode):
                result = self.run_wrapper(mode)
                self.assertEqual(78, result.returncode)
                self.assertNotIn('DOTNET:', result.stdout)
                self.assertNotIn('SLEEP:', result.stdout)
                self.assertIn('INTENTIONALLY_UNAVAILABLE', result.stderr)

if __name__ == '__main__':
    unittest.main()
