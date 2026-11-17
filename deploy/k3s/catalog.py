#!/usr/bin/env python3
"""Register an operator-reviewed CI receipt. Does not build, approve, or deploy it."""
import argparse
import json
from pathlib import Path
import re
from release import validate_image


def validate_receipt(receipt):
    source_sha, image = receipt.get("sourceSha", ""), receipt.get("imageDigest", "")
    if not re.fullmatch(r"[0-9a-f]{40}", source_sha):
        raise ValueError("receipt requires a full lowercase source SHA")
    validate_image(image)
    if receipt.get("buildPassed") is not True or receipt.get("testPassed") is not True or receipt.get("workflow") != "controller-image.yml" or not isinstance(receipt.get("workflowRunId"), str) or not receipt["workflowRunId"].strip():
        raise ValueError("receipt requires successful build/test attestation and controller-image workflow run identity")
    return receipt


def register(api, receipt):
    validate_receipt(receipt)
    source_sha, image = receipt["sourceSha"], receipt["imageDigest"]
    current = api.get("configmap", "forge-image-catalog")
    if current:
        previous = current.get("data", {}).get(source_sha)
        if previous:
            if json.loads(previous)["imageDigest"] != image:
                raise ValueError("source SHA is already bound to another digest; refusing receipt replacement")
            return
        current.setdefault("data", {})[source_sha] = json.dumps(receipt, sort_keys=True)
        # resourceVersion provides compare-and-swap; a concurrent registration must be retried.
        api.kubectl(["replace", "-f", "-", "-o", "json"], current)
    else:
        api.kubectl(["create", "-f", "-", "-o", "json"], {"apiVersion": "v1", "kind": "ConfigMap",
            "metadata": {"name": "forge-image-catalog"}, "data": {source_sha: json.dumps(receipt, sort_keys=True)}})


def main():
    from install import Installer
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--receipt", required=True)
    parser.add_argument("--namespace", default="forge")
    args = parser.parse_args()
    receipt = json.loads(Path(args.receipt).read_text(encoding="utf-8-sig"))
    register(Installer(args), receipt)
    print("Reviewed image receipt registered; deployment still requires build/test and explicit approval.")


if __name__ == "__main__":
    main()
