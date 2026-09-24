"""Trusted importer/grader checks; no containers, network, or model calls."""
import csv
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import swe_sharp as sharp
import swe_sharp_eval as evaluator
import run as benchmark_run


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
            "resolved": resolved, "forge_log_parse_success": True, "forge_independent_trx_valid": True,
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
            lambda r: r["task"]["forge_observed_tests"].update(repair="FAILED"),
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

    def compile_failure_report(self, path="tests/Hidden.cs", code="CS0122", markers=None):
        return {"task": {"patch_is_None": False, "patch_exists": True,
            "patch_successfully_applied": True, "resolved": False,
            "forge_log_parse_success": True, "forge_observed_tests": {},
            "forge_pristine_build_succeeded": True,
            "forge_independent_trx_valid": True,
            "forge_all_observed_status_counts": {"PASSED": 16},
            "forge_compiler_evidence": {"diagnostics": [
                {"path": path, "code": code, "project": "tests/Hidden.csproj"}],
                "unparsedErrorLineDigests": [], "environmentErrorCategories": markers or []},
            "tests_status": {"FAIL_TO_PASS": {"success": [], "failure": ["repair"]},
                             "PASS_TO_PASS": {"success": [], "failure": ["regression"]}}}}

    def test_empty_compile_failure_is_separate_and_confined_to_hidden_patch(self):
        row = {"instance_id": "task", "FAIL_TO_PASS": ["repair"], "PASS_TO_PASS": ["regression"],
               "patch": "gold patch",
               "test_patch": "diff --git a/tests/Hidden.cs b/tests/Hidden.cs\n--- a/tests/Hidden.cs\n+++ b/tests/Hidden.cs\n"}
        accepted = sharp.classify_empty_baseline(self.compile_failure_report(), row)
        self.assertEqual("hidden-test-compile-failure", accepted["kind"])
        # Candidate/gold acceptance remains strict even for this same report.
        with self.assertRaises(ValueError):
            sharp.check_official_report(self.compile_failure_report(), row, True)
        for report in (self.compile_failure_report(path="src/Product.cs"),
                       self.compile_failure_report(markers=["package-or-build-system"])):
            with self.assertRaises(ValueError):
                sharp.classify_empty_baseline(report, row)
        unparsed = self.compile_failure_report()
        unparsed["task"]["forge_compiler_evidence"]["unparsedErrorLineDigests"] = ["a" * 64]
        with self.assertRaises(ValueError):
            sharp.classify_empty_baseline(unparsed, row)
        no_pristine_build = self.compile_failure_report()
        no_pristine_build["task"]["forge_pristine_build_succeeded"] = False
        with self.assertRaises(ValueError):
            sharp.classify_empty_baseline(no_pristine_build, row)

    def test_compiler_evidence_allows_warnings_but_rejects_build_errors(self):
        warning = "/testbed/x.csproj : warning NU1903: advisory\n/testbed/tests/H.cs(1,2): error CS0122: nope [/testbed/tests/H.csproj]\n"
        evidence = evaluator.compiler_evidence(warning)
        self.assertEqual([], evidence["environmentErrorCategories"])
        self.assertEqual("tests/H.cs", evidence["diagnostics"][0]["path"])
        error = evaluator.compiler_evidence("/testbed/x.csproj : error NU1101: package missing\n")
        self.assertIn("package-or-build-system", error["environmentErrorCategories"])
        self.assertTrue(error["unparsedErrorLineDigests"])

    def test_dotnet_directive_adapter_uses_fqns_and_preserves_special_full_suite(self):
        calls = []
        def original(instance):
            calls.append(instance)
            if instance["repo"] in evaluator.SPECIAL_FULL_SUITE_REPOS:
                return []
            return ['"' + " | ".join(
                f"FullyQualifiedName~{name}" for name in instance["FAIL_TO_PASS"] + instance["PASS_TO_PASS"]) + '"']
        row = {"repo": "owner/repo", "FAIL_TO_PASS": ["N.C.repair"], "PASS_TO_PASS": ["N.C.regression"]}
        rendered = evaluator.dotnet_test_directives(original, row)
        self.assertIn("FullyQualifiedName~N.C.repair", rendered[0])
        self.assertIs(True, calls[-1]["dotnet"])
        special = dict(row, repo="ardalis/CleanArchitecture")
        self.assertEqual([], evaluator.dotnet_test_directives(original, special))
        self.assertNotIn("dotnet", calls[-1])

    def test_compile_failure_negative_control_must_repeat_exact_signature(self):
        row = {"instance_id": "task", "FAIL_TO_PASS": ["repair"], "PASS_TO_PASS": ["regression"],
               "patch": "gold patch",
               "test_patch": "diff --git a/tests/Hidden.cs b/tests/Hidden.cs\n--- a/tests/Hidden.cs\n+++ b/tests/Hidden.cs\n"}
        fingerprint = "f" * 64
        images = {"task": "sha256:" + "a" * 64}
        process = {"exitCode": 0, "timedOut": False}
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "control").mkdir()
            manifest_path = root / "manifest.json"
            manifest_path.write_text("{}")
            meta = {"manifestPath": str(manifest_path)}
            manifest = {"datasetSha256": "d" * 64, "cases": [{"id": "task"}]}

            for repeated_code, expected in (("CS0122", True), ("CS0123", False)):
                calls = iter([
                    (process, {"task": self.report(True)}, {"fingerprint": fingerprint, "images": images}),
                    (process, {"task": self.compile_failure_report()}, {"fingerprint": fingerprint, "images": images}),
                    (process, {"task": self.compile_failure_report(code=repeated_code)},
                     {"fingerprint": fingerprint, "images": images}),
                ])
                with patch.object(sharp, "prepared_control", return_value=(meta, manifest, {"task": row})), \
                     patch.object(sharp, "evaluate_official", side_effect=lambda *args: next(calls)):
                    self.assertIs(expected, sharp.preflight(root, Path("python"), 1))

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

    def test_created_container_constraints_are_read_back_fail_closed(self):
        label = "forge-swe-sharp-" + "a" * 32
        attributes = {
            "Config": {"NetworkDisabled": None, "Labels": {evaluator.LABEL_KEY: label}},
            "HostConfig": {"NetworkMode": "none", "Memory": evaluator.MEMORY_BYTES,
                           "NanoCpus": evaluator.NANO_CPUS, "CpuQuota": 0, "CpuPeriod": 0,
                           "PidsLimit": evaluator.PIDS_LIMIT, "CapDrop": ["ALL"],
                           "SecurityOpt": ["no-new-privileges"], "Privileged": False,
                           "Binds": None, "Devices": []},
            "Mounts": [], "NetworkSettings": {"Networks": {}},
        }

        class Container:
            def __init__(self, attrs):
                self.attrs = attrs
            def reload(self):
                pass

        self.assertTrue(evaluator.inspect_created_constraints(Container(attributes), label)["enforced"])
        mutations = (
            lambda value: value["HostConfig"].update(NetworkMode="bridge"),
            lambda value: value["HostConfig"].update(Memory=0),
            lambda value: value["HostConfig"].update(NanoCpus=0),
            lambda value: value["HostConfig"].update(PidsLimit=0),
            lambda value: value["HostConfig"].update(CapDrop=[]),
            lambda value: value["HostConfig"].update(SecurityOpt=[]),
            lambda value: value["HostConfig"].update(Privileged=True),
            lambda value: value["HostConfig"].update(Binds=["/host:/container"]),
            lambda value: value["HostConfig"].update(Devices=[{"PathOnHost": "/dev/sda"}]),
            lambda value: value.update(Mounts=[{"Source": "/host"}]),
            lambda value: value["Config"]["Labels"].update({evaluator.LABEL_KEY: "wrong"}),
        )
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                changed = json.loads(json.dumps(attributes))
                mutate(changed)
                self.assertFalse(evaluator.inspect_created_constraints(Container(changed), label)["enforced"])

    def test_running_container_must_show_cgroup_limits_and_no_network(self):
        class Result:
            def __init__(self, output):
                self.exit_code = 0
                self.output = output.encode()

        class Container:
            def __init__(self, values, networks=None):
                self.values = values
                self.attrs = {"NetworkSettings": {"Networks": networks or {}}}
            def reload(self):
                pass
            def exec_run(self, command):
                if command[0] == "ls":
                    return Result("lo\n")
                return Result(self.values[command[-1]])

        values = {"/sys/fs/cgroup/memory.max": str(evaluator.MEMORY_BYTES),
                  "/sys/fs/cgroup/pids.max": str(evaluator.PIDS_LIMIT),
                  "/sys/fs/cgroup/cpu.max": "200000 100000",
                  "/proc/self/status": "CapEff:\t0000000000000000\nNoNewPrivs:\t1\n"}
        created = {"enforced": True}
        self.assertTrue(evaluator.inspect_runtime_constraints(Container(values), created)["enforced"])
        self.assertTrue(evaluator.inspect_runtime_constraints(
            Container(values, {"none": {}}), created)["enforced"])
        for path, unsafe in (("/sys/fs/cgroup/memory.max", "max"),
                             ("/sys/fs/cgroup/pids.max", "max"),
                             ("/sys/fs/cgroup/cpu.max", "max 100000")):
            with self.subTest(path=path):
                changed = dict(values)
                changed[path] = unsafe
                self.assertFalse(evaluator.inspect_runtime_constraints(Container(changed), created)["enforced"])
        for status in ("CapEff:\t0000000000000001\nNoNewPrivs:\t1\n",
                       "CapEff:\t0000000000000000\nNoNewPrivs:\t0\n"):
            changed = dict(values)
            changed["/proc/self/status"] = status
            self.assertFalse(evaluator.inspect_runtime_constraints(Container(changed), created)["enforced"])
        self.assertFalse(evaluator.inspect_runtime_constraints(
            Container(values, {"podman": {}}), created)["enforced"])

    def test_labeled_start_attests_before_returning_to_evaluator(self):
        label = "forge-swe-sharp-" + "a" * 32

        class Result:
            exit_code = 0
            def __init__(self, output):
                self.output = output.encode()

        class Container:
            id = "container-1"
            attrs = {"Config": {"Labels": {evaluator.LABEL_KEY: label}},
                     "NetworkSettings": {"Networks": {}}}
            def reload(self):
                pass
            def exec_run(self, command):
                values = {"memory.max": str(evaluator.MEMORY_BYTES),
                          "pids.max": str(evaluator.PIDS_LIMIT), "cpu.max": "200000 100000",
                          "status": "CapEff:\t0000000000000000\nNoNewPrivs:\t1\n"}
                if command[0] == "ls":
                    return Result("lo\n")
                return Result(values[command[-1].rsplit("/", 1)[-1]])

        started = []
        saved = []
        environment = {"createdConstraints": {"container-1": {"enforced": True}},
                       "runtimeConstraints": {}, "containerInstances": {"container-1": "task"}}
        container = Container()
        result = evaluator.start_with_attestation(
            container, lambda target, *args, **kwargs: started.append(target.id) or "started",
            (), {}, label, environment, lambda: saved.append(dict(environment["runtimeConstraints"])))
        self.assertEqual("started", result)
        self.assertEqual(["container-1"], started)
        self.assertTrue(environment["runtimeConstraints"]["task"]["enforced"])
        self.assertTrue(saved)

        container.attrs["NetworkSettings"]["Networks"] = {"podman": {}}
        with self.assertRaisesRegex(RuntimeError, "did not retain"):
            evaluator.start_with_attestation(
                container, lambda target, *args, **kwargs: "started", (), {}, label,
                environment, lambda: None)

    def test_official_evaluator_absolutizes_artifacts_but_preserves_venv_symlink(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "venv/bin").mkdir(parents=True)
            target = root / "python-target"
            target.write_text("")
            (root / "venv/bin/python").symlink_to(target)
            (root / "dataset.json").write_text("[]")
            calls = []

            def fake_process(command, cwd, env, log, timeout):
                calls.append((command, Path(cwd), Path(log)))
                self.assertTrue(Path(cwd).is_absolute())
                self.assertTrue(Path(log).is_absolute())
                if len(calls) == 1:
                    prediction = Path(command[command.index("--predictions_path") + 1])
                    self.assertTrue(prediction.is_absolute())
                    self.assertTrue(prediction.is_file())
                else:
                    environment_path = Path(command[-1])
                    self.assertTrue(environment_path.is_absolute())
                    environment_path.write_text("{}")
                return {"exitCode": 0, "timedOut": False}

            previous = Path.cwd()
            try:
                os.chdir(root)
                with patch.object(benchmark_run, "run_process", side_effect=fake_process):
                    sharp.evaluate_official(
                        {"sourceRoot": str(root / "source"), "datasetPath": str(root / "dataset.json")},
                        Path("venv/bin/python"), [], ["task"], Path("relative/output"), 1)
            finally:
                os.chdir(previous)
            expected_python = str(root / "venv/bin/python")
            self.assertEqual(expected_python, calls[0][0][0])
            self.assertEqual(expected_python, calls[1][0][0])
            self.assertNotEqual(str(target), calls[0][0][0])


if __name__ == "__main__":
    unittest.main()
