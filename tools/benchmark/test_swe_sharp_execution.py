from types import SimpleNamespace
import unittest

import swe_sharp_eval as evaluator


class ExecutionBoundaryTests(unittest.TestCase):
    marker = "forge-swe-sharp-" + "a" * 32 + "-execution"

    def test_repository_display_cannot_be_compiler_or_trx_evidence(self):
        spec = SimpleNamespace(eval_script_list=["git show", "git diff " + "b" * 40,
                                                "dotnet build", "git apply -v -"])
        evaluator.mark_execution(spec, self.marker)
        self.assertEqual("echo " + self.marker, spec.eval_script_list[2])
        log = ('+ // Permission Denied; success : error ; <TestRun>\n'
               '+ echo ' + self.marker + '\n' + self.marker + '\n'
               '+ dotnet build\nBuild succeeded.\n+ git apply -v -\n')
        execution = evaluator.execution_output(log, self.marker)
        self.assertNotIn("Permission Denied", execution)
        self.assertTrue(evaluator.pristine_build_succeeded(execution))

    def test_boundary_absent_duplicate_or_upstream_shape_change_fails(self):
        for log in ("", self.marker + "\n" + self.marker):
            with self.assertRaises(ValueError):
                evaluator.execution_output(log, self.marker)
        with self.assertRaises(ValueError):
            evaluator.mark_execution(SimpleNamespace(eval_script_list=["git diff"]), self.marker)

    def test_image_presence_or_later_build_success_is_not_pristine_build_proof(self):
        for log in ("Build succeeded.\n+ git apply -v -",
                    "+ dotnet build\n+ git apply -v -\nBuild succeeded.",
                    "+ dotnet build\nBuild succeeded.\n+ dotnet build\nBuild FAILED.\n+ git apply -v -",
                    "+ dotnet build\nerror NU1301: missing dependency\nBuild succeeded.\n+ git apply -v -",
                    "+ dotnet build\nBuild succeeded.\nerror: checkout failed\n+ git apply -v -"):
            with self.subTest(log=log):
                self.assertFalse(evaluator.pristine_build_succeeded(log))


if __name__ == "__main__":
    unittest.main()
