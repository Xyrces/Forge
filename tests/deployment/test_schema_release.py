import copy
import unittest
from test_k3s_release import FakeKube, FakeControllers, IMAGE, OLD_IMAGE, DATASET
from release import Release
from schema_release import SchemaRelease, choose_release, validate_policy

POLICY = {"fromVersion": 39, "toVersion": 40, "rollbackImage": OLD_IMAGE}

class Kube(FakeKube):
    def __init__(self):
        super().__init__()
        self.version = 39
        self.migration = "Complete"
        self.workers = []
        self.initializers = []
        self.extra_controllers = []
        self.lose_lock_on_job = False

    def create(self, kind, body):
        result = super().create(kind, body)
        if kind == "jobs":
            self.events.append(("migrate", body["metadata"]["name"]))
            if self.lose_lock_on_job:
                self.objects[("configmaps", "forge-release-lock")]["data"]["uid"] = "stolen"
            if self.migration:
                self.objects[(kind, body["metadata"]["name"])]["status"] = {"conditions": [{"type": self.migration, "status": "True"}]}
                if self.migration == "Complete": self.version = 40
            return self.get(kind, body["metadata"]["name"])
        return result

    def selected_pods(self, selector):
        if selector == "app=forge-controller":
            return self.pods("blue") + self.pods("green") + self.extra_controllers
        if selector == "app.kubernetes.io/name=forge-worker": return self.workers
        if selector.startswith("job-name="): return self.initializers
        raise AssertionError("unexpected selector")

class Controllers(FakeControllers):
    def call(self, slot, action, operation, expected_previous_owner=None):
        result = super().call(slot, action, operation, expected_previous_owner)
        if action == "status":
            result.update(schemaVersion=self.kube.version, datasetId=DATASET)
        return result

class Crash(BaseException): pass

class SchemaReleaseTests(unittest.TestCase):
    def make(self, kube=None, controllers=None, uid="uid-1"):
        kube = kube or Kube()
        controllers = controllers or Controllers(kube)
        timer = iter(range(100000))
        release = SchemaRelease(kube, controllers, "upgrade", IMAGE, DATASET, "pod-" + uid, uid,
            timeout=3, sleep=lambda _: None, clock=lambda: next(timer), policy=POLICY)
        return release, kube, controllers

    def test_stop_migrate_qualify_activate_route_order(self):
        release, kube, _ = self.make()
        release.run()
        events = kube.events
        self.assertLess(events.index(("scale", "blue", 0)), events.index(("migrate", "forge-migrate-upgrade")))
        self.assertLess(events.index(("migrate", "forge-migrate-upgrade")), events.index(("create", "forge-green")))
        self.assertLess(events.index(("activate", "green")), events.index(("route", "green")))
        self.assertEqual("committed", release.journal["data"]["phase"])
        initializer = kube.get("jobs", release.migration_name)["spec"]["template"]["spec"]
        self.assertFalse(initializer["automountServiceAccountToken"])
        self.assertEqual("forge-initialize", initializer["serviceAccountName"])
        env = initializer["containers"][0]["env"]
        secret = next(e for e in env if e["name"] == "Db__ConnectionString")
        self.assertEqual("forge-initialize-runtime", secret["valueFrom"]["secretKeyRef"]["name"])
        self.assertEqual("40", next(e for e in env if e["name"] == "Bootstrap__MigrationToVersion")["value"])

    def test_restart_at_each_durable_phase(self):
        for phase in ("draining", "drained", "migration-stopped", "migrating", "migrated", "qualified", "activated"):
            with self.subTest(phase=phase):
                release, kube, controllers = self.make()
                original = release.record
                def crash(recorded):
                    original(recorded)
                    if recorded == phase: raise Crash()
                release.record = crash
                with self.assertRaises(Crash): release.run()
                replacement, _, _ = self.make(kube, controllers, "uid-2")
                replacement.run()
                self.assertEqual("committed", replacement.journal["data"]["phase"])
                self.assertEqual(1, sum(e[0] == "migrate" for e in kube.events))

    def test_initializer_timeout_never_restarts_writers_and_resumes_same_job(self):
        release, kube, controllers = self.make()
        kube.migration = None
        kube.initializers = [{"status": {"phase": "Running"}}]
        # Pods are created only after the Job, not before it.
        kube.initializers = []
        with self.assertRaises(TimeoutError): release.run()
        self.assertEqual("migrating", release.journal["data"]["phase"])
        self.assertFalse(any(e[0] == "activate" for e in kube.events))
        existing = kube.objects[("jobs", release.migration_name)]
        existing["status"] = {"conditions": [{"type": "Complete", "status": "True"}]}
        kube.version = 40
        replacement, _, _ = self.make(kube, controllers, "uid-2")
        replacement.run()
        self.assertEqual(1, sum(e[0] == "migrate" for e in kube.events))

    def test_failed_initializer_waits_for_nonexecuting_pods_before_bridge_rollback(self):
        release, kube, controllers = self.make()
        kube.migration = "Failed"
        original = kube.create
        def create(kind, body):
            result = original(kind, body)
            if kind == "jobs": kube.initializers = [{"status": {"phase": "Running"}}]
            return result
        kube.create = create
        with self.assertRaises(TimeoutError): release.run()
        self.assertFalse(any(e[0] == "activate" for e in kube.events))
        kube.initializers = [{"status": {"phase": "Failed"}}]
        replacement, _, _ = self.make(kube, controllers, "uid-2")
        with self.assertRaisesRegex(RuntimeError, "initializer failed"):
            replacement.run()
        self.assertEqual("rolled-back", replacement.journal["data"]["phase"])
        self.assertEqual(OLD_IMAGE, kube.get("deployments", "forge-blue")["spec"]["template"]["spec"]["containers"][0]["image"])

    def test_candidate_failure_restores_bridge_without_downgrade(self):
        release, kube, controllers = self.make()
        controllers.fail_activate.add("green")
        with self.assertRaisesRegex(RuntimeError, "activation failed"): release.run()
        self.assertEqual(40, kube.version)
        self.assertEqual("rolled-back", release.journal["data"]["phase"])
        self.assertEqual("blue", kube.get("services", "forge")["spec"]["selector"]["slot"])

    def test_worker_and_terminating_controller_prevent_initializer(self):
        for kind in ("worker", "controller"):
            with self.subTest(kind=kind):
                release, kube, _ = self.make()
                if kind == "worker": kube.workers = [{"status": {"phase": "Running"}}]
                else: kube.terminating.add("blue")
                with self.assertRaises(TimeoutError): release.run()
                self.assertIsNone(kube.get("jobs", release.migration_name))
                self.assertFalse(any(e[0] == "activate" for e in kube.events))

    def test_wrong_bridge_or_source_schema_has_no_deployment_mutations(self):
        for kind in ("image", "schema"):
            release, kube, _ = self.make()
            if kind == "image": release.policy["rollbackImage"] = IMAGE
            else: kube.version = 38
            kube.events.clear()
            with self.assertRaises(RuntimeError): release.run()
            self.assertEqual([], kube.events)

    def test_changed_initializer_inputs_fail_closed_on_resume(self):
        release, kube, controllers = self.make()
        kube.migration = None
        with self.assertRaises(TimeoutError): release.run()
        kube.objects[("jobs", release.migration_name)]["spec"]["template"]["spec"]["containers"][0]["image"] = OLD_IMAGE
        replacement, _, _ = self.make(kube, controllers, "uid-2")
        with self.assertRaisesRegex(RuntimeError, "immutable upgrade inputs"): replacement.run()
        self.assertFalse(any(e[0] == "activate" for e in kube.events))

    def test_initializer_adoption_rejects_controller_ownership_or_parallelism_change(self):
        for change in ("owner", "parallelism"):
            with self.subTest(change=change):
                release, kube, controllers = self.make()
                kube.migration = None
                with self.assertRaises(TimeoutError): release.run()
                existing = kube.objects[("jobs", release.migration_name)]
                if change == "owner": existing["metadata"]["ownerReferences"] = [{"kind": "Pod", "name": "controller"}]
                else: existing["spec"]["parallelism"] = 2
                replacement, _, _ = self.make(kube, controllers, "uid-2")
                with self.assertRaisesRegex(RuntimeError, "immutable upgrade inputs"): replacement.run()
                self.assertFalse(any(e[0] == "activate" for e in kube.events))

    def test_lost_lock_after_job_creation_cannot_activate_or_rollback(self):
        release, kube, _ = self.make()
        kube.lose_lock_on_job = True
        with self.assertRaisesRegex(RuntimeError, "ownership lost"): release.run()
        self.assertFalse(any(e[0] == "activate" for e in kube.events))

    def test_selection_uses_journal_or_verified_version_and_rejects_unknown(self):
        _, kube, controllers = self.make()
        self.assertIs(SchemaRelease, choose_release(kube, controllers, "upgrade", POLICY)[0])
        kube.version = 40
        self.assertIs(Release, choose_release(kube, controllers, "upgrade", POLICY)[0])
        for bad in (None, 38, "39", True):
            kube.version = bad
            with self.assertRaises(RuntimeError): choose_release(kube, controllers, "upgrade", POLICY)
        kube.create("configmaps", {"metadata": {"name": "forge-release-upgrade"}, "data": {"kind": "schema-upgrade"}})
        self.assertIs(SchemaRelease, choose_release(kube, controllers, "upgrade", POLICY)[0])
        with self.assertRaises(RuntimeError): choose_release(kube, controllers, "upgrade", None)

    def test_policy_rejects_unknown_fields_and_nonimmutable_bridge(self):
        for policy in ({**POLICY, "extra": True}, {**POLICY, "rollbackImage": "latest"}, {**POLICY, "toVersion": 39}, {**POLICY, "fromVersion": True}):
            with self.assertRaises(ValueError): validate_policy(policy)
