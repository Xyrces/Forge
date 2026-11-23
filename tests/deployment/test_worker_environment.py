import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock


script = Path(__file__).resolve().parents[2] / "deploy/k3s/worker_environment.py"
spec = importlib.util.spec_from_file_location("worker_environment", script)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class EnvironmentBuildTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name).resolve()
        self.origin = self.root / "origin"
        self.origin.mkdir()
        self.git(self.origin, "init", "-q", "-b", "main")
        tools = self.origin / "tools"
        tools.mkdir()
        (tools / "Dockerfile").write_text("FROM example.invalid/approved\n")
        (self.origin / "outside").write_text("outside context\n")
        self.git(self.origin, "add", ".")
        self.git(self.origin, "commit", "-qm", "Initial fixture")
        self.repo = self.root / "checkout"
        self.git(self.root, "clone", "-q", str(self.origin), str(self.repo))
        self.project = {"repoUrl": str(self.origin), "defaultBranch": "main"}
        self.settings = {"buildContext": "tools", "dockerfilePath": "tools/Dockerfile"}

    @staticmethod
    def git(repo, *args):
        return subprocess.check_output(["git", "-C", str(repo), "-c", "user.name=Fixture",
                                        "-c", "user.email=fixture@example.invalid", *args], text=True).strip()

    def export(self):
        return worker.snapshot(self.repo, self.project, self.settings, self.root / "context")

    def test_uses_registered_branch_and_only_committed_context(self):
        self.git(self.repo, "switch", "-qc", "other")
        (self.repo / "tools/Dockerfile").write_text("FROM wrong-branch")
        self.git(self.repo, "commit", "-qam", "Different local branch")
        (self.repo / "tools/private").write_text("untracked")
        expected = "FROM example.invalid/updated\n"
        (self.origin / "tools/Dockerfile").write_text(expected)
        self.git(self.origin, "commit", "-qam", "Updated after cloning")
        sha, dockerfile = self.export()
        self.assertEqual(sha, self.git(self.origin, "rev-parse", "HEAD"))
        self.assertEqual(expected, dockerfile.read_text())
        self.assertEqual(["Dockerfile"], sorted(p.name for p in dockerfile.parent.iterdir()))

    def test_wrong_project_or_outside_context_is_rejected(self):
        self.project["repoUrl"] = str(self.root / "unrelated")
        with self.assertRaisesRegex(ValueError, "registered repository"):
            self.export()
        self.project["repoUrl"] = str(self.origin)
        self.settings["dockerfilePath"] = "outside"
        with self.assertRaises(ValueError):
            self.export()

    @unittest.skipIf(os.name == "nt", "Linux build host uses POSIX symlinks")
    def test_symlink_cannot_import_host_files_into_build_context(self):
        (self.origin / "tools/link").symlink_to("../../outside")
        self.git(self.origin, "add", "tools/link")
        self.git(self.origin, "commit", "-qm", "Link outside context")
        with self.assertRaises(worker.tarfile.FilterError):
            self.export()

    def test_failed_image_check_never_activates_definition(self):
        dashboard = mock.Mock()
        dashboard.call.side_effect = [self.settings, self.project]
        error = subprocess.CalledProcessError(1, "podman run")
        with mock.patch.object(worker.os, "geteuid", return_value=1000, create=True), \
             mock.patch.object(worker, "snapshot", return_value=("a" * 40, "Dockerfile")), \
             mock.patch.object(worker, "output", side_effect=["true", "image-id"]), \
             mock.patch.object(worker, "run", side_effect=[None, error]) as commands:
            with self.assertRaises(subprocess.CalledProcessError):
                worker.qualify(dashboard, "demo", self.repo, "ninja --version")
        self.assertEqual([mock.call("/worker-environment"), mock.call()], dashboard.call.call_args_list)
        flags = commands.call_args.args[0]
        self.assertIn("--network=none", flags)
        self.assertIn("--read-only", flags)
        self.assertIn("10001:10001", flags)

    def test_bearer_transport_is_restricted(self):
        for url in ("http://example.invalid", "https://someone@example.invalid", "file:///tmp"):
            with self.subTest(url=url), self.assertRaises(ValueError):
                worker.Dashboard(url, "demo", "test")
        for url in ("http://127.0.0.1", "http://[::1]", "https://example.invalid"):
            self.assertTrue(worker.Dashboard(url, "demo", "test").base.startswith(url))

    def test_https_and_ssh_github_clones_match(self):
        self.assertEqual(worker.repository_id("https://github.com/example/project.git"),
                         worker.repository_id("git@github.com:example/project.git"))
