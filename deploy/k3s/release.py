#!/usr/bin/env python3
"""Independent, restartable blue/green release Job. No Forge scheduler in this process."""
import argparse
import json
import os
import re
import ssl
import time
import urllib.error
import urllib.request


class ApiError(RuntimeError):
    def __init__(self, code, operation):
        super().__init__(f"{operation}: HTTP {code}")  # Never echo response bodies/secrets.
        self.code = code


class Kube:
    def __init__(self, namespace, server=None, token_file=None, ca_file=None):
        self.namespace = namespace
        self.server = server or "https://kubernetes.default.svc"
        directory = "/var/run/secrets/kubernetes.io/serviceaccount/"
        self.token_file = token_file or directory + "token"
        self.context = ssl.create_default_context(cafile=ca_file or directory + "ca.crt")

    def call(self, method, path, body=None, patch=False):
        with open(self.token_file, encoding="utf-8") as source:
            token = source.read().strip()
        headers = {"Authorization": "Bearer " + token,
                   "Content-Type": "application/merge-patch+json" if patch else "application/json"}
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(self.server + path, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, context=self.context, timeout=30) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            raise ApiError(error.code, method + " " + path) from None

    def path(self, kind, name=""):
        group = "/apis/apps/v1" if kind == "deployments" else "/apis/batch/v1" if kind == "jobs" else "/api/v1"
        return f"{group}/namespaces/{self.namespace}/{kind}" + ("/" + name if name else "")

    def get(self, kind, name):
        try:
            return self.call("GET", self.path(kind, name))
        except ApiError as error:
            if error.code == 404:
                return None
            raise

    def create(self, kind, body):
        return self.call("POST", self.path(kind), body)

    def patch(self, kind, name, body):
        return self.call("PATCH", self.path(kind, name), body, patch=True)

    def pods(self, slot):
        path = self.path("pods") + "?labelSelector=app%3Dforge-controller%2Cslot%3D" + slot
        return self.call("GET", path)["items"]

    def selected_pods(self, selector):
        from urllib.parse import quote
        return self.call("GET", self.path("pods") + "?labelSelector=" + quote(selector))["items"]


class Controllers:
    def __init__(self, namespace, token_file):
        self.namespace = namespace
        self.token_file = token_file

    def call(self, slot, action, operation, expected_previous_owner=None):
        with open(self.token_file, encoding="utf-8") as source:
            token = source.read().strip()
        url = f"http://forge-{slot}.{self.namespace}.svc:9090/api/controller/{action}"
        payload = {"operationId": operation}
        if action == "activate":
            payload["expectedPreviousOwner"] = expected_previous_owner
        body = None if action == "status" else json.dumps(payload).encode()
        request = urllib.request.Request(url, data=body, headers={
            "Authorization": "Bearer " + token, "Content-Type": "application/json"},
            method="GET" if body is None else "POST")
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            raise ApiError(error.code, "controller " + action) from None

    def verify_dashboard(self, source_sha):
        """Verify the routed target, including its served revision; not a browser test."""
        base = f"http://forge.{self.namespace}.svc:8080"
        class NoRedirect(urllib.request.HTTPRedirectHandler):
            def redirect_request(self, request, response, code, message, headers, url):
                return None
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        try:
            with opener.open(base + "/", timeout=15) as response:
                html = response.read(2 * 1024 * 1024).decode("utf-8")
                if response.status != 200 or "<html" not in html.lower():
                    return False
            with opener.open(base + "/api/meta/buildinfo", timeout=15) as response:
                revision = json.load(response).get("sourceRevision")
                return bool(re.fullmatch(r"[0-9a-f]{40}", revision or "")) and (source_sha is None or revision == source_sha)
        except (OSError, ValueError):
            return False


def validate_image(image):
    if not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9._:/-]*@sha256:[0-9a-f]{64}", image):
        raise ValueError("image must be an immutable registry reference @sha256:<64 lowercase hex>")
    return image


def deployment(slot, image, dataset, operation, namespace="forge"):
    labels = {"app": "forge-controller", "slot": slot}
    env = [{"name": key, "value": value} for key, value in {
        "Controller__Enabled": "true", "Controller__Mode": "standby",
        "Controller__DatasetId": dataset, "Controller__TokenFile": "/run/forge-control/controller-token",
        "Controller__KeyringPath": "/var/lib/forge/keyring",
        "Db__Provider": "sqlserver", "Db__AutoMigrate": "false", "Forgesystem__DataRoot": "/var/lib/forge",
        "Worker__Enabled": "true", "Worker__Image": image, "Worker__Namespace": namespace,
        "HOME": "/var/lib/forge", "Dashboard__Hostname": "0.0.0.0", "Dashboard__Port": "8080",
    }.items()]
    env += [{"name": "GitHub__WebhookSecretFile", "valueFrom": {"secretKeyRef": {
                "name": "forge-github-events", "key": "path", "optional": True}}}]
    env += [{"name": "FORGE_GITHUB_APPS_FILE", "valueFrom": {"secretKeyRef": {
                "name": "forge-github-apps", "key": "config-path", "optional": True}}}]
    env += [{"name": "Controller__InstanceId", "valueFrom": {"fieldRef": {"fieldPath": "metadata.uid"}}},
            {"name": "Db__ConnectionString", "valueFrom": {"secretKeyRef": {
                "name": "forge-runtime", "key": "sql-connection-string"}}}]
    return {"apiVersion": "apps/v1", "kind": "Deployment", "metadata": {
        "name": "forge-" + slot, "labels": labels, "annotations": {"forge/release-operation": operation}},
        "spec": {"replicas": 1, "strategy": {"type": "Recreate"}, "selector": {"matchLabels": labels},
                 "template": {"metadata": {"labels": labels}, "spec": {
                     "serviceAccountName": "forge-controller", "terminationGracePeriodSeconds": 120,
                     "securityContext": {"runAsUser": 10001, "runAsGroup": 10001, "fsGroup": 10001,
                                         "runAsNonRoot": True, "seccompProfile": {"type": "RuntimeDefault"}},
                     "containers": [{"name": "forge", "image": validate_image(image), "imagePullPolicy": "IfNotPresent",
                         "env": env, "ports": [{"containerPort": 8080}, {"containerPort": 9090}],
                         "securityContext": {"allowPrivilegeEscalation": False, "capabilities": {"drop": ["ALL"]}},
                         "resources": {"requests": {"cpu": "250m", "memory": "512Mi"},
                                       "limits": {"cpu": "2", "memory": "4Gi"}},
                         "readinessProbe": {"httpGet": {"path": "/healthz", "port": 9090}, "periodSeconds": 5},
                         "volumeMounts": [{"name": "state", "mountPath": "/var/lib/forge"},
                             {"name": "control-token", "mountPath": "/run/forge-control", "readOnly": True},
                             {"name": "github-apps", "mountPath": "/run/forge-github-apps", "readOnly": True},
                             {"name": "github-events", "mountPath": "/run/forge-github-events", "readOnly": True},
                             {"name": "config", "mountPath": "/app/appsettings.json", "subPath": "appsettings.json", "readOnly": True}]}],
                     "volumes": [{"name": "state", "persistentVolumeClaim": {"claimName": "forge-state"}},
                         {"name": "control-token", "secret": {"secretName": "forge-control", "defaultMode": 288}},
                         {"name": "github-apps", "secret": {"secretName": "forge-github-apps", "defaultMode": 288, "optional": True}},
                         {"name": "github-events", "secret": {"secretName": "forge-github-events", "defaultMode": 288, "optional": True}},
                         {"name": "config", "secret": {"secretName": "forge-config", "defaultMode": 288}}]}}}}


class Release:
    def __init__(self, kube, controllers, operation, image, dataset, pod_name, pod_uid,
                 timeout=600, sleep=time.sleep, clock=time.monotonic, environment=None, source_sha=None):
        if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,40}", operation):
            raise ValueError("invalid operation id")
        self.kube, self.controllers = kube, controllers
        self.operation, self.image, self.dataset = operation, validate_image(image), dataset
        self.pod_name, self.pod_uid = pod_name, pod_uid
        self.timeout, self.sleep, self.clock = timeout, sleep, clock
        self.journal_name = "forge-release-" + operation
        self.journal = None
        self.environment, self.source_sha = environment, source_sha
        if environment is not None and (not re.fullmatch(r"[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?", environment)
                                        or not re.fullmatch(r"[0-9a-f]{40}", source_sha or "")):
            raise ValueError("named release requires a valid environment and source SHA")

    def verify_target(self):
        if self.environment is None:
            return
        identity = self.kube.get("configmaps", "forge-dataset")
        if not identity or identity.get("data", {}).get("id") != self.dataset:
            raise RuntimeError("target namespace is not provisioned for the approved dataset")

    def verify_route(self, slot, rollback=False):
        if self.environment is None:
            return
        self.record("rolling-back" if rollback else "verifying-target")
        self.wait(lambda: self.status(slot).get("datasetId") == self.dataset and
                  self.controllers.verify_dashboard(None if rollback else self.source_sha),
                  "routed target identity or dashboard revision check timed out")
        self.journal["data"]["rollbackVerified" if rollback else "targetVerified"] = "true"
        if not rollback:
            self.journal["data"].update(environment=self.environment, sourceSha=self.source_sha, namespace=self.kube.namespace)

    def acquire(self):
        lock = self.kube.get("configmaps", "forge-release-lock")
        data = {"pod": self.pod_name, "uid": self.pod_uid, "operation": self.operation}
        if lock:
            owner = self.kube.get("pods", lock["data"]["pod"])
            if owner and owner["metadata"]["uid"] == lock["data"]["uid"] and owner.get("status", {}).get("phase") not in ("Succeeded", "Failed"):
                raise RuntimeError("another release pod still owns the lock; refusing takeover")
            prior_operation = lock["data"].get("operation")
            if prior_operation != self.operation:
                if not prior_operation:
                    raise RuntimeError("release lock omitted operation identity; refusing ambiguous takeover")
                prior = self.kube.get("configmaps", "forge-release-" + prior_operation)
                from review_authority import assert_previous_initializer_stopped
                assert_previous_initializer_stopped(self.kube, prior)
                if prior and prior.get("data", {}).get("kind") == "schema-upgrade" and prior["data"].get("phase") not in ("committed", "rolled-back"):
                    raise RuntimeError("unfinished schema upgrade must be resumed with its original operation id")
            self.kube.patch("configmaps", "forge-release-lock", {
                "metadata": {"resourceVersion": lock["metadata"]["resourceVersion"]}, "data": data})
        else:
            self.kube.create("configmaps", {"apiVersion": "v1", "kind": "ConfigMap",
                "metadata": {"name": "forge-release-lock"}, "data": data})

    def owned(self):
        lock = self.kube.get("configmaps", "forge-release-lock")
        if not lock or any(lock["data"].get(key) != value for key, value in {
                "uid": self.pod_uid, "pod": self.pod_name, "operation": self.operation}.items()):
            raise RuntimeError("release ownership lost")

    def record(self, phase):
        self.owned()
        self.journal["data"]["phase"] = phase
        self.journal = self.kube.patch("configmaps", self.journal_name, {
            "metadata": {"resourceVersion": self.journal["metadata"]["resourceVersion"]},
            "data": self.journal["data"]})
        print(json.dumps({"operation": self.operation, "phase": phase}), flush=True)

    def wait(self, predicate, description):
        deadline = self.clock() + self.timeout
        while True:
            self.owned()
            if predicate():
                return
            if self.clock() >= deadline:
                raise TimeoutError(description)
            self.sleep(2)

    def status(self, slot):
        try:
            return self.controllers.call(slot, "status", self.operation)
        except (ApiError, OSError, ValueError):
            return {}

    def stop(self, slot):
        self.owned()
        if self.kube.get("deployments", "forge-" + slot):
            self.kube.patch("deployments", "forge-" + slot, {"spec": {"replicas": 0}})
        # A terminating pod can still perform side effects. Require deletion, not Ready=false.
        self.wait(lambda: not self.kube.pods(slot), "old controller pods did not terminate")

    def activate(self, slot, rollback=False):
        self.wait(lambda: self.status(slot).get("ready") is True, "controller readiness timed out")
        status = self.status(slot)
        owner = status.get("leaseOwner") or None
        instance = status.get("instanceId")
        if not instance:
            raise RuntimeError("controller status omitted instance identity")
        known = json.loads(self.journal["data"].get("knownOwners", "[]"))
        if owner is not None and owner not in known and owner != instance:
            raise RuntimeError("SQL lease belongs to an unobserved controller; refusing takeover")
        self.owned()
        action_id = self.operation + ("-rollback-" if rollback else "-activate-") + instance
        self.controllers.call(slot, "activate", action_id, expected_previous_owner=owner)
        def activated():
            observed = self.status(slot)
            if observed.get("mode") == "failed":
                raise RuntimeError("controller reported failed activation")
            return observed.get("mode") == "active" and observed.get("ready") is True
        self.wait(activated, "controller activation timed out")

    def route(self, slot):
        self.owned()
        self.kube.patch("services", "forge", {"spec": {"selector": {"app": "forge-controller", "slot": slot}}})

    def rollback(self, candidate, previous):
        if self.journal["data"].get("candidateActivationAttempted") == "true" and self.journal["data"].get("mergeFenceRequired") == "true" and self.journal["data"].get("mergeFenceConfirmed") != "true":
            self.stop(candidate)
            self.route("none")
            self.record("rollback-needs-merge-fence")
            raise RuntimeError("Downgrade blocked: persisted merge fence was not confirmed; operator reconciliation required")
        self.record("rolling-back")
        self.stop(candidate)  # Never reactivate previous while candidate might own side effects.
        if previous:
            if self.status(previous).get("mode") != "active":
                self.stop(previous)
                self.owned()
                self.kube.patch("deployments", "forge-" + previous, {"spec": {"replicas": 1}})
                self.activate(previous, rollback=True)
            self.route(previous)
            self.verify_route(previous, rollback=True)
        else:
            self.route("none")
        self.record("rolled-back")

    def run(self):
        self.verify_target()
        self.acquire()
        self.journal = self.kube.get("configmaps", self.journal_name)
        if self.journal:
            data = self.journal["data"]
            if data.get("kind") == "schema-upgrade":
                raise RuntimeError("schema-upgrade journal requires its schema supervisor; ordinary release is prohibited")
            if data["image"] != self.image or data["dataset"] != self.dataset:
                raise RuntimeError("operation id already used for different immutable inputs")
            if self.environment is not None and (data.get("environment") != self.environment or
                                                data.get("sourceSha") != self.source_sha or data.get("namespace") != self.kube.namespace):
                raise RuntimeError("operation id already used for another environment or source")
            if data["phase"] == "committed":
                return
            if data["phase"] == "rollback-needs-merge-fence":
                raise RuntimeError("Rollback awaits explicit merge-fence reconciliation")
            if data["phase"] == "rolled-back":
                raise RuntimeError("operation rolled back; use a new operation id after correcting failure")
        else:
            current = self.kube.get("services", "forge")["spec"].get("selector", {}).get("slot", "none")
            previous = current if current in ("blue", "green") else ""
            candidate = "green" if previous == "blue" else "blue"
            self.journal = self.kube.create("configmaps", {"apiVersion": "v1", "kind": "ConfigMap",
                "metadata": {"name": self.journal_name}, "data": {"phase": "created", "image": self.image,
                    "dataset": self.dataset, "previous": previous, "candidate": candidate,
                    **({"environment": self.environment, "sourceSha": self.source_sha, "namespace": self.kube.namespace}
                       if self.environment is not None else {})}})
        data = self.journal["data"]
        candidate, previous = data["candidate"], data["previous"]
        if data["phase"] == "rolling-back":
            self.rollback(candidate, previous)
            raise RuntimeError("interrupted rollback completed")
        existing = self.kube.get("deployments", "forge-" + candidate)
        if existing and existing["metadata"].get("annotations", {}).get("forge/release-operation") != self.operation:
            if existing["spec"].get("replicas", 1) != 0 or self.kube.pods(candidate):
                raise RuntimeError("candidate slot contains another live release; no workloads changed")
        try:
            from review_authority import ensure_review_authority
            ensure_review_authority(self)
            data = self.journal["data"]
            desired = deployment(candidate, self.image, self.dataset, self.operation, self.kube.namespace)
            self.owned()
            if not existing:
                self.kube.create("deployments", desired)
            elif existing["metadata"].get("annotations", {}).get("forge/release-operation") != self.operation:
                self.kube.patch("deployments", "forge-" + candidate, desired)
            elif existing["spec"]["template"]["spec"]["containers"][0]["image"] != self.image:
                raise RuntimeError("candidate image changed outside release operation")
            self.wait(lambda: self.status(candidate).get("ready") is True, "standby qualification timed out")
            known = set(json.loads(data.get("knownOwners", "[]")))
            for slot in (candidate, previous):
                if slot:
                    instance = self.status(slot).get("instanceId")
                    if instance:
                        known.add(instance)
            data["knownOwners"] = json.dumps(sorted(known))
            # Persist the old controller's protocol before its status endpoint disappears.
            if previous and self.status(previous).get("reviewHoldProtocolVersion", 0) < 1:
                self.journal["data"]["mergeFenceRequired"] = "true"
            self.record("qualified")
            if previous:
                previous_deploy = self.kube.get("deployments", "forge-" + previous)
                if previous_deploy and previous_deploy["spec"].get("replicas", 1) != 0:
                    self.owned()
                    self.controllers.call(previous, "drain", self.operation)
                    self.wait(lambda: self.status(previous).get("admissionOpen") is False and
                              self.status(previous).get("activeRuns") == 0, "drain timed out")
                self.stop(previous)
            self.record("old-stopped")
            if self.journal["data"].get("mergeFenceRequired") == "true":
                # The old UI can no longer change the stored operator intent.
                # The candidate must confirm the old controller sees a hold
                # before the new controller can accept any human review request.
                self.record("fencing-merge")
                fence = self.controllers.call(candidate, "merge-fence", self.operation)
                if fence.get("held") is not True or fence.get("operationId") != self.operation or fence.get("projects", 0) < 1:
                    raise RuntimeError("Candidate did not confirm a persisted merge fence")
                self.journal["data"]["mergeFenceConfirmed"] = "true"
            self.journal["data"]["candidateActivationAttempted"] = "true"
            self.record("activating")
            self.activate(candidate)
            self.record("activated")
            self.route(candidate)
            self.verify_route(candidate)
        except Exception:
            # If rollback itself fails, retain its phase; a replacement release Job resumes it.
            self.rollback(candidate, previous)
            raise
        # A lost PATCH response may mean committed was already persisted and
        # observed. Retry the journal; never roll an accepted release back merely
        # because its commit acknowledgement was lost.
        self.record("committed")


def main():
    parser = argparse.ArgumentParser()
    for name in ("namespace", "operation", "image", "dataset"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--timeout", type=int, default=600)
    parser.add_argument("--source-sha")
    parser.add_argument("--environment")
    args = parser.parse_args()
    if args.source_sha:
        with open("/app/source-revision", encoding="utf-8") as source:
            if source.read().strip() != args.source_sha:
                raise RuntimeError("release image does not match approved source SHA")
    from schema_release import choose_release, load_policy
    kube = Kube(args.namespace)
    controllers = Controllers(args.namespace, "/run/forge-control/controller-token")
    kind, policy = choose_release(kube, controllers, args.operation,
        load_policy(os.path.join(os.path.dirname(__file__), "schema-upgrade.json")))
    release = kind(kube, controllers, args.operation, args.image, args.dataset,
                   os.environ["POD_NAME"], os.environ["POD_UID"], args.timeout,
                   environment=args.environment, source_sha=args.source_sha,
                   **({"policy": policy} if policy is not None else {}))
    release.run()


if __name__ == "__main__":
    main()
