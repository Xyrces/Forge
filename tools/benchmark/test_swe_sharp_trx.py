import unittest

from swe_sharp_trx import inspect_trx


def trx(outcomes):
    results = "".join(f'<UnitTestResult testId="case" outcome="{outcome}" />' for outcome in outcomes)
    return ('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            '<TestDefinitions><UnitTest id="case"><TestMethod className="Suite" name="Case" />'
            '</UnitTest></TestDefinitions><Results>' + results + '</Results></TestRun>')


class TrxTests(unittest.TestCase):
    def test_later_passing_parameter_or_framework_cannot_hide_failure(self):
        for log in (trx(["Failed", "Passed"]), trx(["Failed"]) + trx(["Passed"])):
            evidence = inspect_trx(log)
            self.assertTrue(evidence["valid"])
            self.assertEqual({"Suite.Case": "FAILED"}, evidence["statuses"])
            self.assertEqual({"FAILED": 1, "PASSED": 1}, evidence["counts"])

    def test_only_all_passing_variants_pass(self):
        self.assertEqual("PASSED", inspect_trx(trx(["Passed", "Passed"]))["statuses"]["Suite.Case"])
        for outcome in ("Skipped", "NotExecuted", "Error", "Inconclusive"):
            self.assertNotEqual("PASSED", inspect_trx(trx([outcome, "Passed"]))["statuses"]["Suite.Case"])

    def test_missing_malformed_unknown_or_unidentified_evidence_is_invalid(self):
        for log in ("", "<TestRun>", trx(["Passed"]) + "<TestRun>",
                    trx(["Passed"]).replace('testId="case"', 'testId="unknown"'),
                    trx(["Passed"]).replace("http://microsoft.com/schemas/VisualStudio/TeamTest/2010", "foreign"),
                    trx([]), trx(["Passed"]).replace("</Results>", "</Broken>")):
            with self.subTest(log=log):
                self.assertFalse(inspect_trx(log)["valid"])


if __name__ == "__main__":
    unittest.main()
