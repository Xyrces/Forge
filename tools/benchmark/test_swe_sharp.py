"""Trusted importer/grader checks; no containers, network, or model calls."""
import csv
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import swe_sharp as sharp


class SweSharpTests(unittest.TestCase):
    def dataset_row(self):
        return {"repo": "owner/repo", "instance_id": "owner__repo-1", "base_commit": "a" * 40,
                "patch": "source patch", "test_patch": "hidden patch", "problem_statement": "Fix this.\nKeep compatibility.",
                "hints_text": "private hint", "created_at": "2025-01-01", "version": "0.1",
                "FAIL_TO_PASS": "['repair']", "PASS_TO_PASS": "['regression']"}

    def test_csv_literal_lists_and_duplicate_id_rejection(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "data.csv"
            def write(rows):
                with path.open("w", newline="") as f:
                    writer = csv.DictWriter(f, fieldnames=list(self.dataset_row()))
                    writer.writeheader()
                    writer.writerows(rows)
            write([self.dataset_row()])
            self.assertEqual(["repair"], sharp.load_dataset(path)["owner__repo-1"]["FAIL_TO_PASS"])
            write([self.dataset_row(), self.dataset_row()])
            with self.assertRaises(ValueError):
                sharp.load_dataset(path)
            row = self.dataset_row()
            row["FAIL_TO_PASS"] = "__import__('os').system('false')"
            write([row])
            with self.assertRaises(ValueError):
                sharp.load_dataset(path)

    def report(self, resolved=True):
        return {"task": {"patch_is_None": False, "patch_exists": True, "patch_successfully_applied": True,
            "resolved": resolved, "forge_log_parse_success": True,
            "forge_observed_tests": {"repair": "PASSED" if resolved else "FAILED", "regression": "PASSED"},
            "tests_status": {"FAIL_TO_PASS": {"success": ["repair"] if resolved else [], "failure": [] if resolved else ["repair"]},
                             "PASS_TO_PASS": {"success": ["regression"], "failure": []}}}}

    def test_grader_requires_every_declared_test_observed(self):
        row = {"instance_id": "task", "FAIL_TO_PASS": ["repair"], "PASS_TO_PASS": ["regression"]}
        for resolved in (True, False):
            self.assertTrue(sharp.check_official_report(self.report(resolved), row, resolved))
        for mutate in (
            lambda r: r["task"]["forge_observed_tests"].pop("repair"),
            lambda r: r["task"]["forge_observed_tests"].update(repair="SKIPPED"),
            lambda r: r["task"].update(patch_successfully_applied=False),
            lambda r: r["task"]["tests_status"]["PASS_TO_PASS"]["success"].append("regression"),
            lambda r: r["task"].update(forge_log_parse_success=False),
        ):
            report = self.report()
            mutate(report)
            with self.assertRaises(ValueError):
                sharp.check_official_report(report, row, True)
        with self.assertRaises(ValueError):
            sharp.check_official_report(self.report(True), row, False)

    def test_snapshot_preserves_exact_tree_without_future_history_or_ignored_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            upstream = root / "upstream"
            upstream.mkdir()
            sharp.git(upstream, "init", "-q", "-b", "main")
            (upstream / ".gitignore").write_text("tracked.cs\n")
            (upstream / ".gitattributes").write_text("*.cs text eol=lf\n")
            (upstream / "tracked.cs").write_text("public class Original {}\n")
            sharp.git(upstream, "add", "-f", ".")
            sharp.git(upstream, "commit", "-q", "-m", "base")
            base = sharp.git(upstream, "rev-parse", "HEAD")
            (upstream / "future-solution.cs").write_text("private solution\n")
            sharp.git(upstream, "add", ".")
            sharp.git(upstream, "commit", "-q", "-m", "future fix")
            future = sharp.git(upstream, "rev-parse", "HEAD")
            original_git = sharp.git
            def local_fetch(directory, *args, **kwargs):
                args = tuple(str(upstream) if a == "https://github.com/owner/repo.git" else a for a in args)
                return original_git(directory, *args, **kwargs)
            target = root / "snapshot"
            with patch.object(sharp, "git", side_effect=local_fetch):
                _, tree = sharp.snapshot_repository({"repo": "owner/repo", "base_commit": base}, root / "cache", target)
            self.assertEqual(original_git(upstream, "rev-parse", base + "^{tree}"), tree)
            self.assertTrue((target / "tracked.cs").exists())
            self.assertFalse((target / "future-solution.cs").exists())
            self.assertEqual("1", original_git(target, "rev-list", "--all", "--count"))
            self.assertEqual("", original_git(target, "remote"))
            with self.assertRaises(subprocess.CalledProcessError):
                original_git(target, "cat-file", "-e", future)

    def test_hidden_tests_and_build_config_cannot_be_candidate_edits(self):
        with tempfile.TemporaryDirectory() as tmp:
            repo = Path(tmp)
            sharp.git(repo, "init", "-q", "-b", "main")
            for name in ("Code.cs", "Checks.cs", "Project.csproj"):
                (repo / name).write_text("old\n")
            sharp.git(repo, "add", ".")
            sharp.git(repo, "commit", "-q", "-m", "base")
            def diff_for(name):
                (repo / name).write_text("new\n")
                content = sharp.git(repo, "diff") + "\n"
                sharp.git(repo, "checkout", "--", name)
                return content
            hidden = diff_for("Checks.cs")
            candidate = repo / "candidate.patch"
            for name, accepted in (("Code.cs", True), ("Checks.cs", False), ("Project.csproj", False)):
                candidate.write_text(diff_for(name))
                if accepted:
                    self.assertEqual([name], sharp.validate_candidate_patch(candidate, {"test_patch": hidden}, repo))
                else:
                    with self.assertRaises(ValueError):
                        sharp.validate_candidate_patch(candidate, {"test_patch": hidden}, repo)

    def test_unsafe_paths_and_duplicate_json_rejected(self):
        for name in ("/root/file", "../file", ".git/config", "a/../../b", "a\\b", "a\nb", "a/.portHorizon/db"):
            with self.assertRaises(ValueError):
                sharp.safe_path(name)
        with self.assertRaises(ValueError):
            json.loads('{"id": 1, "id": 2}', object_pairs_hook=sharp.unique_object)


if __name__ == "__main__":
    unittest.main()
