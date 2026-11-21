import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "check_pr_privacy.py"
PRIVATE = "harbor" + "operator"
HOME_PATH = "/" + "home/" + PRIVATE + "/checkout"


class PrivacyDiffTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.repo = Path(self.scratch.name)
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.name", "Example Maintainer")
        self.git("config", "user.email", "maintainer@example.com")
        self.git("config", "core.autocrlf", "false")
        self.write("notes.md", "Public project documentation.\n")
        self.base = self.commit("Initial example")

    def git(self, *args):
        result = subprocess.run(
            ["git", *args], cwd=self.repo, capture_output=True, check=True
        )
        return result.stdout.decode("utf-8").strip()

    def write(self, name, text, encoding="utf-8"):
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(text.encode(encoding))

    def commit(self, message):
        self.git("add", "--all")
        self.git("commit", "-q", "-m", message)
        return self.git("rev-parse", "HEAD")

    def scan(self, terms=None, metadata=None, extra=()):
        env = dict(os.environ)
        env.pop("FORGE_PRIVATE_TERMS", None)
        if terms is not None:
            env["FORGE_PRIVATE_TERMS"] = terms
        args = [sys.executable, str(SCRIPT), *extra]
        if metadata is not None:
            metadata_path = self.repo / "pr-metadata.json"
            metadata_path.write_text(json.dumps(metadata), encoding="utf-8")
            args += ["--pr-metadata", str(metadata_path)]
        result = subprocess.run(
            [*args, self.base, "HEAD"], cwd=self.repo, env=env, capture_output=True
        )
        output = (result.stdout + result.stderr).decode("utf-8", "replace")
        self.assertNotIn(PRIVATE, output)
        self.assertNotIn(HOME_PATH, output)
        return result.returncode, output

    def test_clean_additions_with_example_settings_pass(self):
        self.write("guide.md", 'Use admin@example.com and <sql-server>.database.windows.net.\n')
        self.commit("Document an example")
        self.assertEqual(0, self.scan()[0])

    def test_new_private_line_reports_location_without_value(self):
        self.write("notes.md", "Public project documentation.\n" + HOME_PATH + "\n")
        self.commit("Add operator setup")
        code, output = self.scan()
        self.assertEqual(1, code)
        self.assertIn("notes.md:2", output)

    def test_existing_private_line_does_not_block_unrelated_addition(self):
        self.write("notes.md", HOME_PATH + "\n")
        self.base = self.commit("Existing legacy content")
        self.write("notes.md", HOME_PATH + "\nNew public instructions.\n")
        self.commit("Add public instructions")
        self.assertEqual(0, self.scan()[0])

    def test_deleting_existing_private_file_passes(self):
        self.write("legacy.md", HOME_PATH + "\n")
        self.base = self.commit("Existing legacy content")
        (self.repo / "legacy.md").unlink()
        self.commit("Remove legacy content")
        self.assertEqual(0, self.scan()[0])

    def test_clean_rename_passes(self):
        self.git("mv", "notes.md", "guide.md")
        self.commit("Rename guide")
        self.assertEqual(0, self.scan()[0])

    def test_renamed_private_filename_is_redacted(self):
        self.git("mv", "notes.md", PRIVATE + ".md")
        self.commit("Rename guide")
        code, output = self.scan(json.dumps([PRIVATE]))
        self.assertEqual(1, code)
        self.assertIn("name withheld", output)

    @unittest.skipIf(os.name == "nt", "Windows forbids terminal-control filename characters")
    def test_filename_cannot_inject_actions_commands_or_terminal_controls(self):
        name = "::error::pretend-pass\n\x1b[31m.md"
        self.write(name, HOME_PATH + "\n")
        self.commit("Add test fixture")
        code, output = self.scan()
        self.assertEqual(1, code)
        self.assertIn("name withheld", output)
        self.assertNotIn("::error::", output)
        self.assertNotIn("\x1b", output)
        self.assertNotIn("pretend-pass", output)

    def test_utf16_addition_is_scanned(self):
        self.write("unicode.md", "Public heading\n" + HOME_PATH + "\n", "utf-16")
        self.commit("Add Unicode documentation")
        code, output = self.scan()
        self.assertEqual(1, code)
        self.assertIn("unicode.md:2", output)

    def test_utf16_existing_private_line_is_not_new(self):
        self.write("unicode.md", HOME_PATH + "\n", "utf-16")
        self.base = self.commit("Existing Unicode documentation")
        self.write("unicode.md", HOME_PATH + "\nPublic addition.\n", "utf-16")
        self.commit("Expand documentation")
        self.assertEqual(0, self.scan()[0])

    def test_malformed_private_term_configuration_fails_closed(self):
        for value in ["{", json.dumps({"terms": [PRIVATE]}), json.dumps([PRIVATE, 7]), json.dumps([" "])]:
            with self.subTest(configuration_type=value[:1]):
                self.assertGreater(self.scan(value)[0], 1)

    def test_required_private_terms_missing_fails_closed(self):
        self.assertGreater(self.scan(extra=("--require-private-terms",))[0], 1)

    def test_intermediate_leak_still_blocks_after_later_removal(self):
        self.write("temporary.md", HOME_PATH + "\n")
        self.commit("Intermediate change")
        (self.repo / "temporary.md").unlink()
        self.commit("Remove intermediate change")
        self.assertEqual(1, self.scan()[0])

    def test_commit_message_is_scanned_without_printing_message(self):
        self.write("notes.md", "Public update.\n")
        self.commit("Operator context " + HOME_PATH)
        code, output = self.scan()
        self.assertEqual(1, code)
        self.assertIn("commit message", output)

    def test_legacy_commit_message_already_in_base_is_skipped(self):
        self.write("notes.md", "Public update.\n")
        self.base = self.commit("Legacy operator context " + HOME_PATH)
        self.write("notes.md", "Another public update.\n")
        self.commit("Public update")
        self.assertEqual(0, self.scan()[0])

    def test_merge_of_base_private_lines_does_not_report_them_as_new(self):
        self.git("checkout", "-q", "-b", "topic")
        self.write("topic.md", "Topic work.\n")
        self.commit("Topic work")
        self.git("checkout", "-q", "main")
        self.write("legacy.md", HOME_PATH + "\n")
        self.base = self.commit("Existing base documentation")
        self.git("checkout", "-q", "topic")
        self.git("merge", "--no-ff", "-m", "Merge base", "main")
        self.assertEqual(0, self.scan()[0])

    def test_merge_resolution_introducing_private_line_is_detected(self):
        self.git("checkout", "-q", "-b", "topic")
        self.write("notes.md", "Topic wording.\n")
        self.commit("Topic wording")
        self.git("checkout", "-q", "main")
        self.write("notes.md", "Base wording.\n")
        self.base = self.commit("Base wording")
        self.git("checkout", "-q", "topic")
        result = subprocess.run(
            ["git", "merge", "--no-commit", "main"], cwd=self.repo, capture_output=True
        )
        self.assertEqual(1, result.returncode)
        self.write("notes.md", HOME_PATH + "\n")
        self.commit("Resolve wording")
        self.assertEqual(1, self.scan()[0])

    def test_private_pr_description_blocks_with_clean_commits(self):
        code, output = self.scan(metadata={"title": "Public title", "body": HOME_PATH})
        self.assertEqual(1, code)
        self.assertIn("PR", output)

    def test_placeholder_pr_description_and_null_body_pass(self):
        for body in [None, "Use <sql-server>.database.windows.net and admin@example.com."]:
            with self.subTest(body_present=body is not None):
                self.assertEqual(0, self.scan(metadata={"title": "Public title", "body": body})[0])


if __name__ == "__main__":
    unittest.main()
