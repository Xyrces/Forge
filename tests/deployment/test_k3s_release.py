import copy
import importlib.util
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "deploy" / "k3s"))
from release import Release, deployment, validate_image
from manifests import foundation, job

IMAGE = "registry.example/forge@sha256:" + "a" * 64
OLD_IMAGE = "registry.example/forge@sha256:" + "b" * 64
DATASET = "785c251c-2cc7-43c6-980b-a082c121c3d3"


class FakeKube:
    def __init__(self, previous="blue"):
        self.namespace = "forge"
        self.objects = {("services", "forge"): {"metadata": {"resourceVersion": "1"}, "spec": {"selector": {"slot": previous or "none"}}}}
        self.events = []
        self.states = {}
        self.terminating = set()
        if previous:
            self.create("deployments", deployment(previous, OLD_IMAGE, DATASET, "old"))
            self.states[previous] = "active"
        self.events.clear()

    def get(self, kind, name):
        return copy.deepcopy(self.objects.get((kind, name)))

    def create(self, kind, body):
        value = copy.deepcopy(body)
        name = value["metadata"]["name"]
        if (kind, name) in self.objects:
            raise RuntimeError("already exists")
        value["metadata"]["resourceVersion"] = "1"
        self.objects[(kind, name)] = value
        if kind == "deployments":
            self.states[name.removeprefix("forge-")] = "standby"
            self.events.append(("create", name))
        if kind == "jobs" and name.startswith("forge-authority-"):
            value["status"] = {"conditions": [{"type": "Complete", "status": "True"}]}
            self.events.append(("authority", name))
        return copy.deepcopy(value)

    def patch(self, kind, name, body):
        target = self.objects[(kind, name)]
        expected = body.get("metadata", {}).get("resourceVersion")
        if expected and expected != target["metadata"]["resourceVersion"]:
            raise RuntimeError("conflict")
        def merge(left, right):
            for key, value in right.items():
                if isinstance(value, dict):
                    merge(left.setdefault(key, {}), value)
                else:
                    left[key] = copy.deepcopy(value)
        merge(target, body)
        target.setdefault("metadata", {})["resourceVersion"] = str(int(target["metadata"].get("resourceVersion", "0")) + 1)
        if kind == "deployments":
            replicas = body.get("spec", {}).get("replicas")
            slot = name.removeprefix("forge-")
            if replicas is not None:
                self.events.append(("scale", slot, replicas))
                if not replicas:
                    self.states[slot] = "stopped"
                elif self.states.get(slot) == "stopped":
                    self.states[slot] = "standby"
        elif kind == "services":
            self.events.append(("route", body["spec"]["selector"]["slot"]))
        return copy.deepcopy(target)

    def pods(self, slot):
        if slot in self.terminating:
            return [{"metadata": {"deletionTimestamp": "now"}}]
        deployment = self.get("deployments", "forge-" + slot)
        return [{}] if deployment and deployment["spec"]["replicas"] else []

    def selected_pods(self, selector):
        return []


class FakeControllers:
    def __init__(self, kube):
        self.kube = kube
        self.fail_activate = set()
        self.failed_after_activate = set()
        self.not_ready = set()
        self.busy = False
        self.lease_owner = None
        self.activations = []
        self.legacy_slots = set()
        self.fail_fence = False
        self.merge_held = False

    def call(self, slot, action, operation, expected_previous_owner=None):
        if action == "status":
            state = self.kube.states.get(slot)
            return {"ready": slot not in self.not_ready and state not in ("stopped", "failed"), "mode": state,
                    "instanceId": "instance-" + slot, "leaseOwner": self.lease_owner,
                    "admissionOpen": state == "active", "activeRuns": 1 if self.busy else 0, "reviewHoldProtocolVersion": 0 if slot in self.legacy_slots else 1}
        self.kube.events.append((action, slot))
        if action == "merge-fence":
            if self.fail_fence: raise RuntimeError("fence failed")
            self.merge_held = True
            return {"held": True, "operationId": operation, "projects": 1}
        if action == "activate":
            self.activations.append((slot, operation, expected_previous_owner))
            if slot in self.fail_activate:
                raise RuntimeError("activation failed")
            other = "green" if slot == "blue" else "blue"
            if self.kube.states.get(other) in ("active", "draining"):
                raise AssertionError("two active controllers")
            self.kube.states[slot] = "failed" if slot in self.failed_after_activate else "active"
        if action == "drain":
            self.kube.states[slot] = "draining"
        return {}


class ReleaseTests(unittest.TestCase):
    def make(self, previous="blue"):
        kube = FakeKube(previous)
        controllers = FakeControllers(kube)
        timer = iter(range(10000))
        release = Release(kube, controllers, "test-release", IMAGE, DATASET, "job-pod", "uid-1",
                          timeout=2, sleep=lambda _: None, clock=lambda: next(timer))
        return release, kube, controllers

    def test_legacy_rollback_keeps_durable_merge_fence(self):
        release, kube, controllers = self.make()
        controllers.legacy_slots.add("blue")
        controllers.fail_activate.add("green")
        with self.assertRaisesRegex(RuntimeError, "activation failed"): release.run()
        self.assertTrue(controllers.merge_held)
        self.assertLess(kube.events.index(("scale", "blue", 0)), kube.events.index(("merge-fence", "green")))
        self.assertLess(kube.events.index(("merge-fence", "green")), kube.events.index(("activate", "green")))
        self.assertEqual("true", kube.get("configmaps", release.journal_name)["data"]["mergeFenceConfirmed"])
        self.assertEqual("blue", kube.get("services", "forge")["spec"]["selector"]["slot"])

    def test_replacement_supervisor_preserves_legacy_fence_during_rollback(self):
        release, kube, controllers = self.make()
        controllers.legacy_slots.add("blue")
        release.run()
        kube.patch("configmaps", release.journal_name, {"data": {"phase": "rolling-back"}})
        replacement = Release(kube, controllers, "test-release", IMAGE, DATASET, "new-pod", "uid-2")
        with self.assertRaisesRegex(RuntimeError, "interrupted rollback completed"): replacement.run()
        self.assertTrue(controllers.merge_held)
        self.assertEqual("blue", kube.get("services", "forge")["spec"]["selector"]["slot"])
        self.assertEqual("true", kube.get("configmaps", release.journal_name)["data"]["mergeFenceConfirmed"])

    def test_missing_fence_proof_never_reactivates_legacy_controller(self):
        release, kube, controllers = self.make()
        controllers.legacy_slots.add("blue")
        release.run()
        release.journal["data"]["mergeFenceConfirmed"] = "false"
        kube.events.clear()
        with self.assertRaisesRegex(RuntimeError, "Downgrade blocked"): release.rollback("green", "blue")
        self.assertNotIn(("activate", "blue"), kube.events)
        self.assertEqual("none", kube.get("services", "forge")["spec"]["selector"]["slot"])

    def test_failed_fence_never_activates_new_work(self):
        release, kube, controllers = self.make()
        controllers.legacy_slots.add("blue")
        controllers.fail_fence = True
        with self.assertRaisesRegex(RuntimeError, "fence failed"): release.run()
        self.assertNotIn(("activate", "green"), kube.events)
        self.assertEqual("blue", kube.get("services", "forge")["spec"]["selector"]["slot"])
        self.assertEqual("active", kube.states["blue"])

    def test_successful_first_upgrade_keeps_prior_controller_fenced(self):
        release, kube, controllers = self.make()
        controllers.legacy_slots.add("blue")
        release.run()
        self.assertTrue(controllers.merge_held)
        self.assertEqual("committed", kube.get("configmaps", release.journal_name)["data"]["phase"])

    def test_cutover_stops_old_before_activation_and_switches_last(self):
        release, kube, _ = self.make()
        release.run()
        events = kube.events
        self.assertLess(events.index(("drain", "blue")), events.index(("scale", "blue", 0)))
        self.assertLess(events.index(("scale", "blue", 0)), events.index(("activate", "green")))
        self.assertLess(events.index(("activate", "green")), events.index(("route", "green")))
        self.assertEqual("committed", kube.get("configmaps", release.journal_name)["data"]["phase"])

    def test_activation_failure_stops_candidate_then_restores_previous(self):
        release, kube, controllers = self.make()
        controllers.fail_activate.add("green")
        with self.assertRaisesRegex(RuntimeError, "activation failed"):
            release.run()
        self.assertLess(kube.events.index(("scale", "green", 0)), kube.events.index(("activate", "blue")))
        self.assertEqual("blue", kube.get("services", "forge")["spec"]["selector"]["slot"])
        self.assertEqual("rolled-back", kube.get("configmaps", release.journal_name)["data"]["phase"])

    def test_terminating_old_pod_never_allows_green_activation(self):
        release, kube, _ = self.make()
        kube.terminating.add("blue")
        with self.assertRaises(TimeoutError):
            release.run()
        self.assertNotIn(("activate", "green"), kube.events)

    def test_observed_failed_activation_rolls_back_without_waiting_for_timeout(self):
        release, kube, controllers = self.make()
        controllers.failed_after_activate.add("green")
        def unexpected_delay(_):
            self.fail("terminal failed activation must not wait for the polling timeout")
        release.sleep = unexpected_delay
        with self.assertRaisesRegex(RuntimeError, "reported failed activation"):
            release.run()
        self.assertLess(kube.events.index(("scale", "green", 0)), kube.events.index(("activate", "blue")))
        self.assertEqual("active", kube.states["blue"])
        self.assertEqual("blue", kube.get("services", "forge")["spec"]["selector"]["slot"])
        self.assertEqual("rolled-back", kube.get("configmaps", release.journal_name)["data"]["phase"])

    def test_first_install_failure_routes_no_controller(self):
        release, kube, controllers = self.make(previous="")
        controllers.fail_activate.add("blue")
        with self.assertRaises(RuntimeError):
            release.run()
        self.assertEqual("none", kube.get("services", "forge")["spec"]["selector"]["slot"])

    def test_live_release_lock_blocks_all_deployment_changes(self):
        release, kube, _ = self.make()
        kube.create("configmaps", {"metadata": {"name": "forge-release-lock"}, "data": {"pod": "other", "uid": "other-uid"}})
        kube.create("pods", {"metadata": {"name": "other", "uid": "other-uid"}, "status": {"phase": "Running"}})
        with self.assertRaisesRegex(RuntimeError, "still owns"):
            release.run()
        self.assertEqual([], kube.events)

    def test_replaced_release_job_reconciles_committed_operation_without_cutover(self):
        release, kube, controllers = self.make()
        release.run()
        kube.events.clear()
        replacement = Release(kube, controllers, "test-release", IMAGE, DATASET, "new-pod", "uid-2")
        replacement.run()
        self.assertEqual([], kube.events)

    def test_replacement_supervisor_does_not_replay_rolled_back_operation(self):
        release, kube, controllers = self.make()
        controllers.fail_activate.add("green")
        with self.assertRaises(RuntimeError): release.run()
        kube.events.clear()
        replacement = Release(kube, controllers, "test-release", IMAGE, DATASET, "retry-pod", "retry-uid")
        with self.assertRaisesRegex(RuntimeError, "operation rolled back"):
            replacement.run()
        self.assertEqual([], kube.events)

    def test_conflicting_operation_input_is_rejected(self):
        release, kube, controllers = self.make()
        release.run()
        kube.events.clear()
        replacement = Release(kube, controllers, "test-release", OLD_IMAGE, DATASET, "new-pod", "uid-2")
        with self.assertRaisesRegex(RuntimeError, "different immutable inputs"):
            replacement.run()
        self.assertEqual([], kube.events)

    def test_another_live_candidate_is_not_stopped_on_validation_failure(self):
        release, kube, _ = self.make()
        kube.create("deployments", deployment("green", IMAGE, DATASET, "other"))
        kube.events.clear()
        with self.assertRaisesRegex(RuntimeError, "another live release"):
            release.run()
        self.assertEqual([], kube.events)

    def test_observed_old_lease_owner_is_compare_and_swapped_after_stop(self):
        release, kube, controllers = self.make()
        controllers.lease_owner = "instance-blue"
        release.run()
        self.assertEqual(("green", "test-release-activate-instance-green", "instance-blue"), controllers.activations[0])
        self.assertLess(kube.events.index(("scale", "blue", 0)), kube.events.index(("activate", "green")))

    def test_unknown_lease_owner_is_never_stolen(self):
        release, kube, controllers = self.make(previous="")
        controllers.lease_owner = "unrelated-controller"
        with self.assertRaisesRegex(RuntimeError, "unobserved controller"):
            release.run()
        self.assertEqual([], controllers.activations)
        self.assertEqual("none", kube.get("services", "forge")["spec"]["selector"]["slot"])

    def test_rollback_has_distinct_activation_operation(self):
        release, _, controllers = self.make()
        controllers.fail_activate.add("green")
        with self.assertRaises(RuntimeError):
            release.run()
        self.assertEqual("test-release-rollback-instance-blue", controllers.activations[-1][1])

    def test_digest_required_and_jobs_have_no_controller_owner(self):
        with self.assertRaises(ValueError):
            validate_image("registry.example/forge:latest")
        resource = job("forge", IMAGE, DATASET, "test", "release-test")
        self.assertNotIn("ownerReferences", resource["metadata"])
        self.assertEqual(3, resource["spec"]["backoffLimit"])
        self.assertEqual("Never", resource["spec"]["template"]["spec"]["restartPolicy"])
        initializer = job("forge", IMAGE, DATASET, "test", "initialize-test", initialize=True)
        self.assertEqual(0, initializer["spec"]["backoffLimit"])
        self.assertNotIn("ttlSecondsAfterFinished", resource["spec"])

    def test_worker_cannot_get_cluster_api_and_control_plane_has_no_secret_read(self):
        resources = foundation("forge")
        worker = next(r for r in resources if r["kind"] == "ServiceAccount" and r["metadata"]["name"] == "forge-worker")
        self.assertFalse(worker["automountServiceAccountToken"])
        for role in (r for r in resources if r["kind"] == "Role"):
            self.assertFalse(any("secrets" in rule["resources"] or "*" in rule["resources"] for rule in role["rules"]))


if __name__ == "__main__":
    unittest.main()
