import unittest
from test_k3s_release import FakeKube, FakeControllers, Release, IMAGE, OLD_IMAGE, DATASET
from schema_release import choose_release
from review_authority import desired_job


class AuthorityKube(FakeKube):
    failed = False
    still_running = False

    def create(self, kind, body):
        result = super().create(kind, body)
        if kind == "jobs" and self.failed:
            self.objects[(kind, body["metadata"]["name"])]["status"] = {"conditions": [{"type": "Failed", "status": "True"}]}
        return result

    def selected_pods(self, selector):
        return [{"status": {"phase": "Running"}}] if self.still_running else []


class ReviewAuthorityReleaseTests(unittest.TestCase):
    def make(self, previous="blue"):
        kube = AuthorityKube(previous)
        controllers = FakeControllers(kube)
        original_call = controllers.call
        def observed(slot, action, operation, expected_previous_owner=None):
            result = original_call(slot, action, operation, expected_previous_owner)
            if action == "status": result["schemaVersion"] = 43
            return result
        controllers.call = observed
        ticks = iter(range(10000))
        return Release(kube, controllers, "authority-test", IMAGE, DATASET, "pod", "uid",
                       timeout=2, sleep=lambda _: None, clock=lambda: next(ticks)), kube, controllers

    def test_existing_schema43_normal_release_initializes_before_candidate(self):
        release, kube, controllers = self.make()
        kind, policy = choose_release(kube, controllers, "authority-test",
            {"fromVersion": 42, "toVersion": 43, "rollbackImage": OLD_IMAGE})
        self.assertIs(kind, Release)
        self.assertIsNone(policy)
        release.run()
        self.assertLess(kube.events.index(("authority", "forge-authority-authority-test")), kube.events.index(("create", "forge-green")))
        job = kube.get("jobs", "forge-authority-authority-test")
        container = job["spec"]["template"]["spec"]["containers"][0]
        env = {item["name"]: item for item in container["env"]}
        self.assertEqual(IMAGE, container["image"])
        self.assertEqual("true", env["Bootstrap__ReviewAuthorityOnly"]["value"])
        self.assertEqual("false", env["Db__AutoMigrate"]["value"])
        self.assertEqual("forge-initialize-runtime", env["Db__ConnectionString"]["valueFrom"]["secretKeyRef"]["name"])
        self.assertEqual("true", release.journal["data"]["authorityConfirmed"])
        release.run()
        self.assertEqual(1, sum(event[0] == "authority" for event in kube.events))

    def test_failed_authority_keeps_old_controller_and_never_creates_candidate(self):
        release, kube, controllers = self.make()
        kube.failed = True
        with self.assertRaises(RuntimeError): release.run()
        self.assertNotIn(("create", "forge-green"), kube.events)
        self.assertEqual("active", kube.states["blue"])
        self.assertEqual([], controllers.activations)

    def test_job_complete_but_pod_running_does_not_activate(self):
        release, kube, controllers = self.make()
        kube.still_running = True
        with self.assertRaises(TimeoutError): release.run()
        self.assertEqual([], controllers.activations)
        self.assertEqual("active", kube.states["blue"])

    def test_changed_initializer_image_is_not_accepted_as_prerequisite(self):
        release, kube, controllers = self.make()
        impostor = desired_job(release)
        impostor["spec"]["template"]["spec"]["containers"][0]["image"] = OLD_IMAGE
        kube.create("jobs", impostor)
        with self.assertRaises(RuntimeError): release.run()
        self.assertNotIn(("create", "forge-green"), kube.events)
        self.assertEqual([], controllers.activations)

    def test_next_release_cannot_overtake_a_previous_initializer(self):
        release, kube, controllers = self.make()
        kube.create("configmaps", {"metadata": {"name": "forge-release-lock"},
            "data": {"pod": "old-pod", "uid": "old-uid", "operation": "prior"}})
        kube.create("configmaps", {"metadata": {"name": "forge-release-prior"},
            "data": {"phase": "rolled-back", "authorityJob": "forge-authority-prior"}})
        kube.create("jobs", {"metadata": {"name": "forge-authority-prior"}})
        kube.objects[("jobs", "forge-authority-prior")]["status"] = {}
        with self.assertRaises(RuntimeError): release.run()
        self.assertNotIn(("create", "forge-green"), kube.events)
        self.assertEqual([], controllers.activations)

    def test_fresh_install_uses_same_idempotent_security_prerequisite(self):
        release, kube, controllers = self.make(previous=None)
        release.run()
        self.assertEqual("committed", release.journal["data"]["phase"])
        self.assertLess(kube.events.index(("authority", "forge-authority-authority-test")), kube.events.index(("create", "forge-blue")))


if __name__ == "__main__": unittest.main()
