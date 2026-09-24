"""Independent TRX reconciliation for trusted SWE-Sharp evaluation logs."""
import re
import xml.etree.ElementTree as ET


def inspect_trx(log_text):
    blocks = re.findall(r"<TestRun\b[^>]*>.*?</TestRun\s*>", log_text, re.DOTALL)
    valid = bool(blocks) and len(blocks) == len(re.findall(r"<TestRun\b", log_text))
    statuses, counts = {}, {}
    severity = {"PASSED": 0, "SKIPPED": 1, "FAILED": 2, "ERROR": 3}
    for block in blocks:
        try:
            root = ET.fromstring(block)
        except ET.ParseError:
            valid = False
            continue
        # Standard TRX uses this namespace; an absent/foreign schema is not evidence.
        ns = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
        if root.tag != ns + "TestRun":
            valid = False
            continue
        definitions = {}
        for definition in root.iter(ns + "UnitTest"):
            method = definition.find(ns + "TestMethod")
            ident = definition.get("id")
            if (not ident or ident in definitions or method is None
                    or not method.get("className") or not method.get("name")):
                valid = False
                continue
            definitions[ident] = method.get("className") + "." + method.get("name")
        results = list(root.iter(ns + "UnitTestResult"))
        if not results:
            valid = False
        for result in results:
            status = {"Passed": "PASSED", "Failed": "FAILED", "Skipped": "SKIPPED",
                      "NotExecuted": "SKIPPED", "Error": "ERROR"}.get(result.get("outcome"), "ERROR")
            counts[status] = counts.get(status, 0) + 1
            name = definitions.get(result.get("testId"))
            if not name:
                valid = False
                continue
            previous = statuses.get(name, "PASSED")
            statuses[name] = max((previous, status), key=severity.__getitem__)
    return {"statuses": statuses, "counts": counts, "valid": valid}
