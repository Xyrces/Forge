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
import re
import sys

from swe_sharp_trx import inspect_trx


LABEL_KEY = "forge.benchmark.run"
MEMORY_BYTES = 8 * 1024 * 1024 * 1024
NANO_CPUS = 2_000_000_000
PIDS_LIMIT = 512
SPECIAL_FULL_SUITE_REPOS = {
    "chocolatey/choco", "dotnet/BenchmarkDotNet", "ardalis/CleanArchitecture", "AvaloniaUI__Avalonia"
}


def mark_execution(test_spec, marker):
    """Separate the pinned evaluator's repository display from command execution."""
    positions = [i for i, command in enumerate(test_spec.eval_script_list)
                 if re.fullmatch(r"git diff [0-9a-f]{40}", command)]
    if len(positions) != 1 or not re.fullmatch(r"forge-swe-sharp-[0-9a-f]{32}-execution", marker):
        raise ValueError("unexpected pinned evaluator execution boundary")
    test_spec.eval_script_list.insert(positions[0] + 1, "echo " + marker)


def execution_output(log_text, marker):
    lines = log_text.splitlines()
    positions = [i for i, line in enumerate(lines) if line == marker]
    if len(positions) != 1:
        raise ValueError("missing or repeated evaluator execution boundary")
    return "\n".join(lines[positions[0] + 1:])


def pristine_build_succeeded(log_text):
    """Require this evaluation's successful build before hidden tests were applied."""
    apply = re.search(r"(?m)^\+ git apply(?:\s|$)", log_text)
    if apply is None:
        return False
    before = log_text[:apply.start()]
    builds = list(re.finditer(r"(?m)^\+ dotnet build(?:\s|$)", before))
    if not builds:
        return False
    build_output = before[builds[-1].end():]
    evidence = compiler_evidence(before)
    return (re.search(r"(?m)^Build succeeded\.$", build_output) is not None
            and not evidence["diagnostics"] and not evidence["unparsedErrorLineDigests"]
            and not evidence["environmentErrorCategories"]
            and not re.search(r"(?mi)^(?:fatal|error):", before))


def compiler_evidence(log_text):
    """Extract bounded, private evidence for the empty-control compile-failure fallback."""
    diagnostic = re.compile(
        r"^(?P<path>/testbed/[^\r\n:(]+\.cs)\((?:\d+)(?:,\d+)?\): error "
        r"(?P<code>CS\d{4}):.*?(?: \[(?P<project>/testbed/[^\]\r\n]+)\])?\s*$",
        re.IGNORECASE)
    diagnostics = []
    unmatched = []
    for line in log_text.splitlines():
        if ": error " not in line.lower():
            continue
        match = diagnostic.match(line.strip())
        if match:
            project = match.group("project")
            diagnostics.append({"path": match.group("path")[len("/testbed/"):],
                                "code": match.group("code").upper(),
                                "project": project[len("/testbed/"):] if project else None})
        else:
            unmatched.append(hashlib.sha256(line.encode()).hexdigest())
    marker_patterns = {
        # A pinned repository may legitimately emit NU/MSB warnings. Only an
        # error-severity diagnostic makes this an infrastructure/build failure.
        "package-or-build-system": r"(?:\berror\s+(?:NU|MSB|NETSDK)\d{3,}\b|\b(?:NU|MSB|NETSDK)\d{3,}\s*:\s*error\b)",
        "out-of-memory": r"out of memory|cannot allocate memory",
        "disk": r"no space left on device|disk quota exceeded",
        "permission": r"permission denied|operation not permitted",
        "network": r"network is unreachable|temporary failure in name resolution|name or service not known",
        "process-crash": r"segmentation fault|core dumped|killed process",
    }
    markers = sorted(name for name, pattern in marker_patterns.items()
                     if re.search(pattern, log_text, re.IGNORECASE))
    return {"diagnostics": sorted(diagnostics, key=lambda x: (x["path"], x["code"], x["project"] or "")),
            "unparsedErrorLineDigests": sorted(unmatched), "environmentErrorCategories": markers}


def dotnet_test_directives(original, instance):
    """Use the pinned harness's FQN branch for C# rows that omit its undocumented marker."""
    if instance.get("repo") in SPECIAL_FULL_SUITE_REPOS:
        return original(instance)
    marked = dict(instance)
    marked["dotnet"] = True
    directives = original(marked)
    expected = list(instance.get("FAIL_TO_PASS", [])) + list(instance.get("PASS_TO_PASS", []))
    rendered = " ".join(directives)
    if not expected or any(f"FullyQualifiedName~{name}" not in rendered for name in expected):
        raise ValueError("pinned C# evaluator did not render every declared fully-qualified test name")
    return directives


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


def inspect_created_constraints(container, label):
    """Read back daemon-accepted settings while the container is still stopped."""
    container.reload()
    attributes = container.attrs
    config = attributes.get("Config", {})
    host = attributes.get("HostConfig", {})
    labels = config.get("Labels") or {}
    cap_drop = {str(value).upper() for value in (host.get("CapDrop") or [])}
    security = {str(value).lower().removesuffix(":true") for value in (host.get("SecurityOpt") or [])}
    nano_cpus = host.get("NanoCpus") or 0
    quota = host.get("CpuQuota") or 0
    period = host.get("CpuPeriod") or 0
    cpu_enforced = nano_cpus == NANO_CPUS or (quota > 0 and period > 0 and quota == 2 * period)
    checks = {
        "label": labels.get(LABEL_KEY) == label,
        "networkMode": host.get("NetworkMode") == "none",
        "memory": host.get("Memory") == MEMORY_BYTES,
        "cpus": cpu_enforced,
        "pids": host.get("PidsLimit") == PIDS_LIMIT,
        # Podman expands ALL to its default capability set in inspect. The
        # post-start CapEff=0 check below is authoritative.
        "capDropConfigured": bool(cap_drop),
        "noNewPrivileges": "no-new-privileges" in security,
        "notPrivileged": host.get("Privileged") is False,
        "noBinds": not host.get("Binds"),
        "noDevices": not host.get("Devices"),
        "noMounts": not attributes.get("Mounts"),
    }
    return {
        "enforced": all(checks.values()), "checks": checks,
        "observed": {"networkMode": host.get("NetworkMode"), "networkDisabled": config.get("NetworkDisabled"),
                     "memory": host.get("Memory"), "nanoCpus": nano_cpus,
                     "cpuQuota": quota, "cpuPeriod": period, "pids": host.get("PidsLimit"),
                     "capDrop": sorted(cap_drop), "securityOpt": sorted(security),
                     "privileged": host.get("Privileged"), "bindCount": len(host.get("Binds") or []),
                     "deviceCount": len(host.get("Devices") or []),
                     "mountCount": len(attributes.get("Mounts") or [])},
    }


def _read_container_file(container, path):
    result = container.exec_run(["cat", path])
    exit_code = result.exit_code if hasattr(result, "exit_code") else result[0]
    output = result.output if hasattr(result, "output") else result[1]
    if exit_code != 0:
        raise RuntimeError(f"could not read {path} inside evaluator container")
    return output.decode().strip()


def inspect_runtime_constraints(container, created):
    """Verify cgroup limits after start and before the candidate patch is applied."""
    container.reload()
    memory = _read_container_file(container, "/sys/fs/cgroup/memory.max")
    pids = _read_container_file(container, "/sys/fs/cgroup/pids.max")
    cpu = _read_container_file(container, "/sys/fs/cgroup/cpu.max")
    status = _read_container_file(container, "/proc/self/status")
    interfaces_result = container.exec_run(["ls", "-1", "/sys/class/net"])
    interfaces_exit = (interfaces_result.exit_code if hasattr(interfaces_result, "exit_code")
                       else interfaces_result[0])
    interfaces_output = (interfaces_result.output if hasattr(interfaces_result, "output")
                         else interfaces_result[1])
    if interfaces_exit != 0:
        raise RuntimeError("could not inspect evaluator container network interfaces")
    interfaces = sorted(interfaces_output.decode().split())
    status_fields = {}
    for line in status.splitlines():
        if ":" in line:
            key, value = line.split(":", 1)
            status_fields[key] = value.strip()
    networks = (container.attrs.get("NetworkSettings", {}).get("Networks") or {})
    cpu_parts = cpu.split()
    cpu_enforced = (len(cpu_parts) == 2 and cpu_parts[0].isdigit() and cpu_parts[1].isdigit()
                    and int(cpu_parts[0]) == 2 * int(cpu_parts[1]))
    checks = {"createdSettings": created.get("enforced") is True,
              "memoryCgroup": memory == str(MEMORY_BYTES), "pidsCgroup": pids == str(PIDS_LIMIT),
              "cpuCgroup": cpu_enforced,
              "noNetworks": set(networks).issubset({"none"}) and interfaces == ["lo"],
              "noEffectiveCapabilities": status_fields.get("CapEff") == "0000000000000000",
              "noNewPrivileges": status_fields.get("NoNewPrivs") == "1"}
    return {"enforced": all(checks.values()), "checks": checks,
            "observed": {"memoryMax": memory, "pidsMax": pids, "cpuMax": cpu,
                         "networkNames": sorted(networks), "interfaces": interfaces,
                         "capEff": status_fields.get("CapEff"),
                         "noNewPrivs": status_fields.get("NoNewPrivs")}}


def start_with_attestation(container, original_start, args, kwargs, label, environment, save):
    """Start only after create checks, then attest runtime state before returning to the evaluator."""
    container.reload()
    labels = container.attrs.get("Config", {}).get("Labels") or {}
    if labels.get(LABEL_KEY) != label:
        return original_start(container, *args, **kwargs)
    result = original_start(container, *args, **kwargs)
    key = environment.get("containerInstances", {}).get(container.id, container.id)
    try:
        runtime = inspect_runtime_constraints(
            container, environment.get("createdConstraints", {}).get(container.id, {}))
        environment["runtimeConstraints"][key] = runtime
        save()
    except Exception as error:
        environment["runtimeConstraints"][key] = {
            "enforced": False, "inspectionError": type(error).__name__}
        save()
        raise
    if not runtime["enforced"]:
        raise RuntimeError("running evaluator container did not retain every benchmark constraint")
    return result


def main():
    import docker
    import swe_sharp_bench.cli as cli
    import swe_sharp_bench.create_scripts as create_scripts
    from swe_sharp_bench.grading import get_logs_eval

    source = Path(cli.__file__).parent
    packages = sorted((d.metadata["Name"], d.version) for d in importlib.metadata.distributions())
    hashes = {str(p.relative_to(source)): hashlib.sha256(p.read_bytes()).hexdigest() for p in source.rglob("*.py")}
    trx_helper = Path(__file__).with_name("swe_sharp_trx.py")
    fingerprint = hashlib.sha256(json.dumps({"packages": packages, "source": hashes,
        "wrapper": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "trxHelper": hashlib.sha256(trx_helper.read_bytes()).hexdigest()}, sort_keys=True).encode()).hexdigest()
    label = os.environ.get("FORGE_BENCHMARK_CONTAINER_LABEL", "")
    if not label.startswith("forge-swe-sharp-") or len(label) != 48 or any(c not in "0123456789abcdef" for c in label[16:]):
        raise ValueError("missing or invalid benchmark container label")
    environment = {"fingerprint": fingerprint, "images": {}, "network": "disabled", "memoryLimit": "8g",
                   "cpus": 2, "pids": 512, "containerLabel": label, "containerIds": [],
                   "createdConstraints": {}, "runtimeConstraints": {}, "containerInstances": {},
                   "limitations": ["writable container layer size is not separately quota-verified in this pilot"]}

    def save():
        save_json("environment.json", environment)

    original_create = docker.models.containers.ContainerCollection.create
    original_start = docker.models.containers.Container.start

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
        kwargs.update(network_disabled=True, network_mode="none", mem_limit="8g", nano_cpus=NANO_CPUS,
                      pids_limit=PIDS_LIMIT, cap_drop=["ALL"], security_opt=["no-new-privileges"], labels=labels)
        container = original_create(self, image, **kwargs)
        environment["containerIds"].append(container.id)
        save()
        try:
            observed = inspect_created_constraints(container, label)
            environment["createdConstraints"][container.id] = observed
            save()
        except Exception as error:
            environment["createdConstraints"][container.id] = {
                "enforced": False, "inspectionError": type(error).__name__}
            save()
            raise
        if not observed["enforced"]:
            raise RuntimeError("container runtime did not enforce every requested benchmark constraint")
        # ContainerCollection.run starts only after create returns.
        return container

    def restricted_start(self, *args, **kwargs):
        return start_with_attestation(
            self, original_start, args, kwargs, label, environment, save)

    original_build = cli.build_container
    execution_marker = label + "-execution"

    def bound_build(test_spec, *args, **kwargs):
        mark_execution(test_spec, execution_marker)
        container = original_build(test_spec, *args, **kwargs)
        environment["containerInstances"][container.id] = test_spec.instance_id
        environment["images"][test_spec.instance_id] = container.image.id
        save()
        return container

    original_report = cli.get_eval_report
    original_test_directives = create_scripts.get_test_directives

    def observed_report(test_spec, prediction, test_log_path, include_tests_status):
        report = original_report(test_spec, prediction, test_log_path, include_tests_status)
        _, found = get_logs_eval(test_spec, str(test_log_path))
        log_text = execution_output(Path(test_log_path).read_text(errors="replace"), execution_marker)
        independent = inspect_trx(log_text)
        ident = prediction["instance_id"]
        expected = set(test_spec.FAIL_TO_PASS) | set(test_spec.PASS_TO_PASS)
        raw_statuses = independent["statuses"]
        report[ident]["forge_pristine_build_succeeded"] = pristine_build_succeeded(log_text)
        report[ident]["forge_observed_tests"] = {
            test: raw_statuses[test] for test in expected if test in raw_statuses}
        report[ident]["forge_log_parse_success"] = bool(found and independent["valid"])
        report[ident]["forge_independent_trx_valid"] = independent["valid"]
        report[ident]["forge_all_observed_status_counts"] = independent["counts"]
        report[ident]["forge_compiler_evidence"] = compiler_evidence(log_text)
        return report

    docker.models.containers.ContainerCollection.create = restricted_create
    docker.models.containers.Container.start = restricted_start
    cli.build_container = bound_build
    cli.get_eval_report = observed_report
    create_scripts.get_test_directives = lambda instance: dotnet_test_directives(original_test_directives, instance)
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
