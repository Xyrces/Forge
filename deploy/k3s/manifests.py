"""Small JSON Kubernetes bundle; no templating engine or third-party Python dependency."""
from release import validate_image


def obj(kind, name, **extra):
    return {"apiVersion": "v1", "kind": kind, "metadata": {"name": name}, **extra}


def worker_network_policy():
    # Only public destinations plus the cluster DNS pods. Do not allow entire
    # cluster/private CIDRs merely to reach DNS: that exposes operator APIs.
    return {"apiVersion": "networking.k8s.io/v1", "kind": "NetworkPolicy",
            "metadata": {"name": "forge-worker-public-egress"}, "spec": {
                "podSelector": {"matchLabels": {"app.kubernetes.io/name": "forge-worker"}},
                "policyTypes": ["Egress"], "egress": [
                    {"to": [{"namespaceSelector": {"matchLabels": {"kubernetes.io/metadata.name": "kube-system"}},
                             "podSelector": {"matchLabels": {"k8s-app": "kube-dns"}}}],
                     "ports": [{"protocol": "UDP", "port": 53}, {"protocol": "TCP", "port": 53}]},
                    {"to": [{"ipBlock": {"cidr": "0.0.0.0/0", "except": [
                        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8",
                        "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24",
                        "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24",
                        "224.0.0.0/4", "240.0.0.0/4"]}},
                            {"ipBlock": {"cidr": "2000::/3", "except": ["2001:db8::/32"]}}]}]}}


def foundation(namespace, storage="40Gi", pull_secret=None, manager_namespace=None):
    items = [obj("Namespace", namespace),
             obj("PersistentVolumeClaim", "forge-state", spec={"accessModes": ["ReadWriteOnce"],
                 "resources": {"requests": {"storage": storage}}}), worker_network_policy()]
    for account in ("controller", "worker", "release", "initialize"):
        service_account = obj("ServiceAccount", "forge-" + account,
                              automountServiceAccountToken=account in ("controller", "release"))
        if pull_secret:
            service_account["imagePullSecrets"] = [{"name": pull_secret}]
        items.append(service_account)
    rules = {
        "controller": [
            {"apiGroups": [""], "resources": ["configmaps"], "verbs": ["get"]},
            {"apiGroups": ["batch"], "resources": ["jobs"], "verbs": ["create", "get", "list", "watch", "delete"]},
            {"apiGroups": [""], "resources": ["pods", "pods/log"], "verbs": ["get", "list", "watch"]}],
        "release": [
            {"apiGroups": ["batch"], "resources": ["jobs"], "verbs": ["create", "get"]},
            {"apiGroups": ["apps"], "resources": ["deployments"], "verbs": ["create", "get", "patch"]},
            {"apiGroups": [""], "resources": ["pods"], "verbs": ["get", "list"]},
            {"apiGroups": [""], "resources": ["configmaps"], "verbs": ["create", "get", "patch"]},
            {"apiGroups": [""], "resources": ["services"], "resourceNames": ["forge"], "verbs": ["get", "patch"]}],
    }
    for account, account_rules in rules.items():
        items += [{"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "Role",
                   "metadata": {"name": "forge-" + account}, "rules": account_rules},
                  {"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "RoleBinding",
                   "metadata": {"name": "forge-" + account},
                   "subjects": [{"kind": "ServiceAccount", "name": "forge-" + account, "namespace": namespace}],
                   "roleRef": {"apiGroup": "rbac.authorization.k8s.io", "kind": "Role", "name": "forge-" + account}}]
    if manager_namespace and manager_namespace != namespace:
        # A management controller may submit/reconcile releases here, but cannot
        # read target credentials, change RBAC, route services, or mutate workloads.
        # The target-local independent release Job owns those operations.
        items += [{"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "Role",
                   "metadata": {"name": "forge-deployment-manager"}, "rules": [
                       {"apiGroups": ["batch"], "resources": ["jobs"], "verbs": ["create", "get"]},
                       {"apiGroups": [""], "resources": ["configmaps"], "verbs": ["get"]}]},
                  {"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "RoleBinding",
                   "metadata": {"name": "forge-deployment-manager"},
                   "subjects": [{"kind": "ServiceAccount", "name": "forge-controller", "namespace": manager_namespace}],
                   "roleRef": {"apiGroup": "rbac.authorization.k8s.io", "kind": "Role", "name": "forge-deployment-manager"}}]
    for slot in ("blue", "green"):
        items.append(obj("Service", "forge-" + slot, spec={"selector": {"app": "forge-controller", "slot": slot},
            "ports": [{"name": "control", "port": 9090, "targetPort": 9090}]}))
    # The public selector is created once by installer. Never apply 'none' over a live release.
    items.append(obj("Service", "forge", spec={"type": "ClusterIP", "selector": {"app": "forge-controller", "slot": "none"},
        "ports": [{"name": "dashboard", "port": 8080, "targetPort": 8080}]}))
    for item in items:
        if item["kind"] != "Namespace":
            item["metadata"]["namespace"] = namespace
    return items


def job(namespace, image, dataset, operation, name, initialize=False, migration=None, environment=None, source_sha=None):
    if migration is not None and not initialize:
        raise ValueError("migration settings belong only to initializer Jobs")
    env = [{"name": "POD_NAME", "valueFrom": {"fieldRef": {"fieldPath": "metadata.name"}}},
           {"name": "POD_UID", "valueFrom": {"fieldRef": {"fieldPath": "metadata.uid"}}}]
    container = {"name": "operation", "image": validate_image(image), "env": env,
        "securityContext": {"allowPrivilegeEscalation": False, "capabilities": {"drop": ["ALL"]}},
        "resources": {"requests": {"cpu": "100m", "memory": "128Mi"}, "limits": {"cpu": "1", "memory": "1Gi"}},
        "volumeMounts": [{"name": "control-token", "mountPath": "/run/forge-control", "readOnly": True}]}
    volumes = [{"name": "control-token", "secret": {"secretName": "forge-control", "defaultMode": 288}}]
    if initialize:
        for key, value in {"Controller__Enabled": "true", "Controller__Mode": "initialize",
            "Controller__DatasetId": dataset, "Controller__KeyringPath": "/var/lib/forge/keyring",
            "Controller__TokenFile": "/run/forge-control/controller-token", "Forgesystem__DataRoot": "/var/lib/forge",
            "Db__Provider": "sqlserver", "Db__AutoMigrate": "true", "HOME": "/var/lib/forge", "Bootstrap__ProjectId": "forge",
            "Bootstrap__RepoUrl": "https://github.com/Xyrces/Forge.git"}.items():
            env.append({"name": key, "value": value})
        if migration is not None:
            env += [{"name": "Bootstrap__MigrationFromVersion", "value": str(migration["fromVersion"])},
                    {"name": "Bootstrap__MigrationToVersion", "value": str(migration["toVersion"])}]
        env.append({"name": "Db__ConnectionString", "valueFrom": {"secretKeyRef": {
            "name": "forge-initialize-runtime", "key": "sql-connection-string"}}})
        container["volumeMounts"] += [{"name": "state", "mountPath": "/var/lib/forge"},
            {"name": "config", "mountPath": "/app/appsettings.json", "subPath": "appsettings.json", "readOnly": True}]
        volumes += [{"name": "state", "persistentVolumeClaim": {"claimName": "forge-state"}},
                    {"name": "config", "secret": {"secretName": "forge-config", "defaultMode": 288}}]
    else:
        container["command"] = ["python3", "/app/deploy/k3s/release.py"]
        container["args"] = ["--namespace", namespace, "--image", image, "--dataset", dataset, "--operation", operation]
        if environment:
            if not source_sha:
                raise ValueError("named release requires the approved source SHA")
            container["args"] += ["--environment", environment, "--source-sha", source_sha]
    return {"apiVersion": "batch/v1", "kind": "Job", "metadata": {"name": name, "namespace": namespace,
        "labels": {"app": "forge-release", "operation": operation}}, "spec": {
            "backoffLimit": 0 if initialize else 3, "activeDeadlineSeconds": 3600,  # New supervisor pods resume the journal; DDL never retries implicitly.
            "template": {"metadata": {"labels": {"app": "forge-release"}}, "spec": {
                "restartPolicy": "Never", "serviceAccountName": "forge-initialize" if initialize else "forge-release",
                "automountServiceAccountToken": not initialize,
                "securityContext": {"runAsUser": 10001, "runAsGroup": 10001, "fsGroup": 10001,
                                    "runAsNonRoot": True, "seccompProfile": {"type": "RuntimeDefault"}},
                "containers": [container], "volumes": volumes}}}}
