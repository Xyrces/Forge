# Live release and rollback acceptance

Run only with operator coordination. Keep all work/merge gates held and the work
queue empty; release qualification needs no model calls. Prefer paid calls disabled
when that is the installation profile. Use the actual reviewed image receipt and digest throughout.
Do not fabricate a failed build receipt or relax application readiness.

## Successful self-deployment

1. Save current Service selector, old controller pod UID, authenticated controller
   status (instance, epoch, lease owner, admission), and external worker Job UIDs.
2. Register the reviewed CI receipt using `catalog.py`. Request a deployment for
   its exact source through the dashboard API; verify `BuildPassed` came from that
   receipt. Approve through the same API. Do not run repository builds on controller.
3. Observe independent release Job and ConfigMap `forge-release-<deployment-id>`.
   Record `qualified`, `old-stopped`, `activated`, `committed` events from Job logs.
4. Verify old pod UID is absent, new controller owns SQL with a higher epoch and
   admission open, dashboard selector targets the new slot, and deployment-list
   polling records `Deployed`. Existing worker Job UIDs must survive; no controller
   owner references may exist on them.

## Controlled failure after old-controller stop

The coordinated live trial on 17 November 2026 used a more deterministic
configuration fault than the timing-sensitive network-only observer below:

1. Save the inactive control Service selector and candidate deployment data-root
   setting. Block only that inactive Service before approving the real candidate.
   This prevents the release Job from qualifying the initial candidate prematurely.
2. After the candidate Deployment exists, set its `Forgesystem__DataRoot` to
   `/proc/forge-proof-readonly`. Wait for the replacement candidate pod and verify
   directly that it is standby/ready with admission closed; retain its UID. The
   ordinary standby schema/config checks remain in effect.
3. Restore the candidate control Service selector. The real release Job qualifies
   standby, drains/deletes the previous controller, then attempts activation.
   Application composition genuinely fails when creating the data root; the
   terminal failed status triggers normal rollback immediately.
4. Observe `old-stopped`, the actual composition exception, `rolling-back`, and
   `rolled-back`. Verify the previous slot returns with a new pod UID and higher
   SQL epoch, admission open, and deployment `DeployFailed`. Restore the dormant
   candidate's data-root setting after rollback, keeping its replica count zero.

This fault changes candidate configuration, not its image or CI receipt. It is an
operator qualification procedure, not a runtime feature or automatic action.
Detailed observed evidence is in
[`docs/revival-review/live-qualification.md`](../../docs/revival-review/live-qualification.md).
The network-only variant below remains useful but can miss its timing window.

This proof introduces an external control-Service network fault. It changes no
image, source receipt, SQL permission, or readiness implementation. Coordinate one
release at a time and retain the Job, journal, pod UIDs and timestamped evidence.

Start a short-lived, loopback-only Kubernetes API proxy on the host:

```sh
sudo k3s kubectl proxy --address=127.0.0.1 --port=8001
```

The observer below uses that local proxy; it contains no token. Set `operation` to
the deployment candidate ID before approving it, `candidate` to the inactive slot,
and `previous` to the current slot. Start this observer first, then approve the
real candidate. Kubernetes HTTP polling is every 10ms plus actual API latency.
The intercept window is `qualified`, which precedes the old-controller drain.
An observer that misses that window has not proved rollback and must report that.

```python
import json, time, urllib.request, urllib.error
namespace = "forge"
operation = "REPLACE_WITH_DEPLOYMENT_ID"
candidate, previous = "green", "blue"  # inspect current Service first
base = f"http://127.0.0.1:8001/api/v1/namespaces/{namespace}/"
def api(path, patch=None):
    request = urllib.request.Request(base + path,
        data=None if patch is None else json.dumps(patch).encode(),
        method="GET" if patch is None else "PATCH",
        headers={"Content-Type": "application/json" if patch is None else "application/merge-patch+json"})
    with urllib.request.urlopen(request, timeout=10) as response:
        return json.load(response)
def pods(slot):
    return api("pods?labelSelector=app%3Dforge-controller%2Cslot%3D" + slot)["items"]
def evidence(event, **details):
    print(json.dumps({"time": time.time(), "event": event, **details}), flush=True)
service = "services/forge-" + candidate
original = api(service)["spec"]["selector"]
old_uids = [p["metadata"]["uid"] for p in pods(previous)]
evidence("before", oldPodUids=old_uids)
deadline, injected, saw_stopped = time.monotonic() + 1800, False, False
while time.monotonic() < deadline:
    try:
        journal = api("configmaps/forge-release-" + operation)["data"]
    except urllib.error.HTTPError as error:
        if error.code != 404: raise
        time.sleep(.01); continue
    phase = journal["phase"]
    if not injected:
        if phase == "qualified":
            api(service, {"spec": {"selector": {"app": "forge-controller", "slot": "proof-blocked"}}})
            injected = True
            evidence("control-route-blocked", phase=phase)
        elif phase in ("old-stopped", "activated", "committed", "rolling-back", "rolled-back"):
            raise RuntimeError("Missed qualification window; trial is not rollback proof")
    if injected and not pods(previous) and not saw_stopped:
        saw_stopped = True
        evidence("old-pods-deleted")
    if phase == "committed":
        if injected: api(service, {"spec": {"selector": original}})
        raise RuntimeError("Fault did not interrupt cutover; trial is not rollback proof")
    if phase == "rolled-back":
        api(service, {"spec": {"selector": original}})
        restored = [p["metadata"]["uid"] for p in pods(previous)]
        evidence("rolled-back", oldStopped=saw_stopped, restoredPodUids=restored,
                 candidatePods=len(pods(candidate)))
        assert injected and saw_stopped and restored and not set(old_uids).intersection(restored)
        assert not pods(candidate)
        break
    time.sleep(.01)
else:
    raise RuntimeError("Timed out: inspect retained journal; candidate control route remains blocked")
```

Expect the independent release Job to fail after completing rollback. This is the
expected failure receipt, not a successful Job. Verify journal `rolled-back`, old
slot restored with a new pod UID and SQL epoch, admission reopened, dashboard route
restored, candidate pods absent, and SQL deployment status `DeployFailed` after
list reconciliation. Record authenticated statuses separately without saving bearer
tokens. Stop the loopback proxy after evidence capture. If the observer times out,
inspect the journal before restoring the saved Service selector or resuming a Job.

The proof uses the normal release timeout (600s by default), so a controlled fault
can take several minutes. Do not shorten readiness or old-pod deletion checks.
