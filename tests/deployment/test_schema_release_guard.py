import unittest
from test_k3s_release import FakeKube, FakeControllers, IMAGE, DATASET
from release import Release

class SchemaGuardTests(unittest.TestCase):
    def fixture(self, operation="other"):
        kube = FakeKube()
        kube.create("configmaps", {"metadata": {"name": "forge-release-lock"},
            "data": {"pod": "dead", "uid": "dead-uid", "operation": "migration"}})
        kube.create("configmaps", {"metadata": {"name": "forge-release-migration"},
            "data": {"kind": "schema-upgrade", "phase": "migrating", "image": IMAGE, "dataset": DATASET}})
        kube.events.clear()
        return Release(kube, FakeControllers(kube), operation, IMAGE, DATASET, "replacement", "new-uid"), kube

    def test_dead_owner_does_not_allow_different_operation_to_bypass_migration(self):
        release, kube = self.fixture()
        with self.assertRaisesRegex(RuntimeError, "unfinished schema upgrade"):
            release.run()
        self.assertEqual([], kube.events)
        self.assertEqual("dead-uid", kube.get("configmaps", "forge-release-lock")["data"]["uid"])

    def test_same_operation_cannot_resume_through_ordinary_release(self):
        release, kube = self.fixture("migration")
        with self.assertRaisesRegex(RuntimeError, "requires its schema supervisor"):
            release.run()
        self.assertEqual([], kube.events)

    def test_terminal_schema_journal_allows_new_operation_lock(self):
        release, kube = self.fixture()
        kube.objects[("configmaps", "forge-release-migration")]["data"]["phase"] = "rolled-back"
        release.acquire()
        self.assertEqual("new-uid", kube.get("configmaps", "forge-release-lock")["data"]["uid"])
