# Forge administrator guide

For the person who installs, configures, secures, and keeps the Forge orchestrator running. For day-to-day operation see [`user-guide.md`](user-guide.md); for scenario recipes see [`operator-cookbook.md`](operator-cookbook.md).

## 1. Installation

### Local / development

1. Install the **.NET 10 SDK** (`dotnet --version` → `10.0.x`).
2. Clone the repo and copy `appsettings.example.json` → `appsettings.json` (gitignored).
3. Fill in at least one LLM provider key and a GitHub PAT (see §3).
4. `dotnet run --project Forge -- --check` — pre-flight must pass before anything else.

### Linux service (production)

Forge runs as a single systemd unit (`Type=notify`). The full install/upgrade/ops runbook is [`linux-deployment.md`](linux-deployment.md):

- Releases under `/opt/forge/releases/<sha>/`, `current` symlink, unit template `deploy/systemd/forge.service`.
- Config at `/etc/forge/appsettings.json` (0640), optional secrets file `/etc/forge/forge.env` (0600).
- State under `/var/lib/forge/` (`StateDirectory=forge`).
- Install: `scripts/install-systemd-service.sh`. Self-deploys land via `DeploymentPipeline/SelfHostedSystemdServiceDeploymentExecutor.cs` (repoints `current`, `systemctl restart forge`, synchronous).

Only **one orchestrator process per state directory** — SQLite WAL tolerates concurrent readers, not a second writer.

## 2. Configuration reference

Config resolution: environment variables (`__` for nesting) → `appsettings.json` → built-in defaults. `--config <path>` points any CLI mode at a specific file.

| Section | Keys | Notes |
|---|---|---|
| `llm` | `defaultProvider`, `providers[]`, `roles`, `overloadRetryCount`, `maxConcurrentRequests`, `meshy*` | Providers are OpenAI-compatible or Anthropic-protocol (`"api": "anthropic"`). Quirk knobs per provider: `api`, `auth` (`bearer` vs `x-api-key`), `sharedQuota`, `maxOutputTokens`, `contextWindowTokens`, `modelsUrl`. |
| `llm.roles` | `CoreDev` / `ClientDev` / `QA` / `Reviewer` / `Intake` → `{ providerName, model }` | Designer/Artist/Groomer inherit CoreDev's provider. Resolution order at runtime: live DB override → this map → provider default. |
| `github` | `owner`, `repo`, `token` | **Fallback only.** Per-project `github_token` secrets win wherever a project id is known; owner/repo are parsed from each project's registered `RepoUrl`. |
| `workspace` | `worktreeRoot`, `defaultBranch` | `workspace.root` is legacy — the DB project registry owns local clone paths. |
| `db` | `provider` (`sqlite` \| `sqlserver`), `connectionString` | Default sqlite. See §5 for Azure SQL. |
| `spawner` | `pollIntervalSeconds`, `staleMinutes` | `pollIntervalSeconds` drives the dispatch-loop cadence. `maxConcurrentSessions` no longer gates dev dispatch (per-role slots do). |
| `orchestrator` | `execution` (`InProcess` \| `Durable`), `dtsConnectionString` | Durable = P4 Stage B, requires the DTS sidecar (`deploy/docker-compose.yml`). See [`p4-restart-safety.md`](p4-restart-safety.md). |
| `dashboard` | `enabled`, `port` (4097), `hostname` (127.0.0.1) | Multi-endpoint Kestrel binding (HTTP 80 / HTTPS 443 + cert + redirect) is supported via config. |
| `headroom` | `enabled`, `proxyBaseUrl`, `mode`, `budgetUsd`, … | Optional LLM cost-optimization sidecar. See [`headroom.md`](headroom.md). |

**Never bind the dashboard hostname to `0.0.0.0`/`::` for in-process HttpClients** — unspecified addresses throw `HttpRequestException`; use `127.0.0.1` for loopback clients. Public exposure should go through the multi-endpoint Kestrel config, not the hostname field.

## 3. Secrets and credentials

The per-project secrets system is the canonical credential store:

- **Storage:** per-project `secret` table, DataProtection-encrypted at rest (`forge.secret.v1` purpose). Known kinds: `github_token`, `kilo_gateway_api_key`, `meshy_api_key`, `kimi_api_key`; custom kinds (`[a-z0-9][a-z0-9_-]*`) are allowed.
- **Management:** dashboard `/projects/{id}/secrets` or `POST/DELETE /api/projects/{id}/secrets`.
- **Consumption by reference:** at run time, secrets are decrypted and injected into the agent's bash process as `FORGE_SECRET_<KIND>` env vars (`github_token` also as `GITHUB_TOKEN`). Values never enter prompts, tool-call JSON, transcripts, or logs.
- **Precedence:** per-project `github_token` → global `GITHUB_TOKEN` env / `github.token` config.

Admin rules:

1. Keep PATs out of `appsettings.json` where possible — prefer project secrets or the 0600 `forge.env` file.
2. Never log, echo, or commit secret material. Repo-owned skills and role prompts are agent-visible text — no credentials in them either.
3. Git credentials for clones are written to `<localPath>/.forge/git-credentials` (mode 0600) at registration; the stored `origin` is reset to the token-free URL immediately after clone.

## 4. Project registry

Projects are DB rows, not config. The `appsettings.json` `projects[]` array is **deprecated and ignored at boot**.

- **Add:** dashboard **Projects → Add Project** (token → fetch repos → pick → branch detect → register) or `POST /api/projects`. Registration clones the repo inline (best-effort); on failure a local scaffold is created and every boot / `POST /api/projects/{id}/sync` reconciles it once a working PAT exists.
- **Default-branch resolution:** per-project git token → remote HEAD symref → clone `origin/HEAD` → operator override (override always wins).
- **Remove:** `DELETE /api/projects/{id}` removes the registry row only — clones and worktrees are operator-managed cleanup.
- **Role caps:** per-project `roles_json` via `PUT /api/projects/{id}/roles` (live); falls back to defaults (coredev/clientdev/reviewer=2, others=1).

## 5. Database

- **SQLite (default):** `issues.db` + `memory.db` per project state dir. Automatic migration chain on startup; schema is currently **v31**.
- **Azure SQL / SQL Server:** `db.provider=sqlserver` + `db.connectionString`. The provider fresh-creates at the current schema with `IF NOT EXISTS` guards. Cutover runbook: [`azure-sql-cutover.md`](azure-sql-cutover.md) (`--migrate-db --target sqlserver [--include-open-work] [--reset]`, service stopped). Managed-identity provisioning: `--init-azure-sql [--mi-name forge-mi]` (run once as Entra admin, idempotent).
- **Schema changes** (developers): bump `CurrentSchemaVersion` in `Core/IssueStore.cs`, update BOTH DDL paths (SQLite migration chain + SQL Server fresh-create), then run `--check`. `CONTRIBUTING.md` § "Schema migrations" is authoritative.
- **Backups:** SQLite — back up the state dir (`issues.db`, `memory.db`, `orchestrator-state.json`) while the service is stopped or use the SQLite backup API. Azure SQL — rely on service-tier backups. The JSONL mirror is a viewer artifact, not a backup.

## 6. Concurrency, rate limits, and cost

- **Dispatch concurrency** is per (project, role) via `Orchestrator/Slots/SlotTable.cs` — tune per project with `PUT /api/projects/{id}/roles` or the project drill-down page.
- **LLM rate limits:** one process-wide tracker keys cooldowns by provider+model; a 429 from any subsystem cools that model for all of them (Retry-After honored). `llm.overloadRetryCount` (default 3) retries transient capacity 429s in place before cooling. `llm.maxConcurrentRequests` (default 2) caps simultaneous round-trips per provider.
- **Cost observability:** provider-reported token usage is persisted per run (schema v31); see `/ops/cost`. Optional budget enforcement via the headroom sidecar.

## 7. Gates and workflow control

- **Stage gates** (`design`, `groom`, `sprint`, `merge`): hold/release via `GET/POST /api/gates*` or `/ops/gates`. A held stage's scheduler skips its tick; a held merge leaves PR watches live (external merges still detected). State lives in the primary project's memory store (`gate/<stage>`).
- **Editable workflow:** `/flow?mode=edit` — draft → validate → publish (`workflow/live` memory key, snapshots under `workflow/versions/`). Controls wiring and policy (auto-merge, rework limits, step toggles, gate attachment) without restart. The task state transition table stays code-owned.
- **Run gates** (plan gate): resolution per checkpoint — DB override (`gates/run/<checkpoint>`) → `gates.run.<checkpoint>` config → built-in defaults. Do not weaken tool-layer enforcement to prompt-only.

## 8. Monitoring and troubleshooting

| Signal | Where |
|---|---|
| Service health | `sudo systemctl status forge`, `journalctl -u forge -f` |
| HTTP health | `GET /api/forgesystem/health`, `/api/health/uptime`, `/api/health/heartbeat` |
| Live state | dashboard `/now`, `GET /api/state`, `.portHorizon/state/issues.jsonl` |
| Event stream | `GET /api/events` (SSE, last ~1024 replayed) |
| Per-run diagnostics | `<dataRoot>/logs/agent.log` — first stop when a run "completes" with no diff |
| Recovery audit | `/ops/recovery`, `--recover` dry-run |
| Cost | `/ops/cost`, `GET /api/cost/*` |

Standard first step for anything stuck: `dotnet run --project Forge -- --check` (or the deployed binary with `--config /etc/forge/appsettings.json --check`). If it passes, the bug is in the dispatch path — start at `/flow?issue={id}` for a per-issue journey view.

Common failures:

- **Second writer on the state dir** — kill the duplicate process; only one orchestrator per state directory.
- **Model cooled after 429s** — the provider+model is rate-limited; runs on that model fail fast until the cooldown lapses. Check `/agents` for the effective model and consider a project-scoped override.
- **Corrupt state / unknown schema version** — the orchestrator refuses to start; restore from backup or run the migration tooling.
- **Dashboard `HttpRequestException` on bind** — hostname set to an unspecified address; use `127.0.0.1` (see §2).

## 9. Upgrades

1. PR lands on the default branch (branch protection is on — no direct pushes).
2. Build + test: `dotnet build Forge.sln && dotnet test Forge.sln` (clean build is enforced: `TreatWarningsAsErrors=true`).
3. Deploy via the self-deploy pipeline or manually: publish to `/opt/forge/releases/<sha>/`, repoint `current`, `systemctl restart forge`.
4. Schema migrations apply automatically at startup (SQLite) — watch the first boot's logs. SQL Server deploys create schema idempotently.
5. Rollback: repoint `current` at the previous release and restart. SQLite state is forward-migrated — keep a pre-upgrade backup of the state dir if a rollback must also roll back state.
