# Worker platform review: Kubernetes, Docker and native gameplay

Reviewed 2026-11-16. This platform subreview performed no host inspection, installation, deployment, paid requests or runtime changes. The coordinator separately inspected the host and completed authorized SSH and plugged-in availability changes; see `host-review.md`. Updated constraints: restart Forge against Forge first, TimeLoop second; fresh application state is acceptable; Azure SQL is mandatory. TimeLoop source references are pinned at `d3365cfdb900820d31842cd5d888eb2b81a5fdbe` and Forge at `4ac44895c05122ff5705a32b4ceb93e60a6e72e5`.

## Recommendation

The synthesis recommends **direct single-node K3s with embedded etcd and CPU attempt Jobs**, with **Azure SQL retained for Forge application state**, **Forge first**, and **TimeLoop second**. No Docker-only intermediate implementation is required. This is a proposed target, not an installed platform. Embedded etcd is the separate Kubernetes metadata store; it does not replace Azure SQL. K3s documents bootstrapping its embedded-etcd configuration with the cluster initialization option. [K3s embedded etcd](https://docs.k3s.io/datastore/ha-embedded)

It is not necessary to put a permanently running pod behind every named role. Keep one authoritative Forge coordinator and launch a bounded Job for each execution attempt. Role is assignment metadata and tool policy. Use one common worker contract so a qualified native graphics worker can participate without pretending every environment is identical.

For a single host, Docker remains a simpler operational alternative; Kubernetes provides scheduling and lifecycle APIs rather than more compute capacity. Direct K3s fits the chosen long-term direction while avoiding two worker adapter implementations. Keep the initial scope small: one coordinator, one worker image, one Job adapter, durable evidence and lease reconciliation. Do not add a service mesh, custom operator, external message broker, autoscaling or distributed coordinator merely to run the first Forge task. These are recommendations, not requirements imposed by the container platforms.

## Workload placement

| Work | Appropriate first placement | Important constraint |
|---|---|---|
| Forge code implementation, builds, deterministic tests | Linux CPU worker Job/container | Exact source SHA, pinned .NET dependencies, private package access, resource limits and preserved patches/commits |
| Review, grooming, textual design/dialogue work | CPU worker or bounded coordinator-side model call | A separate pod for every short model call adds no inherent quality; project rules and tool policy still matter |
| OpenAI image or Meshy API submission | CPU job/service task | Remote generation does not require a local GPU; persist provider job IDs and credit reservations before retry |
| Asset download, validation and non-rendered import | Toolchain-specific job | Preserve source files, AI labels, provenance, scale/material metadata and import identities |
| TimeLoop Linux gameplay and native asset inspection | Qualified Linux GPU worker; potentially a GPU Job after qualification | Vulkan/driver/device compatibility, isolated compositor, matching Godot/native plugins, actual image inspection |
| TimeLoop Windows compatibility/gameplay | Qualified Windows machine or GPU-capable VM worker | A Linux run does not establish Windows acceptance; Windows containers are not a desktop-app virtualization replacement |
| Human's normal playable game | Existing verified package and normal user session | QA must use separate saves, processes and output locations |

Microsoft's container migration guidance expressly distinguishes Windows containers from desktop virtualization and notes the no-GUI constraint. Therefore a Windows Kubernetes node should not be presented as a drop-in replacement for TimeLoop's native Windows gameplay qualification. Keep the Windows qualification worker outside the initial pod plan unless a specific supported approach is demonstrated. [Microsoft Windows container guidance](https://learn.microsoft.com/en-us/virtualization/windowscontainers/quick-start/lift-shift-to-containers)

## Graphics do not inherently require a permanent GUI session

The earlier shorthand “requires a desktop worker” needs qualification. TimeLoop's current `docs/development.md:36` requires unattended checks to use a separate owned display, **not** the desktop's DISPLAY/WAYLAND_DISPLAY. At `:263`, its existing isolated route uses project-local Weston 14.0.2 with the headless GL backend and system Xwayland; the game still renders through native Vulkan. This is an actual renderer with an isolated display, not Godot headless simulation. `--background` alone does not isolate a display.

Accordingly Linux gameplay can plausibly run in a container/pod, with a persistent qualified GPU **node** and reproducible toolchain, while the compositor, game and test session are ephemeral. It does not intrinsically require a permanent compositor or a human logged into a visible GUI. Container compatibility is not yet proven for this project. The existing launcher expects a non-root user and rejects the handoff-only account (`tools/linux/README.md:3`); preserve this ownership contract.

Kubernetes supports GPU resources through vendor device plugins and host drivers; placement can select nodes with the required GPU. This establishes a platform capability, not proof that the specific Radeon 890M, Mesa/Vulkan stack and TimeLoop build work in a selected plugin/container. Qualify those exact combinations before relying on GPU Jobs. [Kubernetes GPU scheduling](https://kubernetes.io/docs/tasks/manage-gpus/scheduling-gpus/)

Keep the game, input driver and owned compositor in one run boundary. TimeLoop's API binds an ephemeral IPv4 loopback port with a random process token and returns screenshot paths (`docs/development.md:254`, `:283`). Containers in one pod share localhost and can share volumes, so one pod can retain that API design without exposing the game endpoint to the cluster. The token must stay in the run's private endpoint file, not logs or prompts. [Kubernetes pod networking and storage](https://kubernetes.io/docs/concepts/workloads/pods/)

Initially serialize native GPU/export jobs even when CPU engineering work is parallel. TimeLoop explicitly requires coordination of renderer/import resources and verification of process ownership before stopping a process (`.agents/skills/loop-orchestrator/SKILL.md:85`). A GPU allocation does not by itself isolate shared export caches or prevent performance interference from the host's normal game. Raster remains the accepted Linux baseline; neither containers nor Kubernetes remedy NRD performance on the APU.

## State and failure contract

Azure SQL remains mandatory regardless of worker runtime. It stores authoritative issues, runs, leases, checkpoints, budgets and artifact references through Forge's existing store boundaries. Do not introduce a second task queue in Kubernetes or give shell workers unrestricted SQL mutation authority. Prefer a narrow authenticated assignment/result API; the coordinator validates observations and applies existing lifecycle transitions.

Separate three lifetimes:

1. **Application truth:** Azure SQL, surviving worker/coordinator replacement.
2. **Durable deliverables:** commits, manifests, logs, inspected captures and generated asset originals in persistent storage; SQL stores identities and references rather than large image/model blobs.
3. **Disposable execution:** per-attempt source checkout, temporary build state, endpoint token, compositor and game processes. Cleanup only after deliverables are durably acknowledged.

Use an independent checkout per run rather than casually mounting a shared host Git worktree: worktree Git metadata can contain host paths and shared-repository dependencies. Preserve unfinished diffs/commits when a Job fails. Shared read-only package/toolchain caches can reduce startup cost; mutable Godot import/export state needs its own ownership rules.

Kubernetes explicitly warns that even a one-completion, one-parallelism Job can start the same program twice. Set bounded attempt deadlines/restarts, but do not mistake those settings for exactly-once execution. Forge must reconcile attempt identity, fencing/leases, existing branches/PRs and remote asset-job IDs before repeating side effects. Keep business rework in Forge rather than layering automatic Job retries over model rework. Save evidence before TTL cleanup. [Kubernetes Jobs](https://kubernetes.io/docs/concepts/workloads/controllers/job/)

These guards are needed with Docker too: the runtime provides process management, not exactly-once GitHub publication, model billing or asset purchasing. A pod exit of zero is only execution status; accepted work still needs final-source tests, code review, artifact validation and, for gameplay, inspected native evidence.

## Same-host practical comparison

| Concern | Docker execution adapter | Kubernetes Job execution adapter |
|---|---|---|
| Packaging | Worker images | Worker images; containers grouped into pods |
| Task launch and observation | Forge calls runtime and tracks IDs/status | Forge creates/observes Jobs and pod attempts |
| Local operating burden | Engine, image/cache lifecycle, explicit run cleanup | Control plane, runtime, cluster networking, storage and policies as well as images |
| Placement | Explicit host/worker selection | Scheduler plus node labels/resources; useful when multiple worker types exist |
| Resource protection | Explicit CPU/memory/process/storage policy | Requests/limits plus cluster admission policy; still requires correct sizing |
| Host loss | Cannot recover computation on the same failed host | A one-host cluster also has no surviving compute node; durable SQL/evidence can support recovery elsewhere |
| GPU/native compatibility | Must qualify driver and device exposure | Must qualify driver/device exposure plus scheduling/plugin setup |
| TimeLoop completion | Project evidence contract required | Same project evidence contract required |

The operating-burden and same-host conclusions are architectural inferences from Kubernetes' control-plane/node model; no host benchmark was run. [Kubernetes components](https://kubernetes.io/docs/concepts/overview/components/)

Docker containers have no CPU/memory constraints by default, so an agent-run adapter must set explicit limits. Device access can be granted selectively without granting blanket privileged mode; an actual Vulkan/AMD configuration still needs testing. [Docker resource constraints](https://docs.docker.com/engine/containers/resource_constraints/), [Docker run device options](https://docs.docker.com/reference/cli/docker/container/run/)

## Recommended order under the revised plan

1. Finish review and design using the observed host inventory and proposed direct-K3s direction. Azure SQL is part of the baseline, not a later optional migration. Qualify remaining platform prerequisites before installation.
2. Rebuild the coordinator/worker boundary around a minimal attempt contract: project, task, exact source, toolchain image, role policy, resource class, deadline, spend allowance and durable result manifest. Keep one coordinator initially.
3. Prove Forge-against-Forge CPU runs first, including failed build, cancelled model/tool, duplicate attempt, pod eviction/process death and preserved output. The first implementation must address the already identified correctness/migration blockers; containerization does not fix them.
4. Add TimeLoop grounding and existing-route verification next. Initially connect the known qualified graphics environment through the same worker contract; do not block the Forge restart on a complete GPU container platform.
5. Qualify an isolated Linux GPU pod using exact engine/driver/plugin versions, real production input, both required resolutions, image inspection and package fingerprints. Compare outcomes with the existing worker. Retain a separate Windows acceptance route.
6. Expand concurrent execution only after observing CPU, memory, disk, GPU and API-budget behavior. Neither a role count nor pod count is a safe concurrency budget by itself.

## Known host and remaining qualification

The coordinator's `host-review.md` establishes the existing host: Ryzen AI 9 HX PRO 370 (12 cores/24 logical CPUs), approximately 53 GiB RAM with 41 GiB available during inspection, approximately 359 GiB disk available, and Radeon 890M-class graphics with `/dev/dri/card1` and `/dev/dri/renderD128`. Initial SSH checks found no `docker`, `kubectl` or `k3s` executable on PATH. This is evidence of missing discoverable commands, not an exhaustive proof that no installation exists anywhere. Forge currently runs as a user service at the reviewed SHA with SQL Server configuration; its effective device/home settings differ from the system-service template. The desktop user's SDK is 10.0.302 and is absent from noninteractive SSH's default PATH.

Remaining work is qualification, not asking for that inventory again: establish supported K3s/container-runtime installation and durable embedded-etcd storage; test worker identity/device permissions and the exact driver/plugin/Vulkan combination; reconcile TimeLoop's pinned SDK/toolchain with the observed installation; confirm private package/image access and workload Azure SQL identity/connectivity; measure safe CPU/GPU/storage concurrency; and define the Windows qualification cadence. Existing source documentation and available memory do not substitute for an actual native worker test. No platform was installed by this subreview.
