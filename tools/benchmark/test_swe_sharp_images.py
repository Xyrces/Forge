"""Focused tests for local SWE-Sharp image provenance and Dockerfile normalization."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

MODULE_PATH = Path(__file__).with_name("swe_sharp_images.py")
SPEC = importlib.util.spec_from_file_location("swe_sharp_images", MODULE_PATH)
images = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(images)


class ImageBuilderTests(unittest.TestCase):
    def test_normalization_changes_only_known_upstream_typos(self):
        env = "FROM --platform=linux/x86_64 sweb.base.x86_64:latest\nRUN echo intact\n"
        self.assertEqual(images.normalize_dockerfile("env", env),
                         "FROM --platform=linux/amd64 sweb.base.cs.x86_64:latest\nRUN echo intact\n")
        self.assertEqual(images.normalize_dockerfile("base", "FROM --platform=linux/x86_64 ubuntu:22.04\n"),
                         "FROM --platform=linux/amd64 docker.io/library/ubuntu:22.04\n")
        with self.assertRaises(ValueError):
            images.normalize_dockerfile("env", "FROM ubuntu:22.04\n")

    def test_load_specs_selects_one_and_uses_evaluator_namespace(self):
        calls = []
        fake_module = types.ModuleType("swe_sharp_bench.test_spec")

        def make_test_spec(row, namespace):
            calls.append((row["instance_id"], namespace))
            return types.SimpleNamespace(language="cs", arch="x86_64",
                                         instance_image_key=f"{namespace}/sweb.eval.cs.x86_64."
                                         f"{row['instance_id'].lower().replace('__', '_1776_')}:latest")

        fake_module.make_test_spec = make_test_spec
        with tempfile.TemporaryDirectory() as temporary:
            dataset = Path(temporary) / "dataset.json"
            dataset.write_text(json.dumps([{"instance_id": "ardalis__cleanarchitecture-546",
                                            "base_commit": "a" * 40},
                                           {"instance_id": "another__case-1",
                                            "base_commit": "b" * 40}]))
            with patch.dict(sys.modules, {"swe_sharp_bench": types.ModuleType("swe_sharp_bench"),
                                          "swe_sharp_bench.test_spec": fake_module}):
                spec, = images.load_specs(Path(temporary), dataset, "ardalis__cleanarchitecture-546")
        self.assertEqual(calls, [("ardalis__cleanarchitecture-546", "swebcs")])
        self.assertEqual(spec.instance_image_key,
                         "swebcs/sweb.eval.cs.x86_64.ardalis_1776_cleanarchitecture-546:latest")

    def test_matching_image_reused_without_build_and_collision_rejected(self):
        dockerfile = "FROM --platform=linux/amd64 ubuntu:22.04\n"
        parent = "sha256:" + "a" * 64
        image = "sweb.base.cs.x86_64:latest"
        inputs = {"stage": "base", "name": image, "platform": images.PLATFORM,
                  "parentId": parent, "dockerfileSha256": images.digest(dockerfile.encode()),
                  "upstreamGeneratedDockerfileSha256": images.digest(dockerfile.encode()),
                  "scriptsSha256": {}}
        fingerprint = images.json_digest(inputs)
        existing = {"Id": "b" * 64,
                    "Labels": {images.LABEL: fingerprint}}
        with tempfile.TemporaryDirectory() as temporary, \
             patch.object(images, "inspect_image", return_value=existing), \
             patch.object(images, "run") as execute:
            result = images.build_stage("base", image, dockerfile, {}, parent, Path(temporary))
            self.assertTrue(result["reused"])
            self.assertEqual(result["imageId"], "sha256:" + existing["Id"])
            execute.assert_not_called()
        existing["Labels"][images.LABEL] = "different"
        with tempfile.TemporaryDirectory() as temporary, \
             patch.object(images, "inspect_image", return_value=existing), \
             patch.object(images, "run") as execute:
            with self.assertRaisesRegex(ValueError, "different or absent build provenance"):
                images.build_stage("base", image, dockerfile, {}, parent, Path(temporary))
            execute.assert_not_called()

    def test_parent_pin_changes_only_from_line(self):
        parent = "sha256:" + "a" * 64
        original = "FROM --platform=linux/amd64 sweb.base.cs.x86_64:latest\nRUN echo intact\n"
        self.assertEqual(images.pin_parent(original, "sweb.base.cs.x86_64:latest", parent),
                         f"FROM --platform=linux/amd64 {parent}\nRUN echo intact\n")
        with self.assertRaises(ValueError):
            images.pin_parent(original, "another:latest", parent)

    def test_hardening_keeps_base_identity_and_prunes_full_clone_history(self):
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary) / "testbed"
            subprocess.run(["git", "init", "-q", str(repo)], check=True)
            def git(*args):
                return subprocess.run(["git", *args], cwd=repo, text=True,
                                      stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                      check=True).stdout.strip()
            git("config", "user.name", "Benchmark Test")
            git("config", "user.email", "benchmark@example.invalid")
            file = repo / "example.txt"
            commits = []
            for content in ("parent", "base", "future"):
                file.write_text(content)
                git("add", "example.txt")
                git("commit", "-qm", content)
                commits.append(git("rev-parse", "HEAD"))
            git("reset", "--hard", commits[1])
            file.write_text("setup-modified")
            before_diff = git("diff", "--binary", "--")
            (repo / ".gitignore").write_text("build/\n")
            # The ignored build artifact should survive without becoming Git history.
            git("add", ".gitignore")
            before_index = git("diff", "--cached", "--binary", "--")
            (repo / "build").mkdir()
            (repo / "build" / "output.dll").write_text("compiled")
            script = images.hardened_repo_script("#!/bin/bash\nset -e\n", commits[1])
            script = script.replace("/testbed", str(repo))
            subprocess.run(["bash", "-c", script], cwd=repo, check=True,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            self.assertEqual(git("rev-parse", "HEAD"), commits[1])
            self.assertEqual(git("rev-list", "HEAD"), commits[1])
            self.assertEqual(file.read_text(), "setup-modified")
            self.assertEqual(git("diff", "--binary", "--"), before_diff)
            self.assertEqual(git("diff", "--cached", "--binary", "--"), before_index)
            self.assertEqual((repo / "build" / "output.dll").read_text(), "compiled")
            for hidden in (commits[0], commits[2]):
                result = subprocess.run(["git", "cat-file", "-e", hidden], cwd=repo,
                                        stdout=subprocess.PIPE, stderr=subprocess.PIPE)
                self.assertNotEqual(result.returncode, 0)


if __name__ == "__main__":
    unittest.main()
