# Forge on single-node K3s

This bundle starts with Forge as the only project and Azure SQL as application truth.
K3s uses embedded etcd. It does not install SQLite as the Forge or cluster database,
does not require a Docker daemon on the host, and does not configure TimeLoop.

## Build an immutable image

The `controller-image.yml` workflow runs on pushes to `main` and
`codex/forge-first-k3s`, and also accepts a reviewed source ref and a
digest-pinned .NET 10 SDK base image through manual dispatch. It builds the
source branch that can later improve Forge, passes private NuGet credentials
as a BuildKit secret, and reports a
GHCR `@sha256:...` digest. Before publication, the image build runs the complete
solution build and offline C# suite, excluding the three explicitly live provider/
media test classes; the workflow also runs the Python release tests. The source
still requires PR review and the normal CI/e2e gate. A published image is not proof
of a successful live controller handover or rollback.
The workflow is not dispatched automatically by these scripts.

Local/CI build equivalent (outside the K3s host, with BuildKit available):

```sh
docker buildx build --file deploy/k3s/Dockerfile \
  --build-arg SDK_IMAGE='<registry/sdk@sha256:verified-digest>' \
  --build-arg SOURCE_REVISION="$(git rev-parse HEAD)" \
  --secret id=nuget_config,src=/private/path/authenticated-nuget.config \
  --tag '<registry/forge:source-sha>' --push .
```

Keep the credential file outside the source/build context. Resolve and record the
published image digest; the installer rejects mutable tags. Registry permissions
remain private. For private pulls, create an image-pull Secret in the chosen
namespace using the operator's credential process, then pass its name with
`--pull-secret`. Alternatively, `--registry-config /private/path/registry.json`
installs a Docker-format `auths` file as the `forge-registry` pull Secret during
first installation; the file contains credentials and must stay outside the repo.
The service accounts can use that pull Secret without gaining
API permission to read all Secrets.

## Prepare the private install files

Keep these files in a directory outside the checkout, readable only by the
installing user. The root [installation quickstart](../../README.md#installation-quickstart) shows how
to supply them to the installer.

Prepare a dedicated Azure SQL database and allow connections from the Linux
host. A database administrator must create three distinct database users and
authorize their access before installation.

| File | Identity and required access |
| --- | --- |
| `sql-controller.txt` | The controller user, such as `forge-k3s-runtime`, with application read/write permissions on Forge's registry and workload schemas. |
| `sql-initialize.txt` | The initializer user, with authority to create and migrate Forge schemas, create objects, and grant or deny the runtime permissions. |
| `sql-worker.txt` | The worker user, such as `forge-k3s-worker`. The initializer installs its scoped schema, task, attempt, and budget permissions. |

Each file contains one connection string targeting the same database. For SQL
password authentication, the shape is

```text
Server=tcp:<server>.database.windows.net,1433;Initial Catalog=<database>;User ID=<user>;Password=<password>;Encrypt=True;TrustServerCertificate=False;
```

Use the connection-string escaping required by your credentials. The installer
creates the application schemas through the initializer connection. Database
creation, user creation, firewall rules, and the controller's baseline grants
are administrator setup. The controller and worker must have runtime privileges
without DDL authority. See [SQL identity separation](#sql-identity-separation-and-trust-boundary)
for the enforced permissions and credential boundaries.

Copy [appsettings.first-install.json](appsettings.first-install.json) to the
private directory as `appsettings.json`. Set `bootstrap.controllerPrincipal`
and `bootstrap.workerPrincipal` to the exact database user names above.
The template starts with paid model calls disabled. Keep SQL connection strings
in the three separate files.

Create `worker-packages.json` with a GitHub account and token that can read
the private Talaria packages. The account needs package access and the token
needs `read:packages` permission.

```json
{"username":"<github-user>","token":"<package-read-token>"}
```

For a private controller image, create `registry.json` using the Docker auth
format. The `auth` value is the base64 encoding of the registry username,
a colon, and its package-read token. The account must have access to the chosen
GHCR image. A machine-local credential-helper entry cannot be used by the installer.

```json
{"auths":{"ghcr.io":{"auth":"<base64-encoded-username-and-token>"}}}
```

Review the private configuration before the first apply. The ordinary release
path requires the same configuration and credential bytes on later installs.
Changing them requires an intentional configuration-maintenance operation.
For autonomous work, configure the provider and spending authorization, the
project's publishing and review identities, and its deployment targets before
releasing the initial holds. [Runtime configuration](../../docs/runtime-reference.md)
· [GitHub App identities](../../docs/github-app-identities.md)
· [Deployment environments](../../docs/deployment-environments.md)

## One installation/release entrypoint

Run as the existing Linux desktop/service user (not a root login), with Python 3
and sudo available. The installer checks both system and current-user Forge
services; use the actual service owner's session so that check is meaningful.
Supply a private connection-string file and private appsettings file. Do not put
credentials in command arguments or checked-in examples. The SQL connection must
target the separately prepared fresh Forge-owned database/state, not an unrelated
service's schemas. The installer never drops data or resets an existing dataset.
Set `State.WriteAuthority=true` in the private appsettings file so lifecycle
violations are reported in authority mode. Keep all five operator stage gates
held until the intended pilot is ready; holding sprint assembly does not pause
engineering tasks already assigned to the active sprint.

```sh
python3 deploy/k3s/install.py \
  --image '<registry/forge@sha256:verified-image-digest>' \
  --source-sha '<full-reviewed-40-character-commit-sha>' \
  --image-receipt '/private/path/forge-image-receipt.json' \
  --dataset '<fresh-UUID>' --operation 'initial-forge' \
  --sql-file '/private/path/sql-controller.txt' \
  --initialize-sql-file '/private/path/sql-initialize.txt' \
  --worker-sql-file '/private/path/sql-worker.txt' \
  --worker-packages-file '/private/path/worker-packages.json' \
  --config '/private/path/appsettings.json' \
  --k3s-version '<pinned-vX.Y.Z+k3sN>' --k3s-sha256 '<verified-binary-sha256>'
```

Without `--apply` this prints the nonsecret plan and changes nothing. Append
`--apply` for the actual install. If the old Forge service is running, installation
refuses unless `--retire-legacy` explicitly permits stopping it. That action does
not disable/delete its service, config, or state, and does not replay its backlog.
The operator must prevent the retired service from being restarted later against
the fresh deployment's SQL database. A failed migration never silently starts it.

The initial K3s binary is downloaded from the exact version, SHA256 checked, and
installed with a systemd unit and `cluster-init: true`. Existing K3s is accepted
only when its embedded-etcd member data exists. Existing ambiguous configuration
fails for operator reconciliation. No remote installation script is piped to a shell.
No swap, firewall, SSH, Wi-Fi, power, DNS or unrelated host settings are changed.

Foundation resources include namespace-local service accounts/RBAC, one state PVC,
internal slot-control services and a cluster-private dashboard Service. Control port 9090
requires the mounted bearer token at `/run/forge-control/controller-token` for
status/drain/activate; only `/healthz` is
unauthenticated. The dashboard has no authentication and must not be exposed as a
LAN NodePort. From a workstation, use the [quickstart SSH tunnel](../../README.md#3-install-and-open-the-dashboard)
to the service's ClusterIP on port 8080. For a browser on the Linux host itself,
run `sudo /usr/local/bin/k3s kubectl -n forge port-forward --address 127.0.0.1 svc/forge 8080:8080`.
No ingress or TLS identity is silently provisioned.

Initial deployment runs `Controller__Mode=initialize` in a separate Job, which must
finish before standby starts. It creates/validates the SQL controller dataset and
Forge registry, with the application's exclusive migration lock. Standby does not
run migrations, recovery or schedulers. No tasks are enqueued by the installer.
The application's bootstrap must retain held work/merge policy until acceptance.

The PVC is mounted at `/var/lib/forge` for controller and worker paths. DataProtection
keys persist at `/var/lib/forge/keyring`. Do not delete it when rotating a controller.
Both release slots share the volume on this single node; this is not multinode HA.

## Independent release and rollback

`release.py` runs in a Job with no owner reference to either controller. It uses
the Kubernetes API directly: no kubectl executable, Docker socket or privileged
pod is needed. It records phases in a per-operation ConfigMap and serializes
release jobs with a compare-and-swap lock bound to the release pod UID. A replacement
job may take over only after the previous owner pod is absent or terminal.

1. Prepare the other slot with the exact image and standby-only startup.
2. Require authenticated application readiness, including SQL schema compatibility.
3. Close old admission and wait for every local run class to drain.
4. Scale the old slot to zero and wait for its pods to be deleted, not merely unready.
5. Activate the candidate through SQL ownership acquisition; require active readiness.
6. Switch dashboard traffic and record committed.

On failure, stop candidate pods before restoring the previous controller. A
rollback failure remains recorded as `rolling-back`; a replacement job resumes
that rollback. The scripts never claim rollback succeeded merely because a patch
request returned. A committed operation with identical inputs is idempotent;
different inputs under the same operation ID are rejected. A completed rollback
requires a new operation ID after its cause is corrected.

No automatic TTL removes jobs/evidence. Preserve logs and journals before explicit
cleanup. External CPU attempt jobs have no controller owner references and can
outlive controller replacement; acceptance of their output remains governed by
the application's durable attempt/epoch contract. Kubernetes program completion
is not business acceptance.

For a normal same-schema upgrade, rerun the installer with the same dataset,
namespace and credential/config bytes, a new operation ID, and a new exact image
digest. Configuration/credentials are immutable through this release path; it
refuses silent in-place replacement. Schema-changing upgrades need a separately
reviewed exclusive migration sequence while controllers are stopped; this bundle
does not claim automatic rollback across incompatible database changes.

If the caller loses its terminal, inspect the independently running Job and
`forge-release-<operation>` journal rather than issuing a blind second release.
Initialization failures retain their Job; correct the cause and use a new
operation ID, without changing the dataset or dropping data implicitly.

## Tests

```sh
python3 -m unittest discover -s tests/deployment -v
```

The fake API tests check ownership, drain/stop/activate ordering, terminating-pod
barriers, rollback, conflicting inputs, retained receipts and restricted RBAC.
They do not claim a live K3s installation, native Linux build, Azure SQL migration
or image pull has passed. Those are explicit supported-host acceptance gates.

## Existing deployment approval surface

`DeploymentKind.Kubernetes` connects the existing deployment candidate/approval API
and dashboard to the independent release Job. Per-project deployment configuration
must be in top-level `Deployments.forge` and contain `Kind: Kubernetes`,
`KubernetesNamespace`, `KubernetesDatasetId`, and
`KubernetesImageCatalog: forge-image-catalog`. This reads an operator-owned
ConfigMap per approval and reconciliation; project identity remains database-owned.
`RequireBuildCheck` must remain true; approval requires `BuildPassed`. The CI image
workflow emits `forge-image-receipt.json` containing `sourceSha`, `imageDigest`,
`buildPassed: true`, `testPassed: true`, `workflow: controller-image.yml`, and a
nonempty string `workflowRunId`. The candidate gate validates this attestation;
it never runs repository build scripts inside the live Kubernetes controller.
Register only a receipt from the reviewed build with
`python3 deploy/k3s/catalog.py --receipt /private/path/forge-image-receipt.json`.
The installer registers its initially qualified source/image automatically. Catalog
updates are append-only with resource-version comparison; a source SHA cannot be
rebound to another image. This needs no replacement of immutable config Secrets.
Catalog ingestion is explicit;
this first implementation does not automatically trust tags or dispatch builds.

The image records its revision both in the OCI label and `/app/source-revision`.
The installer checks the embedded revision in an isolated, credential-free Job
before initialization. API release Jobs check it before touching the release
journal. This binds the selected digest to the approved source under the trusted
builder; it is not a signature or a substitute for CI/review.

Push-triggered builds use the repository variable
`FORGE_SDK_IMAGE` set to a reviewed SDK digest, plus `PACKAGES_PAT` for private
NuGet dependencies. No push or workflow execution is performed by these files.

An integration-branch deployment does not promote its source into `main`.
Before closing a restart or migration, open and qualify a promotion PR into the
repository's default branch, preserving commit ancestry with a merge commit.
Then align the registered project's default branch, bootstrap configuration,
and clean managed checkout through a drained controller replacement. A registry
update alone neither switches an existing checkout nor refreshes cached dispatch
bundles. Keep the promotion and the operational branch cutover visible as
separate completion steps. See
[`main-promotion-review.md`](../../docs/workflow-audit/main-promotion-review.md).

Deployment-list polling reconciles the external journal into SQL after a
controller replacement. Approved work whose Job creation was interrupted is
submitted using its deterministic Job name. `committed` means deployed;
`rolled-back` means failed with completed rollback. Incomplete rollback remains
Deploying for intervention/resume and never masquerades as a successful release.
The controller can create Jobs and read journals, but cannot patch Deployments;
only the dedicated release service account changes controller replicas/routes.

## Project worker tools

A project can supply a Dockerfile for the commands its K3s workers run. For example,
a C++ project might add a compiler, CMake and Ninja. The stock Forge container
continues to run the agent, enforce its plan and record its verification result.

1. Commit the Dockerfile and its inputs to the project's default branch. Choose a
   base image with Git, Bash and `/bin/sh`. Tools must work as UID/GID 10001 with
   a read-only image filesystem, a writable task checkout for engineering work,
   and a writable `/tmp`. Pin the base by digest for reproducible rebuilds.
2. Open **Projects → your project → Worker environment**, enter the Dockerfile
   path and build context, then save. Both paths are repository-relative. The
   Dockerfile must be inside the context. The existing image continues serving
   new assignments while the definition is pending.
3. On the single K3s node, run the command below as the operator. It requires
   rootless Podman, Git access to the registered origin, Python with tar extraction
   filters, and permission to import images through `sudo -n k3s`. Point
   `--dashboard` at the intended Forge instance through HTTPS or a loopback tunnel.

   ```sh
   python3 deploy/k3s/worker_environment.py \
     --project <project-id> --repo <project-checkout> \
     --dashboard <operator-url> --token-file <controller-token-file> \
     --check 'cmake --version && ninja --version'
   ```

The helper fetches the registered default branch, exports the chosen context using
`git archive`, builds the image and runs the optional project check offline.
Supply build inputs as tracked files. Submodule contents and Git LFS objects
need to be packaged separately. The helper
imports the image into the local K3s runtime and activates its immutable digest
only after all checks pass. A concurrent settings change requires a fresh build.
Refresh the project settings to see the active digest and source commit.
Re-run the helper after changing the Dockerfile or its inputs on the default branch.
For a rollback, revert that change and qualify the reverted commit, or save a blank
Dockerfile path to resume stock execution. Existing assignments keep their pinned
environment. Local image import targets single-node K3s.

The project container receives the assigned checkout, a sanitized Git snapshot
and project-scoped secrets explicitly supplied to a command. The trusted worker
holds Forge's SQL and model credentials. An engineering agent can use
`request_worker_dependency` to record the missing tool, reason and suggested
change. Open the task to acknowledge or dismiss the request. To adopt it, review
the repository change and qualify the resulting image through the same flow.

## SQL identity separation and trust boundary

Three distinct private connection files are required. `--sql-file` supplies the
controller's `forge-runtime` Secret; `--initialize-sql-file` supplies the schema
initializer's `forge-initialize-runtime` Secret; `--worker-sql-file` supplies
`forge-worker-runtime` for worker Jobs. Each uses key `sql-connection-string`.
The initializer alone receives the DDL-capable identity and grants the configured
`Bootstrap__WorkerPrincipal=forge-k3s-worker` its required initialized-schema
permissions. Shared `forge-config` must not contain a database connection string;
ordinary controllers/workers must never receive the initializer credential.

The controller runtime identity has application read/write permissions. Workers
need read access plus specific task/attempt and budget DML; they do not need DDL
or control-lease mutation. These are trusted-code execution identities, not an
adversarial sandbox: direct SQL DML can bypass application-level state machines,
and a controller permitted to create Kubernetes Jobs can select another service
account or Secret in that namespace. Namespace RBAC and separate SQL identities
limit accidental exposure but do not establish hostile-code isolation. Stronger
isolation requires a restricted execution broker/admission policy and constrained
stored procedures rather than granting arbitrary agents direct database access.

`--worker-packages-file` is a private JSON object with nonempty `username` and
`token` strings. It creates immutable Secret `forge-worker-packages`; package-read
credentials are supplied by reference to worker Jobs, never embedded in the image
or printed by the installer. Use a package-read-scoped credential. Git push uses
the project's existing encrypted GitHub secret and shared clone credential helper;
no second global push credential is introduced. The image defines a nonsecret
Forge Git author/committer identity so fresh clones can create commits.

Self-deployment proof must cover two distinct checks: (1) exact reviewed source
SHA packaged into the digest and qualified before initialization/release, and
(2) candidate build/test approval followed by independent Job cutover, replacement
controller ownership, dashboard routing, and SQL deployment journal reconciliation.
A successful image build or rollout alone does not establish the second check.
Keep gates held and paid calls disabled until the installed runtime passes these
acceptance checks; publishing a receipt never enqueues work or approves a release.

### Worker build credentials and verification boundary

Worker Jobs receive `GITHUB_PACKAGES_USER` and `GITHUB_PACKAGES_PAT` by Kubernetes
Secret reference so the repository's NuGet configuration can restore private
packages. Known credential values and common encoded forms are redacted from
shell and verification output. Redaction is defense in depth, not a guarantee
against deliberately transformed or exfiltrated values.

The worker host runs the project's configured verification commands after the
agent finishes. It records a typed receipt containing the prospective Git tree
hash and a hash of the exact commands. The controller verifies both before
committing and again before publication; it does not rerun project builds for
worker executions. Changing source or verification commands invalidates the
receipt. Legacy in-process execution retains its existing verification behavior.

For K3s, `Spawner.AgentRunTimeoutMinutes` (default 15) bounds the model/tool
phase inside the worker, not the controller's wait for the entire attempt.
The independent verifier runs the full configured commands once after that
phase; it retains the existing 15-minute per-command timeout. The Job's
`Worker.DeadlineSeconds` (default 3600) remains the total hard ceiling, including
startup and verification. No deadline increase or skipped qualification is
implied. Ownership cancellation still stops either phase. Verification failures
remain blocking worker failures handled by normal retry policy; only legacy
in-process execution retains the automatic in-session verification correction
loop. Dirty files and local commits are preserved for recovery, never evidence
of successful qualification by themselves.

The worker's tools and verification process still share its user identity, SQL
credentials, project credentials, and writable PVC. A successful receipt proves
that the trusted executor observed passing commands against that source tree;
it is not an attestation against malicious generated code. Git filters, shared
configuration, ignored build outputs, and direct database access remain within
this trusted-code boundary. Strong isolation requires separate tool execution,
a constrained credential broker, and artifacts that the worker cannot rewrite
after verification. Do not treat namespace RBAC or output redaction as that
isolation boundary.


## Additive schema upgrades

Same-schema releases retain the ordinary blue/green path. Fresh installs also use that path after the installer has initialized the database; target standby readiness must verify the strict schema before admission. A target image may opt into one reviewed additive transition by bundling `deploy/k3s/schema-upgrade.json` with exactly `fromVersion`, `toVersion`, and `rollbackImage`. The rollback image must be an immutable digest that has been independently qualified on both versions, including partial additive migration. No policy file ships until that digest is approved. A source-39 binary that strictly accepts only39 is never a rollback for schema40.

The supervisor first binds the observed previous image, dataset, and verified schema version to that policy. It journals the operation, drains admission and active runs, requires the SQL owner to be released, scales both controller slots to zero, and waits until every controller pod (including terminating pods) is gone and no live worker pods remain. Only then can its deterministic initializer Job run. The release service account gets Jobs create/get, not Secret read; only the initializer mounts the privileged database Secret, and that pod has no Kubernetes API token.

Initializer preflight holds the exclusive SQL schema lock. It checks the dataset, budget, Forge repository and branch, and requires a released lease and zero queued or running worker attempts. It checks the registry and every registered project's workload schema against the declared source and target versions before migration. The target must match the initializer binary. The initializer upgrades each workload and installs its worker permissions and review guard.

For the 43 → 44 upgrade, install the [schema 43/44 recovery image](https://github.com/Xyrces/Forge/actions/runs/44575951459) first. On schema 43 the recovery image serves Forge normally. On schema 44 its recovery dashboard reads database status and rejects mutations. Restore the environment-aware release to resume scheduling. The supervisor verifies the previous slot's exact image digest before running the migration. [Environment configuration and promotion](../../docs/deployment-environments.md) explains how to provision each destination.

For the 42 → 43 upgrade, first promote the rollback image pinned in the schema 43 release while the database is still on 42. Verify the active slot's digest and schema version before submitting the schema 43 release. The supervisor uses that slot if the candidate fails after migration. The fallback reads both versions and continues to write the schema 42 record format. A rollback leaves the database on 43. [Rollback image build and immutable deployment receipt](https://github.com/Xyrces/Forge/actions/runs/44403998494)

At startup the upgraded controller copies historical design and art records for a spec to its owner's workload database. Removing a registered writer also runs this copy before removing its registry entry. Keep the original art-output files on the shared volume because the retained records reference those files. A writer removed before the first copy must have its original storage made available for recovery.

The journal and initializer Job are immutable-input bound. A replacement supervisor resumes the same operation and adopts the same Job. A different operation cannot take over an unfinished schema upgrade, even after its owner pod dies; the compatibility bridge includes that guard too. Supervisor Jobs allow three new-pod retries, with `restartPolicy: Never`. Initializer Jobs have zero automatic retries. Kubernetes Job retry behavior is documented in the [official Job guide](https://kubernetes.io/docs/concepts/workloads/controllers/job/); database side effects are protected separately by the schema lock and version checks.

An initializer timeout or uncertain transport result leaves both controllers stopped and the operation resumable. It never triggers rollback while initializer execution might continue. A terminal initializer failure can restore the qualified bridge only after its pods are nonexecuting. A candidate failure after migration also restores that bridge without downgrading the database. The schema39/40 bridge is a maintenance fallback: it preserves the older feature set and is not feature-equivalent to new schema40 initiative workflows. Keep workflow gates held throughout qualification and rollback.

The installer invokes the target's independent release Job; it does not orchestrate a host-side migration sequence. Full rollback/restart qualification must precede policy publication. Unit fault tests do not establish actual SQL migration or live bridge compatibility on their own.

## Review identity and rollback qualification

`--github-apps-directory` optionally packages the privately staged App config and referenced PEMs into `forge-github-apps`. `--github-webhook-secret-file` optionally packages the independent signature secret into `forge-github-events`. Existing Secret contents are never silently replaced. Both blue/green controller templates mount these read-only; initializers, release supervisors and workers do not receive these mounts. Neither option exposes a Service, ingress or network port. The controller and initializer continue using distinct Azure SQL principals.

A controller advertises `reviewHoldProtocolVersion: 1` on its authenticated status endpoint. On an upgrade from an older revision the supervisor drains and stops the old controller, then calls the candidate's authenticated `merge-fence` endpoint before activation. For each project a single database transaction preserves the operator's prior merge setting under `review/operator-merge-gate`, writes the legacy `gate/merge=hold` and records the release operation in `deployment/merge-fence`. The supervisor verifies the receipt and records it in the release journal.

After promotion the new controller honors the operator's existing stage setting and individual review holds. Operators can hold and release the stage normally in Flow. The old controller still reads a held gate if a rollback is needed. SQL enforcement prevents the old dashboard from reopening or deleting that gate or removing its fence receipt, and prevents a workflow override from disabling merge-gate enforcement. Restoring the default workflow remains supported. Upgrade to a controller that understands the saved review controls to reconcile outstanding reviews and resume automatic merges; the old release endpoint is not an override. If fence acknowledgement is missing after candidate activation, rollback stops the candidate and leaves the Service unrouted for operator recovery.

Build/test and deployment remain separate: GitHub-hosted Ubuntu workflows test source and produce a digest-bound controller image; the release catalog verifies repository/workflow/source/run/artifact provenance. The initializer alone owns schema changes. Controller SQL leases fence concurrent owners, the release supervisor stops old pods before activating the candidate, and the stable Service changes only after activation. Locally signed webhook probes qualify private ingress only; real GitHub delivery and external HTTPS routing require separate proof.
# SQL review authority prerequisite

Ordinary immutable-image releases, including source-SHA self-releases on a dataset
already at the target schema version, run a short initializer Job before creating the candidate controller.
This uses the existing initializer SQL secret and `Bootstrap__ReviewAuthorityOnly=true`;
automatic migration is disabled. It only installs/enables and verifies the project-scoped
operator-memory guard and grants the controller visibility of that guard's definition.
It does not reseed data, change schema versions, acquire a controller lease, or drain the
old runtime. Fresh initialization and schema upgrades install the same guard.

The Job is bound to the release operation, dataset and immutable image, and must finish
with no running initializer pod before candidate qualification. Failed or changed Jobs
prevent activation. Controller health, status, activation and read-only deployment checks
also verify the exact enabled guard body and configured worker identity; an absent,
disabled or mismatched guard is not ready. Existing policy values and old-controller
rollback keys remain in place. An older deployment is not modified by a failed candidate
qualification, and the security guard remains installed across rollback.
