# Project deployment environments

An environment is a real deployment destination. Blue and green are rollback
slots inside one environment, not UAT and production.

Kubernetes projects configure a host-controlled `deployments.<project>.environments`
dictionary. Each entry has `displayName`, its own `kubernetesNamespace` and
`kubernetesDatasetId`, an explicit `requireApproval` gate, and allowed `promoteTo`
destinations. `sprintEnvironment` selects the destination that completes a sprint.

For Forge, UAT is `forge-uat` with dataset
`53f5d6e0-283e-4208-bed3-f96450aca77e`; staging is `forge-staging` with dataset
`5f2b9954-31cb-40c1-b8a4-a2c6b7ba03b0`; production remains `forge` with dataset
`f610a39c-50e1-4aab-8d5a-e18e10aeede9`. UAT can be automatic; higher environments
have an explicit approval gate and are selected from Deployments.

The dashboard edits policy within the host's physical target allowlist. It cannot
grant itself access to a new namespace. Each deployment captures the environment
name, namespace and dataset before approval. Later configuration edits do not
redirect an in-flight release or its rollback.

Promotion selects a successful release and preserves its exact source revision
and trusted immutable image receipt. It does not rebuild the current branch.
Each destination has independent rollout, verification, journal, fencing lease,
traffic route and rollback state. Promotion does not reopen the source sprint.

## Provisioning a target

Install each target using `deploy/k3s/install.py` with its own `--namespace`,
`--dataset`, SQL connection files and shared configuration. Add
`--environment <name>` to enable routed target verification and
`--manager-namespace forge` when the production controller will manage it.
The target-local RoleBinding grants the management controller release Job
submission/reconciliation and ConfigMap reads. It grants no cluster-wide role.
The management controller is trusted to submit release Jobs; Kubernetes Job
creation is a privileged workload-submission capability within that target.

Each target requires a separate Azure SQL database because Forge's SQL schema
names are fixed within a database. A different dataset UUID alone does not
isolate two controllers using the same SQL database. Give each target separate
initializer, controller and worker identities. Only its initializer needs DDL
authority; the normal controller and worker use scoped runtime grants.

For Forge's lower environments, disable paid calls, omit model and GitHub write
credentials, and retain the initializer's explicit held stage gates. Seed a
local clean repository clone without the production credential helper or
untracked credential files. A cached immutable node image permits startup
without distributing registry credentials to the lower environment. These are
isolated rehearsal instances; their configuration does not authorize them to
run autonomous repository work.

## Release acceptance and rollback

A named release verifies the namespace's dataset identity before mutation. After
cutover it verifies the controller dataset and fetches the routed dashboard HTML
and build-info endpoint, requiring the exact approved source revision. Only then
does its journal become committed with `targetVerified=true`, environment,
namespace and source SHA. This is an HTTP smoke check, not an interactive browser
test. Project-specific browser acceptance remains a separately evidenced check.

A failed check restores and verifies the previous target controller and route.
For a fresh environment with no prior release, failure removes its route. Schema
upgrades retain the requirement for a separately qualified compatibility image;
promoting an old incompatible binary is not a database downgrade mechanism.

The UAT and staging databases use Azure SQL's free allowance with
`useFreeLimit=true` and `freeLimitExhaustionBehavior=AutoPause`. Exhausting the
allowance pauses that environment rather than authorizing paid overage; it can
temporarily block verification or promotion. It does not affect the separate
production database. Always-on controller connections consume the allowance;
idle serverless auto-pause cannot occur while those connections remain active.

## Sprint completion and promotion

Open **Projects → the project → Deployment environments** to select a sprint target, pre-deployment approval and promotion routes. A sprint captures its policy when it starts. The board shows each required human approval before deployment. Implementation checks, protected merges and trusted CI qualify the release. The final target check verifies the routed dashboard and its source revision. A single database transaction records a successful sprint deployment and closes the sprint.

In **Deployments**, select a successful release, choose a destination, and confirm the displayed commit and image. Repeating a request for the same active promotion returns its existing deployment. A competing request reports that the destination is busy. A failed rollout retains its history, and the operator can select that release again.

An operator can register a release installed outside the controller using an authenticated `POST /api/projects/{id}/environments/verified-installation`. Supply `environment`, `operation` (the installer's operation name) and `sourceSha`. The controller reads the named release journal and exact GitHub build and test receipts before adding the artifact to Deployments.

The historical reconciliation action for a delivered legacy sprint requires the plan hash, deployed source, deployment ID, every missed operator criterion and a reason. The record preserves the original observations and identifies the operator and time of the exception.
