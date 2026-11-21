"""Existing release supervisor provisions one project, without publishing its registry row."""
import argparse
import time
import re
from release import Kube, validate_image
from manifests import job
from schema_release import contains


def initializer(namespace, image, dataset, project, operation):
    if not re.fullmatch(r"[a-z0-9][a-z0-9_-]{0,31}", project):
        raise ValueError("Project must be a canonical lowercase slug")
    result = job(namespace, validate_image(image), dataset, operation, operation + "-init", initialize=True)
    container = result["spec"]["template"]["spec"]["containers"][0]
    container["env"] = [item for item in container["env"] if item["name"] not in ("Bootstrap__ProjectId", "Bootstrap__WorkerPrincipal", "Db__AutoMigrate")]
    container["env"] += [{"name": "Bootstrap__ProjectId", "value": project},
                         {"name": "Bootstrap__ProjectProvisionOnly", "value": "true"},
                         {"name": "Db__AutoMigrate", "value": "false"}]
    # Only SQL initializer credentials and immutable configuration are needed.
    # No controller bearer token, state PVC or App credentials are mounted.
    pod = result["spec"]["template"]["spec"]
    pod["volumes"] = [v for v in pod["volumes"] if v["name"] == "config"]
    container["volumeMounts"] = [v for v in container["volumeMounts"] if v["name"] == "config"]
    return result


def run(kube, desired, timeout=840):
    name = desired["metadata"]["name"]
    existing = kube.get("jobs", name)
    if existing is None:
        existing = kube.create("jobs", desired)
    if existing.get("metadata", {}).get("ownerReferences") or not contains(existing, desired):
        raise RuntimeError("Project initializer differs from immutable request")
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        current = kube.get("jobs", name)
        if current is None or current.get("metadata", {}).get("ownerReferences") or not contains(current, desired):
            raise RuntimeError("Project initializer disappeared or changed")
        stopped = all(p.get("status", {}).get("phase") in ("Succeeded", "Failed")
                      and not any("running" in c.get("state", {}) for c in p.get("status", {}).get("containerStatuses", []))
                      for p in kube.selected_pods("job-name=" + name))
        conditions = current.get("status", {}).get("conditions", [])
        if stopped:
            if any(c.get("type") == "Failed" and c.get("status") == "True" for c in conditions):
                raise RuntimeError("Project initializer failed; inspect its retained diagnostics before retry")
            if any(c.get("type") == "Complete" and c.get("status") == "True" for c in conditions):
                return
        time.sleep(1)
    raise RuntimeError("Project initializer has not finished; existing projects remain unchanged")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for key in ("namespace", "image", "dataset", "project", "operation"):
        parser.add_argument("--" + key, required=True)
    args = parser.parse_args()
    run(Kube(args.namespace), initializer(args.namespace, args.image, args.dataset, args.project, args.operation))
