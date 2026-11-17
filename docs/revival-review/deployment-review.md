# Deployment, self-build and container-worker plan

Review and plan only, 2026-11-16. Source: Forge current main `4ac44895c05122ff5705a32b4ceb93e60a6e72e5`. No implementation, installation, deployment, data reset, Linux test, or container run was performed for this report.

## Settled requirements and recommendation

- **Keep Azure SQL.** Fresh workload data is authorized; moving to SQLite is not part of this plan. Previous reports' SQLite suggestions are superseded.
- Use the existing Linux machine initially. The actual deployment is a **user** systemd unit, not the repository's system-service template; see the host report.
- Forge building and improving itself is a useful first project once a small trusted bootstrap makes its basic loop dependable. Do not require the currently broken automation to repair its own prerequisites.
- Given the intended long-term Kubernetes direction, **direct single-node K3s with CPU attempt Jobs is a reasonable preferred target**, after repairing the worker contract and ownership rules below. A Docker-only adapter is an alternative, not a required intermediate implementation. Keep **one active controller**, with a qualified standby for release switching. This is a recommendation for review, not an approved cluster migration.
- Deployment progress must live outside the Forge process being replaced. With K3s, a separate release Job plus Kubernetes workload controllers can provide that execution infrastructure; a new custom host supervisor daemon is not mandatory. Forge-specific drain, SQL ownership transfer, qualification and rollback logic still need implementation.
- On one host, blue-green improves release qualification and rollback. It does not provide machine availability or double GPU capacity. Keep dashboard traffic switching separate from ownership of scheduling and side effects.

## What already exists

| Capability | Actual implementation | Limitation |
|---|---|---|
| Deployment candidate ledger | `DeploymentPipeline/DeploymentStore.cs`; guarded approval used by `Dashboard/DeploymentsEndpoints.cs:154` | Per-candidate duplicate approval is guarded, but not a complete release/rollback coordinator. |
| Build verification | `DeploymentBuildRunner.cs:36`; detached candidate worktree and configured commands | Must pass on supported Linux; current Windows receipt did not reach its commands because of path length. |
| Deployment strategy seam | `IDeploymentExecutor.cs:13` and `DeploymentExecutorFactory` | Existing result has `StillInProgress`, reserved for a future external executor; a good extension point. |
| Linux self-deployment | `SelfHostedSystemdServiceDeploymentExecutor.cs:33` | Publish → symlink swap → restart, **not blue-green**. |
| Installer | `scripts/install-systemd-service.sh:1` | Requires a pre-published release and configuration; installs a root-managed system service. It does not bootstrap the actual user-service layout or provide a complete one-step verified install. |
| Azure SQL | `Core/Db/IDbConnectionFactory.cs:153–191` | Shared DB with `core` registry schema and `proj_<id>` workload schemas; existing data provider need not be replaced. |
| Atomic initial task claim | `Core/IssueStore.cs:2086–2127` | Pending→InProgress update prevents two successful initial claims, but there is no worker ownership lease/fencing on later mutations. |
| In-process concurrency | `Orchestrator/Slots/SlotTable.cs:16` | Semaphores/counters disappear on restart and are not shared among controllers or containers. |
| Durable alternative | `Orchestrator/DurableDispatcher.cs:101` | Still bound to primary stores; accepts then ignores per-project bundle. Not a turnkey distributed worker solution. |
| Existing Docker Compose | `deploy/docker-compose.yml` | Only a DTS emulator, not Forge worker packaging; file explicitly describes memory-only emulator state and a placeholder always-successful health check. |

## Self-deployment defects to resolve before trusting it

1. **Wrong service-manager scope for this installation.** The executor runs `systemctl restart <unit>` without `--user` (`SelfHostedSystemdServiceDeploymentExecutor.cs:98`). The observed deployment is a user service. Service scope and identity must be explicit deployment target properties, verified during onboarding.
2. **A service cannot reliably finish reporting its own synchronous replacement.** The endpoint records Deploying, awaits the executor, then writes Deployed (`DeploymentsEndpoints.cs:167–180`). Restarting the service that contains this request may terminate its process and children before it returns. The code comment asserting no independent helper is needed does not make that final record durable. A supervisor outside the replaced service must own deployment progress and reconciliation.
3. **No qualified standby, health-based cutover or rollback.** The executor swaps the current symlink before restart and never restores the previous target if the new release fails (`:85–108`). Successful service startup is not proof of valid SQL access, scheduler ownership, API compatibility, or an engineering smoke test.
4. **Critical shell results are ignored.** UI copy, link creation and rename return exit codes that are not checked (`:81`, `:91–92`). A failed step can be logged as success and followed by a restart. All release steps need explicit outcomes; build into a staging directory and mark an immutable release complete only after validation.
5. **Publish output can block the publisher.** Its process helper waits for exit before reading redirected stdout/stderr (`:143–145`). A verbose publish can fill a pipe and block while the parent waits. Drain streams concurrently with bounded logs and deadlines. Cancellation must terminate or deliberately hand off the process rather than merely abandon its waiter.
6. **The current in-flight check is not a drain.** Approval checks a SlotTable snapshot (`DeploymentsEndpoints.cs:134–147`), then continues; new work can be admitted immediately afterward. Planning/review/QA are not all represented by these engineering slots. A real drain first closes admission, then waits for or cancels/adopts every run class under a defined policy.

These are code-path findings, not claims that a live self-deployment was exercised and failed during this review.

## Why two active copies are unsafe despite Azure SQL

The DB is already shared and transactional, but process distribution changes invariants outside the initial claim:

- **Recovery assumes exclusive startup.** `Program.cs:1199` runs startup recovery before launching loops. `StartupRecovery` scans all claimed InProgress work and requeues engineering-owned work based on checkpoints, with comments explicitly assuming no runs are alive at boot. Starting green normally while blue owns running work can therefore reclaim blue's work.
- **Initial claim is only partially protected.** `IssueStore.ClaimAsync` uses `WHERE status='Pending'`, preventing duplicate initial claim winners. However, `IsBlockedAsync(id)` opens its own connection before the guarded update (`:2096`); the blocker predicate is not part of the update/transaction despite the comment. A dependency inserted between check and claim can be missed. Fix the eligibility/claim boundary rather than relying on process timing.
- **Later writes are not ownership-conditioned.** `TransitionAsync` reads current metadata separately, then updates by ID (`IssueStore.cs:2145–2166`), without attempt ID, lease epoch, or row version. A paused worker can resume after replacement and overwrite the new attempt's state.
- **Run heartbeats are observability, not fencing.** The orphan reaper uses active run rows and task age (`Core/OrphanedClaimReaper.cs:35–58`). It does not establish a worker lease that every completion/publish must prove it still owns.
- **Capacity and cooldowns are process-local.** Role SlotTable, in-flight registries, model-rate admission and watch launch registries do not establish global limits. Two controllers can double the intended request/job capacity even when they do not claim the same task.
- **Messaging is process-local.** `Messaging/ForgeMessagingExtensions.cs:27` supports only InMemoryTransport; the `servicebus` selection throws. Moving a consumer into another pod does not make it receive the current hint channels.
- **Files are local identities.** `GitWorktreeService.cs:27–32` derives task worktree paths under a local clone; task metadata stores those paths. Git worktrees also reference the owning repository's Git directory. Copying/mounting only the worktree into another filesystem layout is insufficient. Recovery, review, verification, artifact reads and cleanup assume those paths exist on the controller host.
- **Database startup performs DDL.** Store initialization invokes SQL schema setup (`IssueStore.cs:279`) and ordered migration/stamping (`:1622–1648`). Existence guards make retries convenient but do not constitute a distributed migration lock or old/new-version compatibility contract.
- **Secrets include local key material.** The encrypted database values require the DataProtection keyring (`Core/SecretStore.cs:73`). A fresh image with the same SQL connection but a different keyring cannot be assumed to decrypt the same secrets.

Therefore, do not launch two ordinary Forge replicas against the same Azure SQL state and shared worktrees. A Kubernetes replica count of two is not a high-availability implementation of this code.

## Minimum safe same-host blue-green design

Use two release slots and an independently managed release coordinator/front door. On K3s these can be separately named controller Deployments, a Service, and a release Job; with systemd they can be distinct internal ports and an external supervisor. This is a proposed lifecycle, not an existing mode:

1. **Build an immutable candidate.** Pin source SHA, dependency lock/profile, build/test evidence and package/image digest. A failed build never touches the active release.
2. **Migrate once under an exclusive migration operation.** Additive changes must support both active and candidate code. Destructive schema changes require an explicit later contraction or a planned stop; switching the binary backward does not reverse schema/data changes. Preserve Azure SQL as the authority.
3. **Start green without work ownership.** Validate dependencies, schema compatibility, credentials, API/version and read-only readiness. No scheduler, startup recovery, queue mutation, paid model calls, or PR side effects until green is activated. Current startup does not provide this contract; adding a real controller lifecycle is required.
4. **Drain blue.** Stop new admissions across engineering, planning, review, QA, triage and deployment. For the first version, finish or safely cancel/checkpoint in-flight runs before changing ownership. Later independently managed workers can be adopted without cancellation once leases and protocol compatibility exist.
5. **Transfer one controller lease/fencing epoch in Azure SQL.** Green acquires the next epoch; blue relinquishes it and stops. Every state-changing scheduler and publish decision validates active ownership. On one host an external supervisor can additionally enforce only one writer process, but do not confuse a routing switch with this ownership control.
6. **Activate green, reconcile, switch traffic.** Enable mutations and recovery only after ownership transfer; reconcile durable attempts and existing remote PR/artifact state. Publish readiness/version and move the front door after the agreed checks. Keep request retries idempotent.
7. **Observe and roll back deliberately.** Supervisor records the outcome independently of either release. If green fails, revoke its ownership, ensure workers cannot publish under its old epoch, and reactivate compatible blue. Retain previous binary/image and deployment receipts. Do not promise rollback across an incompatible migration.

A short admission pause is a sensible first target. “Zero interrupted agent work” additionally requires remote/external worker attempts and adoption, not just two HTTP listeners. Both slots share one physical host, so hardware/power failure remains outside the protection offered here.

## Minimum worker contract for container Jobs

Separate **task planning/state ownership** from **executing an attempt**. Keep the existing `IAgentRunner` role-level contract where useful, but introduce an execution boundary beneath/around it that can run the same attempt locally or as a container job. Do not put a full scheduler and recovery sweep in every agent container.

Each attempt should carry:

- Fresh dataset/instance ID, project ID, task ID, immutable attempt ID, controller epoch, role, source/base SHA and accepted plan/input hashes.
- Pinned runtime/toolchain image and model configuration, allowed tools/network/paths, resource class, deadline and budget reservation.
- Leased worker ownership with heartbeat, expiry, cancellation and idempotent terminal result; progress/log cursor and content-addressed artifact manifest.
- Explicit outputs: commits/patch, verification receipts, source identity, produced artifact hashes, actual model usage and unresolved findings. No success inferred from a container exiting zero alone.

**Azure SQL additions:** durable attempt/lease records; guarded state transitions with version/epoch; admission reservations for roles, spend and the renderer/export resource; and durable side-effect intent/result records where needed. Use DB time for lease decisions. Do not give ordinary workers unrestricted IssueStore/secret-store write authority. Workers report results through the controller; the controller remains responsible for PR creation, acceptance and merge.

**Stale workers:** a lease expiry is not enough if an old process can still push. The minimum pilot keeps publishing in the controller, gives workers only the credentials they need, and accepts output only for the current attempt/epoch. Controller validates ownership immediately before each externally visible side effect and reconciles uncertain results. Multi-controller high availability requires careful fencing of the publisher as well; a simple “check lease then call GitHub” still has a race if an old controller is merely paused rather than stopped.

**Workspace choice:** for one-host containers, a stable per-attempt volume mounted at a predictable container path is simplest, with correct UID/GID and deliberate Git ownership. Prefer independent attempt clones plus a read-only object/dependency cache for eventual remote workers. If retaining git worktrees, mount the owning Git directory consistently and coordinate its modifications; do not let several containers prune/reset the same repository metadata. Use durable artifact storage and immutable evidence refs so a deleted container does not delete acceptance evidence. A single-node local volume is still tied to that machine; moving to multiple nodes requires deliberate storage placement or remote artifact access.

**Execution supervision:** expose start/status/cancel/logs/result rather than keeping a request open for a complete game export. Enforce deadlines on process trees, preserve outputs before cleanup, and reconcile lost responses. Docker exposes containers, mounts, resource controls and exit status but does not supply Forge's acceptance/state semantics. [Docker running containers](https://docs.docker.com/engine/containers/run/).

**Messaging:** with one active controller and worker callbacks/polling, the existing in-process hint bus can remain inside the controller. A distributed event transport is not mandatory for the first Kubernetes or Docker worker. If introducing cross-process consumers later, add durable delivery/outbox or equivalent reconciliation; preserve per-topic consumer semantics and idempotent handlers.

**Native TimeLoop QA:** initially keep a dedicated host worker under the qualified desktop identity with one renderer/export lease. CPU code/test containers can come first. Containerized GPU/display/plugin support must be demonstrated on the actual Radeon/desktop setup rather than presumed. Kubernetes GPU scheduling requires vendor drivers/device plugins and explicit resource requests; its documentation does not establish that this particular laptop/display stack is qualified. [Kubernetes GPU scheduling](https://kubernetes.io/docs/tasks/manage-gpus/scheduling-gpus/).

## Platform comparison

| Choice | Best immediate use | What it does not solve | Recommendation |
|---|---|---|---|
| systemd controller and host workers | Fastest trusted bootstrap and native GPU verification on the existing machine | Reproducible worker environments, worker isolation, automatic multi-host scheduling | Useful bootstrap/fallback and initial native QA host; does not have to become a permanent custom worker supervisor. |
| Docker/OCI attempt containers | Pinned CPU build/test/engineering environments with fewer cluster concepts | SQL leases, durable acceptance, task ownership, GPU qualification, self-deploy coordination | Reasonable smallest-host alternative if cluster operation is unwanted. Building this adapter first adds throwaway work if K3s is already the intended destination. |
| Single-node K3s with Kubernetes Jobs | CPU attempt lifecycle, replacement, placement and a consistent path to later worker machines; independent release Jobs | Exactly-once business work, safe shared DB writers, correct evidence, old/new schema compatibility | Preferred proposed target given the stated long-term direction, after protocol/fencing prerequisites. Use Jobs for bounded attempts, with one active Forge controller. |

Kubernetes Jobs manage completion and replacement Pods; applications must tolerate retries and possible repeated execution. Consequently a Job success flag cannot replace a fenced Forge attempt/result contract. [Kubernetes Jobs](https://kubernetes.io/docs/concepts/workloads/controllers/job/). Kubernetes on this same single host can improve packaging/scheduling consistency, but it would add operational components without removing the machine failure domain.

K3s packages Kubernetes and a container runtime, so this direction does not require operating a separate Docker daemon first. Its default internal datastore is SQLite; an embedded-etcd configuration can avoid SQLite even for cluster metadata. **Forge application state remains in Azure SQL** regardless; cluster metadata is separate, and Azure SQL is not being proposed as the K3s datastore. A single etcd server does not provide high availability. [K3s overview](https://docs.k3s.io/), [embedded etcd setup](https://docs.k3s.io/datastore/ha-embedded).

### Staged direct-K3s proposal

1. Repair and validate Forge's core loop using an isolated test deployment on the existing Linux host; define the attempt/result and controller standby contracts before changing deployment infrastructure. Do not patch individual old production tasks around the loop. Stop/disable the old service before resetting Forge-owned SQL state.
2. Package CPU workers as pinned OCI images and implement a single Kubernetes Job adapter. Start with bounded concurrency, explicit resources/deadlines, SQL attempt identity and immutable result receipts. Retain native desktop QA outside the cluster under the same protocol until its graphics stack is qualified.
3. Move the controller into the cluster only after SQL lease fencing, standby-without-recovery, migration serialization and secret/keyring persistence work. Use two named release slots with only one allowed to dispatch. Ordinary rolling replacement can overlap Pods; it is not the Forge ownership protocol. [Kubernetes Deployments](https://kubernetes.io/docs/concepts/workloads/controllers/deployment/).
4. Add a narrowly privileged release Job that survives replacement of either controller slot. It owns the durable deployment operation, drains the active controller, qualifies the candidate, transfers SQL ownership and switches the Service. Reconcile interrupted release Jobs from durable operation records; do not substitute Kubernetes readiness for application qualification.
5. Wrap the pinned cluster prerequisites, images/manifests, Azure SQL connectivity, keyring/secret setup, native-worker registration and smoke checks in one repeatable installer. Test rollback and repair on this single host before adding nodes or GPU scheduling.

The tradeoff is concrete: Docker-first reduces initial cluster operations but requires Forge to supply more worker/deployment supervision itself. Direct K3s introduces cluster maintenance now while reusing Kubernetes lifecycle infrastructure and avoiding a second executor implementation. Neither path removes the application correctness prerequisites; direct K3s is justified by the intended destination, not by a claim that containers repair those defects automatically.

## Forge builds itself first: bounded route

1. **Trusted bootstrap, performed interactively on feature branches:** normal audit-enabled Linux build, project configuration/verification fix, genuine model/tool adapter, minimum planning/publish/recovery correctness and fresh Azure SQL provisioning. No old-data import; no new SQLite option.
2. **Register Forge as the first clean project:** actual repo instructions/territories, required checks, conservative concurrency and held merge/deployment policy. Choose a small real defect with testable acceptance. Prove queue→plan→edit→tests→PR→review at the correct SHA before broad backlog work.
3. **Let Forge implement bounded self-improvements:** supply accepted specifications and deterministic regression scenarios from the review. Keep an independent operator/reviewer acceptance boundary for changes to state transitions, gates, budget, installer and deploy supervisor. Forge's ability to emit a patch is not authority to weaken its own controls.
4. **Build the installer/release coordinator as a reviewed artifact:** one operator entry point validates Azure SQL identity/connectivity, selected K3s or service profile, release identity, directories/keyring, prerequisites and readiness; supports repeatable repair/update and clear rollback. Install native-worker capability separately from controller privilege. Fresh-data authorization does not imply permission to recreate infrastructure or rotate identities silently.
5. **Prove deployment by injected failure:** candidate fails build, fails readiness, dies during cutover, loses SQL, or loses a worker; ensure one active writer, honest deployment status and preserved rollback. Then prove Forge can build a new Forge release and the independent supervisor can promote it.
6. **Bring TimeLoop second:** reuse the same durable attempt contract and Azure SQL ownership. Add its instruction/artifact/native acceptance requirements rather than treating self-build success as game-workflow qualification.

Fresh state should have a new **dataset/instance identity**, even if project IDs are reused. Azure SQL issue sequences restart, while old Git branches/PRs/worktree paths and external generation jobs may remain. Namespace attempts and reconcile intended remote outputs so a fresh `task-1` cannot adopt an unrelated historical `agent/task-1` PR. A clean Azure SQL database on the existing service, or a deliberately reset and verified schema set, is an implementation choice; data disposal is settled and need not be asked again.

## Definition of done for this direction

- Azure SQL retained; fresh registry/workload identity established with no old queue replay.
- One active controller at all times, including standby startup, failed cutover and rollback.
- Worker kill/retry/late completion cannot produce duplicate accepted state or publish stale output.
- Supported-host normal build/tests and deterministic engineering loop pass with exact stage evidence.
- Supervisor survives Forge replacement and records actual release outcome.
- One command installs/upgrades the selected deployment profile reproducibly and reports actionable failures; no unverified readiness or ignored exit codes. Any systemd bootstrap/fallback explicitly distinguishes **user/system** scope.
- Kubernetes CPU Jobs and the host-native worker share the same attempt/result protocol; cluster infrastructure never becomes a second business state authority.

These are proposed acceptance conditions. None of this deployment/worker design exists merely because the current code has SQL Server support, Docker Compose, or a DurableDispatcher class.
