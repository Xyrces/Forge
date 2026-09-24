#!/usr/bin/env python3
"""Trusted subprocess entry point for the pinned, unmodified SWE-Sharp grader.

Apply resource/network restrictions at Docker container creation, and record
exact image identities plus observed test statuses. No model calls occur here.
"""
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import sys


LABEL_KEY = "forge.benchmark.run"


def save_json(path, value):
    path = Path(path)
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("w") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)
    directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def cleanup_labeled_containers(client, label, environment_path, not_found_type):
    environment_path = Path(environment_path)
    environment = json.loads(environment_path.read_text()) if environment_path.exists() else {}
    errors = []
    containers = client.containers.list(all=True, filters={"label": f"{LABEL_KEY}={label}"})
    for container in containers:
        if container.labels.get(LABEL_KEY) != label:
            errors.append(f"label mismatch for container {container.id}")
            continue
        try:
            container.stop(timeout=10)
        except not_found_type:
            continue
        except Exception as error:  # Docker errors must be reported, but removal should still be attempted.
            errors.append(f"stop {container.id}: {type(error).__name__}: {error}")
        try:
            container.remove(force=True)
        except not_found_type:
            pass
        except Exception as error:
            errors.append(f"remove {container.id}: {type(error).__name__}: {error}")
    environment["cleanup"] = {"label": label, "matchedContainerIds": [c.id for c in containers],
                              "success": not errors, "errors": errors}
    save_json(environment_path, environment)
    if errors:
        raise RuntimeError("; ".join(errors))


def cleanup_main(label, environment_path):
    import docker

    if not label.startswith("forge-swe-sharp-") or len(label) != 48 or any(c not in "0123456789abcdef" for c in label[16:]):
        raise ValueError("invalid benchmark cleanup label")
    client = docker.from_env()
    client.ping()
    cleanup_labeled_containers(client, label, environment_path, docker.errors.NotFound)


def main():
    import docker
    import swe_sharp_bench.cli as cli
    from swe_sharp_bench.grading import get_logs_eval

    source = Path(cli.__file__).parent
    packages = sorted((d.metadata["Name"], d.version) for d in importlib.metadata.distributions())
    hashes = {str(p.relative_to(source)): hashlib.sha256(p.read_bytes()).hexdigest() for p in source.rglob("*.py")}
    fingerprint = hashlib.sha256(json.dumps({"packages": packages, "source": hashes,
        "wrapper": hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}, sort_keys=True).encode()).hexdigest()
    label = os.environ.get("FORGE_BENCHMARK_CONTAINER_LABEL", "")
    if not label.startswith("forge-swe-sharp-") or len(label) != 48 or any(c not in "0123456789abcdef" for c in label[16:]):
        raise ValueError("missing or invalid benchmark container label")
    environment = {"fingerprint": fingerprint, "images": {}, "network": "disabled", "memoryLimit": "8g",
                   "cpus": 2, "pids": 512, "containerLabel": label, "containerIds": []}

    def save():
        save_json("environment.json", environment)

    original_create = docker.models.containers.ContainerCollection.create

    def restricted_create(self, image, *args, **kwargs):
        if (args or kwargs.get("volumes") or kwargs.get("mounts") or kwargs.get("privileged")
                or kwargs.get("pid_mode") or kwargs.get("ipc_mode") or kwargs.get("devices")):
            raise ValueError("unexpected host/container access requested by evaluator")
        kwargs.pop("network_mode", None)
        labels = kwargs.pop("labels", None)
        if labels is not None and not isinstance(labels, dict):
            raise ValueError("unexpected non-object Docker labels")
        labels = dict(labels or {})
        if LABEL_KEY in labels:
            raise ValueError("evaluator attempted to set the reserved benchmark label")
        labels[LABEL_KEY] = label
        kwargs.update(network_disabled=True, mem_limit="8g", nano_cpus=2_000_000_000,
                      pids_limit=512, cap_drop=["ALL"], security_opt=["no-new-privileges"], labels=labels)
        container = original_create(self, image, **kwargs)
        environment["containerIds"].append(container.id)
        save()  # ContainerCollection.run starts only after create returns.
        return container

    original_build = cli.build_container

    def bound_build(test_spec, *args, **kwargs):
        container = original_build(test_spec, *args, **kwargs)
        environment["images"][test_spec.instance_id] = container.image.id
        save()
        return container

    original_report = cli.get_eval_report

    def observed_report(test_spec, prediction, test_log_path, include_tests_status):
        report = original_report(test_spec, prediction, test_log_path, include_tests_status)
        statuses, found = get_logs_eval(test_spec, str(test_log_path))
        ident = prediction["instance_id"]
        expected = set(test_spec.FAIL_TO_PASS) | set(test_spec.PASS_TO_PASS)
        report[ident]["forge_observed_tests"] = {test: statuses[test] for test in expected if test in statuses}
        report[ident]["forge_log_parse_success"] = bool(found)
        return report

    docker.models.containers.ContainerCollection.create = restricted_create
    cli.build_container = bound_build
    cli.get_eval_report = observed_report
    save()
    try:
        # from_env().ping() fails before building/pulling anything if unavailable.
        docker.from_env().ping()
        cli.cs_main()
    finally:
        save()


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "--cleanup-label":
        cleanup_main(sys.argv[2], sys.argv[3])
    else:
        main()
