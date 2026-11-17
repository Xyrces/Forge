"""Small JSON Kubernetes bundle; no templating engine or third-party Python dependency."""
from release import validate_image


def obj(kind, name, **extra):
    return {"apiVersion": "v1", "kind": kind, "metadata": {"name": name}, **extra}


def foundation(namespace, storage="40Gi", pull_secret=None):
    items = [obj("Namespace", namespace),
             obj("PersistentVolumeClaim", "forge-state", spec={"accessModes": ["ReadWriteOnce"],
                 "resources": {"requests": {"storage": storage}}})]
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


def job(namespace, image, dataset, operation, name, initialize=False):
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
            "Bootstrap__RepoUrl": "https://github.com/Xyrces/Forge.git",
            "Bootstrap__WorkerPrincipal": "forge-k3s-worker"}.items():
            env.append({"name": key, "value": value})
        env.append({"name": "Db__ConnectionString", "valueFrom": {"secretKeyRef": {
            "name": "forge-initialize-runtime", "key": "sql-connection-string"}}})
        container["volumeMounts"] += [{"name": "state", "mountPath": "/var/lib/forge"},
            {"name": "config", "mountPath": "/app/appsettings.json", "subPath": "appsettings.json", "readOnly": True}]
        volumes += [{"name": "state", "persistentVolumeClaim": {"claimName": "forge-state"}},
                    {"name": "config", "secret": {"secretName": "forge-config", "defaultMode": 288}}]
    else:
        container["command"] = ["python3", "/app/deploy/k3s/release.py"]
        container["args"] = ["--namespace", namespace, "--image", image, "--dataset", dataset, "--operation", operation]
    return {"apiVersion": "batch/v1", "kind": "Job", "metadata": {"name": name, "namespace": namespace,
        "labels": {"app": "forge-release", "operation": operation}}, "spec": {
            "backoffLimit": 0 if initialize else 3, "activeDeadlineSeconds": 3600,  # New supervisor pods resume the journal; DDL never retries implicitly.
            "template": {"metadata": {"labels": {"app": "forge-release"}}, "spec": {
                "restartPolicy": "Never", "serviceAccountName": "forge-initialize" if initialize else "forge-release",
                "automountServiceAccountToken": not initialize,
                "securityContext": {"runAsUser": 10001, "runAsGroup": 10001, "fsGroup": 10001,
                                    "runAsNonRoot": True, "seccompProfile": {"type": "RuntimeDefault"}},
                "containers": [container], "volumes": volumes}}}}
