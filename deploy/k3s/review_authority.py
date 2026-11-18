"""Additive, restart-safe SQL authority prerequisite for ordinary immutable releases."""
import hashlib
import json


def assert_previous_initializer_stopped(kube, journal):
    name = (journal or {}).get("data", {}).get("authorityJob")
    if not name:
        return
    observed = kube.get("jobs", name)
    if observed is not None and not any(item.get("type") in ("Complete", "Failed") and item.get("status") == "True"
                                        for item in observed.get("status", {}).get("conditions", [])):
        raise RuntimeError("prior authority initializer is still running; resume its operation before another release")
    if any(pod.get("status", {}).get("phase") not in ("Succeeded", "Failed")
           or any("running" in state.get("state", {}) for state in pod.get("status", {}).get("containerStatuses", []))
           for pod in kube.selected_pods("job-name=" + name)):
        raise RuntimeError("prior authority initializer pod has not stopped")


def desired_job(release):
    from manifests import job
    result = job(release.kube.namespace, release.image, release.dataset, release.operation,
                 "forge-authority-" + release.operation, initialize=True)
    container = result["spec"]["template"]["spec"]["containers"][0]
    container["env"] = [entry for entry in container["env"] if entry["name"] != "Db__AutoMigrate"]
    container["env"] += [{"name": "Db__AutoMigrate", "value": "false"},
                         {"name": "Bootstrap__ReviewAuthorityOnly", "value": "true"}]
    result["spec"].update(parallelism=1, completions=1)
    result["metadata"]["labels"]["app"] = "forge-initialize"
    digest = hashlib.sha256(json.dumps(result, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    result["metadata"]["annotations"] = {"forge/authority-inputs": digest}
    return result


def ensure_review_authority(release):
    from schema_release import contains
    desired = desired_job(release)
    name = desired["metadata"]["name"]
    release.owned()
    existing = release.kube.get("jobs", name)
    if existing is None:
        release.journal["data"]["authorityJob"] = name
        release.record("authority-installing")
        existing = release.kube.create("jobs", desired)
    if existing.get("metadata", {}).get("ownerReferences") or not contains(existing, desired):
        raise RuntimeError("authority initializer differs from immutable release inputs")

    def completed():
        observed = release.kube.get("jobs", name)
        if observed is None or observed.get("metadata", {}).get("ownerReferences") or not contains(observed, desired):
            raise RuntimeError("authority initializer disappeared or changed")
        conditions = observed.get("status", {}).get("conditions", [])
        if any(item.get("type") == "Failed" and item.get("status") == "True" for item in conditions):
            raise RuntimeError("authority initialization failed; candidate activation prohibited")
        complete = any(item.get("type") == "Complete" and item.get("status") == "True" for item in conditions)
        stopped = all(pod.get("status", {}).get("phase") in ("Succeeded", "Failed")
                      and not any("running" in state.get("state", {}) for state in pod.get("status", {}).get("containerStatuses", []))
                      for pod in release.kube.selected_pods("job-name=" + name))
        return complete and stopped

    release.wait(completed, "authority initializer has not completed; candidate activation prohibited")
    release.journal["data"]["authorityConfirmed"] = "true"
    release.record("authority-installed")
