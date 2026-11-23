import base64
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid


example = Path(__file__).resolve().parents[2] / "docs/examples/prepare-first-install.py"
spec = importlib.util.spec_from_file_location("quickstart_inputs", example)
inputs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(inputs)


class FirstInstallExampleTests(unittest.TestCase):
    def prepare(self, directory, enable_models):
        args = [str(example), str(directory)] + (["--enable-models"] if enable_models else [])
        answers = ["example.invalid", 'Sample;"Forge"', "package-reader"]
        with (patch.object(inputs.sys, "argv", args), patch.object(inputs.sys.stdin, "isatty", return_value=True),
              patch("builtins.input", side_effect=answers),
              patch.object(inputs.getpass, "getpass", side_effect=["fixture-token", "fixture-key"]) as prompts):
            old_mask = os.umask(0o077)
            try:
                inputs.main()
            finally:
                os.umask(old_mask)
            self.assertEqual(prompts.call_count, 2 if enable_models else 1)

    def test_model_access_requires_opt_in_and_inputs_match_the_installer(self):
        with tempfile.TemporaryDirectory() as tmp:
            for enabled in (False, True):
                with self.subTest(enabled=enabled):
                    directory = Path(tmp) / str(enabled)
                    self.prepare(directory, enabled)
                    config = json.loads((directory / "appsettings.json").read_text())
                    self.assertIs(config["Budget"]["PaidCallsEnabled"], enabled)
                    provider = config["Llm"]["Providers"][0]
                    if enabled:
                        self.assertEqual(provider["ApiKey"], "fixture-key")
                        self.assertIn(provider["DefaultModel"], config["Budget"]["Models"])
                    else:
                        self.assertNotIn("ApiKey", provider)
                    packages = json.loads((directory / "worker-packages.json").read_text())
                    registry = json.loads((directory / "registry.json").read_text())
                    credential = base64.b64decode(registry["auths"]["ghcr.io"]["auth"]).decode()
                    self.assertEqual(credential, f"{packages['username']}:{packages['token']}")
                    uuid.UUID((directory / "dataset-id").read_text().strip())
                    sql = (directory / "create-users.sql").read_text()
                    passwords = set()
                    for filename in ("sql-controller.txt", "sql-initialize.txt", "sql-worker.txt"):
                        connection = (directory / filename).read_text()
                        self.assertIn('Initial Catalog="Sample;""Forge"""', connection)
                        password = connection.split("Password=")[1].split(";")[0]
                        self.assertIn(f"WITH PASSWORD = '{password}'", sql)
                        passwords.add(password)
                    self.assertEqual(len(passwords), 3)
                    if os.name == "posix":
                        self.assertEqual(directory.stat().st_mode & 0o777, 0o700)
                        self.assertTrue(all(p.stat().st_mode & 0o077 == 0 for p in directory.iterdir()))
                    with self.assertRaises(SystemExit):
                        self.prepare(directory, enabled)
                    self.assertEqual(sql, (directory / "create-users.sql").read_text())
