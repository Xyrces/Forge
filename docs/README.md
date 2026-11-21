# Forge documentation

The [project introduction](../README.md) explains what Forge does. Use this index to find the guide for your next step.

**Contents** · [Set up Forge](#set-up-forge) · [Use and operate Forge](#use-and-operate-forge) · [Work on the code](#work-on-the-code) · [Design history](#design-history)

## Set up Forge

The current deployment uses K3s, Azure SQL, and OpenAI Responses.

- [Getting started](getting-started.md) — choose a setup path, build, and test.
- [K3s runbook](../deploy/k3s/README.md) — install, upgrade, deploy, and roll back.
- [GitHub App setup](github-app-identities.md) — configure the publisher and reviewer identities.
- [Project registration](distributed-project-registration.md) — add repositories with qualified storage.

## Use and operate Forge

- [User guide](user-guide.md) — shape the vision, approve work, follow sprints, and resolve blockers. Jump to [reviewing a pull request](user-guide.md#review-a-pull-request) to add a human review requirement.
- [Deployment environments](deployment-environments.md) — choose the sprint target and promote a specific release.
- [Runtime reference](runtime-reference.md) — commands, configuration, models, state, and logs.
- [Webhook delivery](github-webhook-delivery.md) — deliver GitHub events to a private host.
- [Existing implementation verification](empty-pr-verification.md) — establish completion when the requested code already exists.

## Work on the code

- [Contributing](../CONTRIBUTING.md) — module layout, conventions, tests, and schema changes.
- [Agent rules](../AGENTS.md) — repository instructions for coding agents.
- [System flow](system-flow.md) — stores, dispatch, dependencies, and memory. The diagrams describe the in-process runtime.
- [End-to-end harness](e2e-harness.md) — deterministic workflow tests and optional live-model tests.

## Design history

These records describe particular revisions. Check their dates, commit IDs, and scope when reading implementation claims.

- [Master design](MASTER_DESIGN.md) — detailed project design recorded in August 2026.
- [Revival review](revival-review/README.md) — architecture review and initial restart qualification.
- [Workflow audit](workflow-audit/README.md) — investigations, repair designs, and qualification evidence.
- [Intake-to-sprint design](intake-to-sprint-workflow.md) — original planning model and implementation proposal.
- [Agent Framework design](agent-framework-design.md) and [shared context design](p5-shared-context.md) — earlier architectural decisions.
- [Headroom experiments](headroom-benchmark.md) and [follow-up measurements](headroom-benchmark-v2.md).

<details>
<summary>Earlier operating guides</summary>

These cover earlier deployment modes and interfaces.

- [Administrator guide](administrator-guide.md) and [operator cookbook](operator-cookbook.md)
- [Linux systemd deployment](linux-deployment.md) and [original deployment pipeline](deployment-pipeline.md)
- [SQLite-to-Azure-SQL cutover](azure-sql-cutover.md)
- [Multi-project v1](multi-project.md) and [checkpoint recovery](p4-restart-safety.md)
- [Dashboard roadmap](dashboard-roadmap.md), [UI mockups](p6-ui-mockups.md), and [UI troubleshooting](ui-troubleshooting.md)

</details>
