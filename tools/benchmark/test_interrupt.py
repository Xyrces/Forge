"""Exercise cancellation across the coordinator, trial process, and shell descendants."""
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest


@unittest.skipUnless(os.name == 'posix', 'POSIX process groups required')
class InterruptTests(unittest.TestCase):
    def test_interrupt_kills_parallel_trial_descendants_promptly(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            ready = root / 'ready'
            escaped = root / 'escaped'
            grandchild = f"import time,pathlib; time.sleep(1.5); pathlib.Path({str(escaped)!r}).write_text('escaped')"
            child = (f"import subprocess,sys,pathlib,time; subprocess.Popen([sys.executable,'-c',{grandchild!r}]); "
                     f"pathlib.Path({str(ready)!r}).write_text('ready'); time.sleep(30)")
            runner = str(Path(__file__).with_name('run.py'))
            script = f"""
import importlib.util, os, sys
from pathlib import Path
from decimal import Decimal
spec = importlib.util.spec_from_file_location('benchmark', {runner!r})
b = importlib.util.module_from_spec(spec); spec.loader.exec_module(b)
def execute(job):
    return b.run_process([sys.executable, '-c', {child!r}], {temp!r}, dict(os.environ), Path({str(root / 'log')!r}), 30)
try:
    b.execute_waves([('case', {{'id':'fake'}}, 1)], 1, Decimal(0), False,
                    lambda j,a,t:j, execute, lambda j,r:False, lambda j,r:None)
except KeyboardInterrupt:
    sys.exit(130)
"""
            process = subprocess.Popen([sys.executable, '-c', script], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                deadline = time.monotonic() + 5
                while not ready.exists() and time.monotonic() < deadline:
                    time.sleep(.02)
                self.assertTrue(ready.exists(), 'trial never started')
                process.send_signal(signal.SIGINT)
                _, error = process.communicate(timeout=5)
                self.assertEqual(130, process.returncode, error.decode(errors='replace'))
                time.sleep(1.6)
                self.assertFalse(escaped.exists(), 'grandchild survived benchmark cancellation')
            finally:
                if process.poll() is None:
                    process.kill()
                    process.communicate()
