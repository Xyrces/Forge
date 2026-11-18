import sys
import pathlib
import unittest
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "deploy" / "k3s"))
from project_provision import initializer, run
from test_k3s_release import IMAGE, DATASET

class ProvisionTests(unittest.TestCase):
    def test_scoped_initializer_has_no_controller_or_worker_credentials(self):
        value = initializer("forge", IMAGE, DATASET, "second-project", "registration")
        pod = value["spec"]["template"]["spec"]
        self.assertEqual("forge-initialize", pod["serviceAccountName"])
        self.assertFalse(pod["automountServiceAccountToken"])
        self.assertEqual(["config"], [v["name"] for v in pod["volumes"]])
        env = {v["name"]:v for v in pod["containers"][0]["env"]}
        self.assertEqual("second-project",env["Bootstrap__ProjectId"]["value"])
        self.assertEqual("true",env["Bootstrap__ProjectProvisionOnly"]["value"])
        self.assertEqual("forge-initialize-runtime",env["Db__ConnectionString"]["valueFrom"]["secretKeyRef"]["name"])
        self.assertNotIn("Bootstrap__MigrationFromVersion",env)

    def test_failed_job_is_not_a_successful_registration(self):
        desired = initializer("forge",IMAGE,DATASET,"second","registration")
        class Kube:
            def get(self,*args): return dict(desired,status={"conditions":[{"type":"Failed","status":"True"}]})
            def selected_pods(self,*args): return []
        with self.assertRaisesRegex(RuntimeError,"initializer failed"):
            run(Kube(),desired,1)

    def test_finished_job_adopts_same_immutable_request(self):
        desired = initializer("forge",IMAGE,DATASET,"second","registration")
        class Kube:
            def get(self,*args): return dict(desired,status={"conditions":[{"type":"Complete","status":"True"}]})
            def selected_pods(self,*args): return []
        run(Kube(),desired,1)
