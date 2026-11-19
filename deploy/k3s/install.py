#!/usr/bin/env python3
"""Single operator entrypoint. Plan by default; --apply performs the explicit installation/release."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import tempfile
import time
import urllib.request
import uuid

from manifests import foundation, job, obj
from release import validate_image
from catalog import register, validate_receipt
from github_apps import app_bundle, webhook_bundle


def command(args, data=None, optional=False):
    result = subprocess.run(args, input=data, text=True, capture_output=True, timeout=120)
    if result.returncode and not optional:
        # Neither argv nor stderr are printed: config/credential diagnostics may contain secrets.
        raise RuntimeError(f"{Path(args[0]).name} failed with exit {result.returncode}")
    return result


def legacy_active(run=command):
    active = []
    for scope in ([], ["--user"]):
        result = run(["systemctl", *scope, "is-active", "forge.service"], optional=True)
        state = result.stdout.strip()
        if state in ("active", "activating", "reloading", "deactivating"):
            active.append(scope)
        elif state not in ("inactive", "failed", "unknown"):
            raise RuntimeError("cannot determine legacy Forge service state; inspect both system and user service")
    return active


def bootstrap(version, checksum):
    if not re.fullmatch(r"v\d+\.\d+\.\d+\+k3s\d+", version or "") or not re.fullmatch(r"[0-9a-f]{64}", checksum or ""):
        raise ValueError("new K3s installation requires explicit --k3s-version and --k3s-sha256")
    if os.uname().machine != "x86_64":
        raise RuntimeError("this bootstrap is qualified only for Linux x86_64")
    config = Path("/etc/rancher/k3s/config.yaml")
    if config.exists():
        raise RuntimeError("K3s config already exists without executable; reconcile it before bootstrap")
    with tempfile.TemporaryDirectory(prefix="forge-k3s-") as temp:
        binary = Path(temp) / "k3s"
        url = "https://github.com/k3s-io/k3s/releases/download/" + version.replace("+", "%2B") + "/k3s"
        with urllib.request.urlopen(url, timeout=60) as response, binary.open("wb") as target:
            shutil.copyfileobj(response, target)
        if hashlib.sha256(binary.read_bytes()).hexdigest() != checksum:
            raise RuntimeError("K3s binary checksum mismatch")
        configuration = Path(temp) / "config.yaml"
        configuration.write_text("cluster-init: true\nsecrets-encryption: true\nwrite-kubeconfig-mode: '0600'\ndisable:\n  - traefik\n  - servicelb\n", encoding="utf-8")
        unit = Path(temp) / "k3s.service"
        unit.write_text("""[Unit]
Description=K3s for Forge
After=network-online.target
Wants=network-online.target
[Service]
Type=notify
ExecStart=/usr/local/bin/k3s server
Restart=always
RestartSec=5
Delegate=yes
KillMode=process
LimitNOFILE=1048576
LimitNPROC=infinity
TasksMax=infinity
[Install]
WantedBy=multi-user.target
""", encoding="utf-8")
        command(["sudo", "install", "-m", "0755", str(binary), "/usr/local/bin/k3s"])
        command(["sudo", "install", "-d", "-m", "0700", "/etc/rancher/k3s"])
        command(["sudo", "install", "-m", "0600", str(configuration), str(config)])
        command(["sudo", "install", "-m", "0644", str(unit), "/etc/systemd/system/k3s.service"])
        command(["sudo", "systemctl", "daemon-reload"])
        command(["sudo", "systemctl", "enable", "--now", "k3s.service"])


class Installer:
    def __init__(self, args):
        self.args = args
        self.prefix = ["sudo", "/usr/local/bin/k3s", "kubectl"]

    def kubectl(self, args, payload=None):
        result = command([*self.prefix, "--namespace", self.args.namespace, *args],
                         None if payload is None else json.dumps(payload))
        return json.loads(result.stdout) if result.stdout.strip().startswith(("{", "[")) else result.stdout

    def get(self, kind, name):
        value = self.kubectl(["get", kind, name, "--ignore-not-found", "-o", "json"])
        return value if isinstance(value, dict) else None

    def apply(self, item):
        return self.kubectl(["apply", "-f", "-", "-o", "json"], item)

    def immutable_secret(self, name, values, secret_type="Opaque"):
        desired = {key: base64.b64encode(value).decode() for key, value in values.items()}
        existing = self.get("secret", name)
        if existing:
            if existing.get("data") != desired or existing.get("type", "Opaque") != secret_type:
                raise RuntimeError(f"existing {name} differs; refuse in-place credential/config replacement during release")
            return
        self.apply(obj("Secret", name, type=secret_type, data=desired))

    def wait_job(self, name):
        deadline = time.monotonic() + 3700
        while time.monotonic() < deadline:
            current = self.get("job", name)
            conditions = current.get("status", {}).get("conditions", []) if current else []
            if any(c["type"] == "Complete" and c["status"] == "True" for c in conditions):
                return
            if any(c["type"] == "Failed" and c["status"] == "True" for c in conditions):
                raise RuntimeError(f"Job {name} failed; retained pod logs and release journal require inspection")
            time.sleep(3)
        raise TimeoutError("Job wait timed out; Job remains independently supervised; inspect before retry")

    def run(self):
        args = self.args
        legacy = legacy_active()
        if legacy and not args.retire_legacy:
            raise RuntimeError("legacy Forge is active; explicitly choose --retire-legacy after reviewing fresh-state cutover")
        if not Path("/usr/local/bin/k3s").exists():
            bootstrap(args.k3s_version, args.k3s_sha256)
        self.kubectl(["wait", "--for=condition=Ready", "node", "--all", "--timeout=180s"])
        # Do not silently adopt K3s' SQLite default or an external datastore.
        check = command(["sudo", "test", "-d", "/var/lib/rancher/k3s/server/db/etcd/member"], optional=True)
        if check.returncode:
            raise RuntimeError("embedded etcd data not found; refusing unqualified K3s datastore")
        for scope in legacy:
            command((["systemctl", "--user"] if scope else ["sudo", "systemctl"]) + ["stop", "forge.service"])
        if legacy_active():
            raise RuntimeError("legacy service still active; no Forge workloads were launched")
        pull_secret = args.pull_secret or ("forge-registry" if args.registry_config else None)
        for item in foundation(args.namespace, pull_secret=pull_secret, manager_namespace=getattr(args, "manager_namespace", None)):
            if item["kind"] == "Service" and item["metadata"]["name"] == "forge" and self.get("service", "forge"):
                continue  # Never reset live traffic to 'none' during an upgrade.
            self.apply(item)
        if args.registry_config:
            self.immutable_secret(pull_secret, {".dockerconfigjson": Path(args.registry_config).read_bytes()},
                                  secret_type="kubernetes.io/dockerconfigjson")
        elif pull_secret and not self.get("secret", pull_secret):
            raise RuntimeError("requested image-pull Secret does not exist")
        # Qualify source before any schema initialization. This isolated Job receives no secrets or API token.
        qualification = job(args.namespace, args.image, args.dataset, args.operation,
                            "forge-source-" + args.operation, initialize=True)
        pod = qualification["spec"]["template"]["spec"]
        pod["volumes"] = []
        container = pod["containers"][0]
        container["env"], container["volumeMounts"] = [], []
        container["command"] = ["python3", "-c", "import pathlib,sys; sys.exit(0 if pathlib.Path('/app/source-revision').read_text().strip()==sys.argv[1] else 1)"]
        container["args"] = [args.source_sha]
        self.apply(qualification)
        self.wait_job(qualification["metadata"]["name"])
        register(self, json.loads(Path(args.image_receipt).read_text(encoding="utf-8-sig")))
        identity = self.get("configmap", "forge-dataset")
        if identity and identity["data"]["id"] != args.dataset:
            raise RuntimeError("namespace belongs to another dataset; reset is never implicit")
        if not identity:
            self.apply(obj("ConfigMap", "forge-dataset", data={"id": args.dataset}))
        if not self.get("secret", "forge-control"):
            self.immutable_secret("forge-control", {"controller-token": secrets.token_urlsafe(48).encode()})
        self.immutable_secret("forge-runtime", {"sql-connection-string": Path(args.sql_file).read_bytes().strip()})
        self.immutable_secret("forge-initialize-runtime", {"sql-connection-string": Path(args.initialize_sql_file).read_bytes().strip()})
        self.immutable_secret("forge-worker-runtime", {"sql-connection-string": Path(args.worker_sql_file).read_bytes().strip()})
        self.immutable_secret("forge-worker-packages", worker_packages(Path(args.worker_packages_file)))
        self.immutable_secret("forge-config", {"appsettings.json": Path(args.config).read_bytes()})
        if getattr(args, "github_apps_directory", None):
            self.immutable_secret("forge-github-apps", app_bundle(args.github_apps_directory))
        if getattr(args, "github_webhook_secret_file", None):
            self.immutable_secret("forge-github-events", webhook_bundle(args.github_webhook_secret_file))
        service = self.get("service", "forge")
        current_slot = service["spec"].get("selector", {}).get("slot", "none")
        # Initialization is explicit and never runs alongside an active controller.
        if current_slot == "none":
            if any(self.kubectl(["get", "pods", "-l", "app=forge-controller", "-o", "json"])["items"]):
                raise RuntimeError("controller pod exists before initialization; reconcile release journal first")
            init_name = "forge-init-" + args.operation
            existing = self.get("job", init_name)
            if not existing:
                self.apply(job(args.namespace, args.image, args.dataset, args.operation, init_name, initialize=True))
            self.wait_job(init_name)
        name = "forge-release-" + args.operation + "-" + uuid.uuid4().hex[:6]
        self.apply(job(args.namespace, args.image, args.dataset, args.operation, name,
                       environment=getattr(args, "environment", None), source_sha=args.source_sha))
        print(json.dumps({"releaseJob": name, "namespace": args.namespace, "image": args.image}), flush=True)
        self.wait_job(name)
        print("Release completed. Verify application through a loopback port-forward before opening work gates.")


def worker_packages(path):
    credentials = json.loads(path.read_text(encoding="utf-8-sig"))
    if not all(isinstance(credentials.get(key), str) and credentials[key].strip() for key in ("username", "token")):
        raise ValueError("worker package file requires nonempty username and token strings")
    return {key: credentials[key].strip().encode() for key in ("username", "token")}


def validate_shared_configuration(configuration):
    database = next((value for key, value in configuration.items() if key.lower() == "db"), {})
    database = {key.lower(): value for key, value in database.items()}
    if str(database.get("provider", "sqlserver")).lower() != "sqlserver":
        raise ValueError("only Azure SQL/sqlserver configuration is accepted")
    if database.get("connectionstring") or any(key.lower() in ("db:connectionstring", "db__connectionstring") and value for key, value in configuration.items()):
        raise ValueError("shared configuration must not contain SQL credentials; use the three separate connection files")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for flag in ("image", "source-sha", "image-receipt", "dataset", "operation", "sql-file", "initialize-sql-file", "worker-sql-file", "worker-packages-file", "config"):
        parser.add_argument("--" + flag, required=True)
    parser.add_argument("--namespace", default="forge")
    parser.add_argument("--environment", help="Named project environment; verifies the routed dashboard and source before release completion")
    parser.add_argument("--manager-namespace", help="Namespace of the Forge controller allowed to deploy into this isolated target")
    parser.add_argument("--github-apps-directory", help="Private directory containing App config.json and referenced PEM keys; controller only")
    parser.add_argument("--github-webhook-secret-file", help="Private signature secret for /hooks/github; controller only, does not expose ingress")
    parser.add_argument("--k3s-version")
    parser.add_argument("--k3s-sha256")
    parser.add_argument("--pull-secret", help="Existing registry image-pull Secret in target namespace")
    parser.add_argument("--registry-config", help="Private Docker-format registry auth file to install as a pull Secret")
    parser.add_argument("--retire-legacy", action="store_true")
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    validate_image(args.image)
    receipt = validate_receipt(json.loads(Path(args.image_receipt).read_text(encoding="utf-8-sig")))
    if receipt["sourceSha"] != args.source_sha or receipt["imageDigest"] != args.image:
        raise ValueError("image/source inputs do not match reviewed CI receipt")
    if not re.fullmatch(r"[0-9a-f]{40}", args.source_sha):
        raise ValueError("source-sha must be the full reviewed lowercase commit SHA")
    uuid.UUID(args.dataset)
    if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,40}", args.operation):
        raise ValueError("invalid operation")
    if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,40}", args.namespace):
        raise ValueError("invalid namespace")
    if args.manager_namespace and not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,40}", args.manager_namespace):
        raise ValueError("invalid manager namespace")
    if args.environment and not re.fullmatch(r"[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?", args.environment):
        raise ValueError("invalid environment name")
    configuration = json.loads(Path(args.config).read_text(encoding="utf-8-sig"))
    validate_shared_configuration(configuration)
    if args.github_apps_directory: app_bundle(args.github_apps_directory)
    if args.github_webhook_secret_file: webhook_bundle(args.github_webhook_secret_file)
    worker_packages(Path(args.worker_packages_file))
    for connection_file in (args.sql_file, args.initialize_sql_file, args.worker_sql_file):
        if not Path(connection_file).is_file() or not Path(connection_file).read_bytes().strip():
            raise ValueError("a required SQL connection file is missing or empty")
    if len({Path(path).read_bytes().strip() for path in (args.sql_file, args.initialize_sql_file, args.worker_sql_file)}) != 3:
        raise ValueError("controller, initializer, and worker require distinct SQL connection identities")
    if args.registry_config:
        registry = json.loads(Path(args.registry_config).read_text(encoding="utf-8"))
        if not registry.get("auths"):
            raise ValueError("registry config must contain explicit auths, not a machine-local credential helper")
    if not args.apply:
        print(json.dumps({"mode": "plan", "namespace": args.namespace, "dataset": args.dataset,
            "environment": args.environment, "managerNamespace": args.manager_namespace, "sourceSha": args.source_sha,
            "image": args.image, "operation": args.operation, "legacyAction": "stop" if args.retire_legacy else "require stopped",
            "clusterDatastore": "embedded-etcd", "applicationDatastore": "Azure SQL"}, indent=2))
        return
    Installer(args).run()


if __name__ == "__main__":
    main()
