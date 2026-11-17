# Budget39 maintenance command plan

The supervised execution subsequently completed; see [live qualification](budget-live-qualification.md). This document preserves the reviewed command plan and its unattended-upgrade limitations.

**Prepared only; none of these live commands were executed by this task.** Root/operator owns execution after both immutable image receipts and release approval. This plan restores the bridge on SQL39 first, then uses the normal deployment API to release the budget39 image. It preserves the existing release journal and slot-selection protocol.

## Inputs and preflight

Use the exact qualified bridge and budget source/image receipts, existing namespace `forge`, dataset UUID from the protected bootstrap directory, and new unique operation IDs. Do not substitute tags, overwrite receipts, change the $25 ceiling, or infer a null SQL owner from elapsed time. Keep receipts and phase observations in a private maintenance directory (`umask 077`).

```bash
set -euo pipefail
umask 077
NS=forge
BOOT=/home/jtn5016/.config/forge-bootstrap
# BRIDGE_IMAGE, BRIDGE_SOURCE, BUDGET_IMAGE, BUDGET_SOURCE and OP must be
# populated from reviewed source-bound receipts before running any mutation.
: "${BRIDGE_IMAGE:?}" "${BRIDGE_SOURCE:?}" "${BUDGET_IMAGE:?}" "${BUDGET_SOURCE:?}" "${OP:?}"
K=(sudo /usr/local/bin/k3s kubectl -n "$NS")
"${K[@]}" get service forge -o json
"${K[@]}" get configmap forge-release-lock -o json
"${K[@]}" get pods -l app=forge-controller -o json
"${K[@]}" get pods -l app.kubernetes.io/name=forge-worker -o json
/home/jtn5016/.dotnet/dotnet run --project /tmp/forge-sql-proof/SqlProof.csproj --no-build
```

The helper must parse/assert, not merely display, these observations: active route is blue or green and its Deployment uses the qualified bridge digest; no other release operation is active; dataset and ceiling match the expected immutable values; schema38 is present before migration; runtime admission/owner belong to that bridge; all work is idle or explicitly drained. Retain worker-attempt snapshots. Do not delete/reset a release lock or journal to force progress. A stale lock is resolved by the existing release ownership rules, not this checklist.

Before maintenance, register both valid image receipts through the existing catalog mechanism, and deploy the bridge normally via API while the database is38. Do not migrate while the original exact38 binary remains the active rollback floor.

## Explicit initializer configuration update

The schema39 initializer now requires `Bootstrap.ControllerPrincipal=forge-k3s-runtime`; worker remains `forge-k3s-worker`. Privately read/modify only that configuration property in `$BOOT/appsettings.json`, preserve every other value, and update the existing `forge-config` Secret to the exact same bytes. The host helper must build the patch in memory or a mode0600 file and pass it over stdin; never print the config, secret payload, SQL strings, tokens, or base64 data. Preserve a mode0600 original for review/recovery.

This is an explicit reviewed configuration operation: `install.py.immutable_secret` deliberately refuses implicit replacement. Do not bypass that protection by deleting the Secret. Confirm only the intended nonsecret property differs, and that budget/dataset/state-authority/gate settings remain unchanged. Existing bridge code ignores this new bootstrap property; the schema39 initializer consumes it.

## Drain and prove writers stopped

Capture the route as `PREVIOUS` and verify it is exactly `blue` or `green`. Use an owned temporary local port-forward to `svc/forge-$PREVIOUS:9090`, bound to127.0.0.1 only. A private Python helper obtains the control token into memory from the existing Kubernetes Secret and calls:

```text
POST http://127.0.0.1:<owned-local-port>/api/controller/drain
Authorization: Bearer <in-memory token>
{"operationId":"<OP>-maintenance-drain"}
```

Poll authenticated status at bounded intervals until `admissionOpen=false`, `activeRuns=0`, and `leaseOwner=null`. Independently repeat the read-only SQL ownership query and require `owner_instance_id IS NULL`. Drain POST is asynchronous: do not proceed solely because it returned202. If ownership cannot be proved released, stop the operation for investigation; do not manually clear SQL ownership.

Only after successful drain:

```bash
"${K[@]}" scale deployment "forge-$PREVIOUS" --replicas=0
"${K[@]}" wait --for=delete pod -l app=forge-controller --timeout=180s
"${K[@]}" get pods -l app=forge-controller -o json
"${K[@]}" get pods -l app.kubernetes.io/name=forge-worker -o json
```

Require zero controller pods, including terminating pods, and zero surviving worker pods. Query durable `worker_attempt` state read-only and require no queued/running work. Do not delete working pods merely to satisfy the predicate without following the established cancellation/cleanup contract. Run the SQL owner proof again immediately before initializer launch. The initializer itself holds `forge-schema-migration` and rejects a non-null owner, providing the final barrier.

**Leave the public Service selector pointing to the previous bridge slot.** Do not change it to `none`; it is how the independent release selects the previous image for rollback. Preserve the zero-replica bridge Deployment/template.

## Generate/review the initializer Job, then execute once

Use `deploy/k3s/manifests.py` from the reviewed source checkout to render the existing initializer manifest. This is the reviewed factory, not a new migration shell:

```python
# Render into a private reviewed JSON file; this snippet does not apply it.
manifest = job(namespace, budget_image, dataset_id, operation_id,
               "forge-init-" + operation_id, initialize=True)
```

Review exact image digest, operation/dataset, `forge-initialize-runtime` SQL Secret, `forge-initialize` service account, `automountServiceAccountToken=false`, and existing PVC/config mounts. The manifest must not contain controller or worker SQL credentials. Preserve source/image receipt association in the maintenance record.

```bash
"${K[@]}" apply -f "$PRIVATE_RECEIPTS/initializer-job.json"
"${K[@]}" wait --for=condition=complete "job/forge-init-$OP" --timeout=600s
"${K[@]}" logs "job/forge-init-$OP" > "$PRIVATE_RECEIPTS/initializer.log"
```

A failed/unknown Job is not successful migration. Inspect schema versions and the idempotent migration state before a controlled retry; do not blindly create concurrent initializers. Verify `core` and `proj_forge`39, old ledger rows/totals unchanged, both reconciliation tables present, and runtime/worker SELECT allowed but INSERT/UPDATE/DELETE denied on those tables. Do not perform the separate live operator-reported reconciliation merely because schema migration succeeded; retain its separately authorized exact request manifest and immutable evidence hash.

## Restore bridge on39 using the independent release journal

The dashboard/API is unavailable during the gap. Render an ordinary **bridge-image** release Job using the existing `job(..., initialize=False)` factory with a fresh restore operation, append `--source-sha <BRIDGE_SOURCE>` (supported by the release runner), and bind the reviewed receipt in the maintenance record. It must use the release service account/control token only, never initializer SQL credentials.

```bash
"${K[@]}" apply -f "$PRIVATE_RECEIPTS/bridge-restore-job.json"
"${K[@]}" wait --for=condition=complete "job/$BRIDGE_RESTORE_JOB" --timeout=1200s
"${K[@]}" get configmap "forge-release-$BRIDGE_RESTORE_OP" -o json
```

Require journal phase `committed`, bridge active status at39, matching dataset/owner/epoch and a healthy public route. The release runner chooses the opposite slot, qualifies it, proves the old slot stopped, acquires ownership and routes normally. Do not hand-activate a controller, change the route directly, or manufacture a deployment candidate/queue row while the API is down. This independent release Job is the existing installer-style release path; it has a release journal but is not an API-recorded deployment candidate.

If bridge restoration fails, leave the failed-operation journal intact. Investigate rather than activating the original schema38 image or downgrading SQL. The bridge's budget view deliberately ignores reconciliation credit and may conservatively deny paid work; keep work held until budget39 is active.

## Release budget39 normally through the API

With the bridge healthy on39, submit/review/approve the budget39 deployment using the existing deployment API and catalog-bound immutable source. Root handles these normal API calls. The independent release can now qualify budget39 before draining the bridge, and rollback to the bridge remains valid on39.

Record final route, instance/pod UID, epoch, schema, runtime deny checks, budget snapshot, and completed release journal. Only then proceed with the separately approved exact-manifest reconciliation or paid-work gate changes. No automatic budget increase, old-row rewrite, or release-time model request is part of this plan.

## Scope limitations

This is a supervised maintenance plan using existing primitives, not an implemented all-in-one migration command. The absence of a durable journal across drain/migrate/bridge-restore is still a limitation; retain a private phase receipt and stop on ambiguous state. Full unattended upgrades should make that narrow sequence a persisted operation supervised outside the retiring controller. The existing no-overlap, explicit SQL ownership, immutable-image and source qualification requirements remain unchanged.
