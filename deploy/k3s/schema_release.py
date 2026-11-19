"""Opt-in additive schema upgrade, supervised by the independent release Job.
A qualified compatibility image is mandatory. No database downgrade is attempted.
"""
import hashlib
import json
import os
from release import Release, deployment, validate_image
from manifests import job


def validate_policy(policy):
    if not isinstance(policy, dict) or set(policy) != {"fromVersion", "toVersion", "rollbackImage"}:
        raise ValueError("schema policy requires exact fromVersion/toVersion/rollbackImage fields")
    if type(policy["fromVersion"]) is not int or type(policy["toVersion"]) is not int or not 0 < policy["fromVersion"] < policy["toVersion"]:
        raise ValueError("schema policy requires increasing positive integer versions")
    validate_image(policy["rollbackImage"])
    return dict(policy)


def load_policy(path):
    if not os.path.exists(path):
        return None
    with open(path, encoding="utf-8") as source:
        return validate_policy(json.load(source))


def choose_release(kube, controllers, operation, policy):
    journal = kube.get("configmaps", "forge-release-" + operation)
    if journal and journal.get("data", {}).get("kind") == "schema-upgrade":
        if policy is None:
            raise RuntimeError("resuming schema upgrade requires its source-bundled policy")
        return SchemaRelease, policy
    if policy is None:
        return Release, None
    current = kube.get("services", "forge")["spec"].get("selector", {}).get("slot")
    if current == "none":
        # The installer has already completed initialization. Ordinary standby
        # readiness verifies this target binary's strict schema before admission.
        return Release, None
    if current not in ("blue", "green"):
        raise RuntimeError("schema policy requires an observed existing controller")
    status = controllers.call(current, "status", operation)
    version = status.get("schemaVersion")
    if type(version) is not int:
        raise RuntimeError("controller omitted verified schema version")
    if version == policy["toVersion"]:
        return Release, None
    if version == policy["fromVersion"]:
        return SchemaRelease, policy
    raise RuntimeError("observed schema is outside the source-bundled upgrade policy")


def contains(actual, expected):
    """Kubernetes adds default fields, but must not alter any immutable input."""
    if isinstance(expected, dict):
        return isinstance(actual, dict) and all(key in actual and contains(actual[key], value) for key, value in expected.items())
    if isinstance(expected, list):
        return isinstance(actual, list) and len(actual) == len(expected) and all(contains(a, b) for a, b in zip(actual, expected))
    return actual == expected


class MigrationFailed(RuntimeError):
    pass


class SchemaRelease(Release):
    def __init__(self, *args, policy, **kwargs):
        super().__init__(*args, **kwargs)
        self.policy = validate_policy(policy)
        self.migration_name = "forge-migrate-" + self.operation

    def migration_job(self):
        desired = job(self.kube.namespace, self.image, self.dataset, self.operation,
                      self.migration_name, initialize=True, migration=self.policy)
        desired["spec"].update(parallelism=1, completions=1)
        desired["metadata"]["labels"]["app"] = "forge-initialize"
        desired["spec"]["template"]["metadata"]["labels"] = {"app": "forge-initialize", "operation": self.operation}
        digest = hashlib.sha256(json.dumps(desired, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
        desired["metadata"]["annotations"] = {"forge/schema-inputs": digest}
        return desired

    def no_writers(self):
        if self.kube.selected_pods("app=forge-controller"):
            return False
        return not any(p.get("metadata", {}).get("deletionTimestamp") or
                       p.get("status", {}).get("phase") not in ("Succeeded", "Failed")
                       for p in self.kube.selected_pods("app.kubernetes.io/name=forge-worker"))

    def initializer_stopped(self):
        pods = self.kube.selected_pods("job-name=" + self.migration_name)
        return all(p.get("status", {}).get("phase") in ("Succeeded", "Failed") and
                   not any("running" in c.get("state", {}) for c in p.get("status", {}).get("containerStatuses", []))
                   for p in pods)

    def await_migration(self):
        desired = self.migration_job()
        existing = self.kube.get("jobs", self.migration_name)
        if existing is None:
            self.owned()
            self.wait(self.no_writers, "writers remain before schema migration")
            self.wait(self.initializer_stopped, "orphan initializer pods remain before migration retry")
            self.owned()
            existing = self.kube.create("jobs", desired)
        if existing.get("metadata", {}).get("ownerReferences") or not contains(existing, desired):
            raise RuntimeError("initializer Job differs from immutable upgrade inputs")
        def terminal():
            observed = self.kube.get("jobs", self.migration_name)
            if observed is None or observed.get("metadata", {}).get("ownerReferences") or not contains(observed, desired):
                raise RuntimeError("initializer Job disappeared or immutable inputs changed")
            conditions = observed.get("status", {}).get("conditions", [])
            failed = any(c.get("type") == "Failed" and c.get("status") == "True" for c in conditions)
            complete = any(c.get("type") == "Complete" and c.get("status") == "True" for c in conditions)
            if (failed or complete) and self.initializer_stopped():
                if failed:
                    raise MigrationFailed("initializer failed; compatibility rollback required")
                return True
            return False
        self.wait(terminal, "initializer still running; resume this schema operation, never restart writers")

    def safe_rollback(self, candidate, previous):
        existing = self.kube.get("jobs", self.migration_name)
        if existing is not None:
            if existing.get("metadata", {}).get("ownerReferences") or not contains(existing, self.migration_job()):
                raise RuntimeError("initializer inputs changed; rollback is prohibited")
            conditions = existing.get("status", {}).get("conditions", [])
            if not any(c.get("type") in ("Failed", "Complete") and c.get("status") == "True" for c in conditions):
                raise RuntimeError("initializer is not terminal; rollback is prohibited")
        self.wait(self.initializer_stopped, "initializer pods still executing; rollback prohibited")
        bridge = self.kube.get("deployments", "forge-" + previous)
        if bridge is None or bridge["spec"]["template"]["spec"]["containers"][0]["image"] != self.policy["rollbackImage"]:
            raise RuntimeError("qualified rollback deployment changed; refusing to start an unknown image")
        super().rollback(candidate, previous)

    def run(self):
        self.verify_target()
        self.acquire()
        self.journal = self.kube.get("configmaps", self.journal_name)
        if self.journal is None:
            current = self.kube.get("services", "forge")["spec"].get("selector", {}).get("slot")
            if current not in ("blue", "green"):
                raise RuntimeError("schema upgrade needs an existing qualified rollback controller")
            previous_deploy = self.kube.get("deployments", "forge-" + current)
            status = self.status(current)
            if previous_deploy is None or previous_deploy["spec"]["template"]["spec"]["containers"][0]["image"] != self.policy["rollbackImage"]:
                raise RuntimeError("previous image is not the policy's qualified rollback bridge")
            if status.get("schemaVersion") != self.policy["fromVersion"] or status.get("datasetId") != self.dataset:
                raise RuntimeError("previous schema or dataset differs from upgrade policy")
            other = "green" if current == "blue" else "blue"
            existing = self.kube.get("deployments", "forge-" + other)
            if existing and (existing["spec"].get("replicas", 1) != 0 or self.kube.pods(other)):
                raise RuntimeError("other controller slot is live before schema upgrade")
            self.owned()
            self.journal = self.kube.create("configmaps", {"apiVersion": "v1", "kind": "ConfigMap",
                "metadata": {"name": self.journal_name}, "data": {"kind": "schema-upgrade", "phase": "created",
                    "image": self.image, "dataset": self.dataset, "previous": current, "candidate": other,
                    "policy": json.dumps(self.policy, sort_keys=True), "knownOwners": json.dumps([status["instanceId"]]),
                    **({"environment": self.environment, "sourceSha": self.source_sha, "namespace": self.kube.namespace}
                       if self.environment is not None else {})}})
        data = self.journal["data"]
        if data.get("kind") != "schema-upgrade" or data["image"] != self.image or data["dataset"] != self.dataset or json.loads(data["policy"]) != self.policy:
            raise RuntimeError("schema operation id already binds different immutable inputs")
        if self.environment is not None and (data.get("environment") != self.environment or
                                            data.get("sourceSha") != self.source_sha or data.get("namespace") != self.kube.namespace):
            raise RuntimeError("schema operation id already binds another environment or source")
        if self.journal["data"]["phase"] == "committed":
            return
        if self.journal["data"]["phase"] == "rolled-back":
            raise RuntimeError("schema operation rolled back; use a new qualified operation")
        if self.journal["data"]["phase"] not in {"created", "draining", "drained", "migration-stopped", "migrating", "migrated", "qualified", "activated", "rolling-back", "verifying-target"}:
            raise RuntimeError("unknown schema upgrade journal phase")
        candidate, previous = data["candidate"], data["previous"]
        bridge = self.kube.get("deployments", "forge-" + previous)
        if bridge is None or bridge["spec"]["template"]["spec"]["containers"][0]["image"] != self.policy["rollbackImage"]:
            raise RuntimeError("qualified previous deployment changed during upgrade")
        if self.journal["data"]["phase"] == "rolling-back":
            self.safe_rollback(candidate, previous)
            raise RuntimeError("interrupted schema rollback completed")
        if self.journal["data"]["phase"] in ("created", "draining"):
            self.record("draining")
            self.owned()
            self.controllers.call(previous, "drain", self.operation)
            def drained():
                status = self.status(previous)
                return status.get("admissionOpen") is False and status.get("activeRuns") == 0 and status.get("leaseOwner") is None
            self.wait(drained, "controller has not drained and released its SQL lease")
            self.record("drained")
        if self.journal["data"]["phase"] in ("drained", "migration-stopped"):
            self.stop(previous)
            self.stop(candidate)
            self.wait(self.no_writers, "controller or worker pods remain before migration")
            self.record("migration-stopped")
            self.record("migrating")
        if self.journal["data"]["phase"] == "migrating":
            # Timeout/transport/ownership failures deliberately leave this resumable
            # phase. Never roll back while an independently running Job may write SQL.
            try:
                self.await_migration()
            except MigrationFailed:
                self.safe_rollback(candidate, previous)
                raise
            self.record("migrated")
        try:
            desired = deployment(candidate, self.image, self.dataset, self.operation, self.kube.namespace)
            existing = self.kube.get("deployments", "forge-" + candidate)
            self.owned()
            if existing is None:
                self.kube.create("deployments", desired)
            elif existing["metadata"].get("annotations", {}).get("forge/release-operation") != self.operation:
                if existing["spec"].get("replicas", 1) != 0 or self.kube.pods(candidate):
                    raise RuntimeError("candidate slot changed after migration")
                self.kube.patch("deployments", "forge-" + candidate, desired)
            elif existing["spec"]["template"]["spec"]["containers"][0]["image"] != self.image:
                raise RuntimeError("candidate image changed outside schema operation")
            elif existing["spec"].get("replicas", 1) == 0:
                self.kube.patch("deployments", "forge-" + candidate, {"spec": {"replicas": 1}})
            self.wait(lambda: self.status(candidate).get("ready") is True and
                      self.status(candidate).get("schemaVersion") == self.policy["toVersion"], "schema target standby not ready")
            known = set(json.loads(data["knownOwners"]))
            known.add(self.status(candidate)["instanceId"])
            self.journal["data"]["knownOwners"] = json.dumps(sorted(known))
            self.record("qualified")
            self.activate(candidate)
            self.record("activated")
            self.route(candidate)
            self.verify_route(candidate)
            self.record("committed")
        except Exception:
            self.safe_rollback(candidate, previous)
            raise
