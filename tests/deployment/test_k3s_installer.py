import argparse
import base64
import copy
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "deploy" / "k3s"))
from install import Installer, legacy_active, validate_shared_configuration
from manifests import job, foundation
from catalog import register, validate_receipt
from release import deployment

class FakeInstaller(Installer):
    def __init__(self, value):
        super().__init__(argparse.Namespace(namespace="forge"))
        self.value, self.applied = value, []

    def get(self, kind, name):
        return {"data": {"value": base64.b64encode(self.value).decode()}}

    def apply(self, value):
        self.applied.append(value)


class InstallerGuardTests(unittest.TestCase):
    def test_shared_configuration_cannot_distribute_sql_credentials(self):
        for configuration in ({"Db": {"ConnectionString": "private"}},
                              {"db:ConnectionString": "private"}, {"DB__CONNECTIONSTRING": "private"}):
            with self.assertRaisesRegex(ValueError, "must not contain SQL"):
                validate_shared_configuration(configuration)
        validate_shared_configuration({"db": {"provider": "sqlserver", "connectionString": ""}})

    def test_dashboard_is_cluster_private(self):
        service = next(x for x in foundation("forge") if x["kind"] == "Service" and x["metadata"]["name"] == "forge")
        self.assertEqual("ClusterIP", service["spec"]["type"])
        self.assertNotIn("nodePort", service["spec"]["ports"][0])

    def test_catalog_never_rebinds_a_source_commit(self):
        receipt = {"sourceSha": "a" * 40, "imageDigest": "registry/forge@sha256:" + "b" * 64,
                   "buildPassed": True, "testPassed": True, "workflow": "controller-image.yml", "workflowRunId": "123"}
        class Catalog:
            def get(self, kind, name):
                return {"data": {"a" * 40: json.dumps(receipt)}}
            def kubectl(self, *args):
                raise AssertionError("idempotent/conflicting receipt must not write")
        register(Catalog(), receipt)
        with self.assertRaisesRegex(ValueError, "already bound"):
            register(Catalog(), {**receipt, "imageDigest": "registry/forge@sha256:" + "c" * 64})

    def test_catalog_requires_actual_success_booleans(self):
        with self.assertRaisesRegex(ValueError, "attestation"):
            validate_receipt({"sourceSha": "a" * 40, "imageDigest": "registry/forge@sha256:" + "b" * 64,
                "buildPassed": "true", "testPassed": True, "workflow": "controller-image.yml", "workflowRunId": "123"})

    def test_live_legacy_is_detected_in_both_scopes(self):
        result = argparse.Namespace(stdout="active\n", returncode=0)
        calls = []
        def run(args, optional):
            calls.append(args)
            return result
        self.assertEqual([[], ["--user"]], legacy_active(run))
        self.assertEqual(2, len(calls))

    def test_unavailable_user_bus_does_not_mean_old_service_stopped(self):
        responses = [argparse.Namespace(stdout="inactive\n", returncode=3),
                     argparse.Namespace(stdout="", returncode=1)]
        with self.assertRaisesRegex(RuntimeError, "cannot determine"):
            legacy_active(lambda args, optional: responses.pop(0))

    def test_existing_credentials_are_never_replaced_implicitly(self):
        installer = FakeInstaller(b"old")
        with self.assertRaisesRegex(RuntimeError, "refuse in-place"):
            installer.immutable_secret("forge-runtime", {"value": b"new"})
        self.assertEqual([], installer.applied)

    def test_identical_credentials_are_idempotent(self):
        installer = FakeInstaller(b"same")
        installer.immutable_secret("forge-runtime", {"value": b"same"})
        self.assertEqual([], installer.applied)

    def test_initialization_has_no_cluster_api_and_persists_keyring(self):
        manifest = job("forge", "registry/forge@sha256:" + "a" * 64, "dataset", "initial", "initial", initialize=True)
        pod = manifest["spec"]["template"]["spec"]
        self.assertFalse(pod["automountServiceAccountToken"])
        values = {x["name"]: x.get("value") for x in pod["containers"][0]["env"]}
        self.assertEqual("initialize", values["Controller__Mode"])
        self.assertEqual("sqlserver", values["Db__Provider"])
        self.assertEqual("/var/lib/forge/keyring", values["Controller__KeyringPath"])
        self.assertEqual("forge-k3s-worker", values["Bootstrap__WorkerPrincipal"])

    def test_only_initializer_gets_schema_owner_secret(self):
        image = "registry/forge@sha256:" + "a" * 64
        initializer = job("forge", image, "dataset", "initial", "initial", initialize=True)
        controller = deployment("blue", image, "dataset", "initial")
        release = job("forge", image, "dataset", "upgrade", "upgrade")
        def references(manifest):
            pod = manifest["spec"]["template"]["spec"]
            return [item["valueFrom"]["secretKeyRef"]["name"]
                    for container in pod["containers"] for item in container.get("env", [])
                    if "secretKeyRef" in item.get("valueFrom", {})]
        self.assertEqual(["forge-initialize-runtime"], references(initializer))
        self.assertEqual(["forge-github-events", "forge-github-apps", "forge-runtime"], references(controller))
        self.assertEqual([], references(release))


    def test_control_mount_does_not_mask_service_account_directory(self):
        image = "registry/forge@sha256:" + "a" * 64
        for manifest in (deployment("blue", image, "dataset", "initial"),
                         job("forge", image, "dataset", "upgrade", "upgrade"),
                         job("forge", image, "dataset", "initial", "initial", initialize=True)):
            pod = manifest["spec"]["template"]["spec"]
            for container in pod["containers"]:
                token_mount = next(m for m in container["volumeMounts"] if m["name"] == "control-token")
                self.assertEqual("/run/forge-control", token_mount["mountPath"])
                if pod.get("automountServiceAccountToken", True):
                    for mount in container["volumeMounts"]:
                        path = mount["mountPath"].replace("/var/run/", "/run/").rstrip("/")
                        self.assertFalse(mount.get("readOnly", False) and
                            ("/run/secrets/kubernetes.io/serviceaccount" == path or
                             "/run/secrets/kubernetes.io/serviceaccount".startswith(path + "/")))
                for value in container.get("env", []):
                    if value["name"] == "Controller__TokenFile":
                        self.assertEqual("/run/forge-control/controller-token", value["value"])


if __name__ == "__main__":
    unittest.main()
