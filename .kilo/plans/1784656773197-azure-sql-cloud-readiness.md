# Azure SQL Cloud Readiness — SQLite → Azure SQL provider seam + cutover

## Goal
Forge's state database becomes portable: SQLite stays for tests/dev, Azure SQL becomes the
24/7 primary store on this machine via Entra-only auth, ready for a later ACA/AKS cutover
(managed identity, ephemeral agents) with config-only changes.

## Locked decisions (operator-approved 2026-07-27)
1. **Compute model**: new **Basic tier** database `forge` (~$5/mo flat, 2GB max) as the 24/7
   primary. The free serverless DB (`free-sql-db-5815147`, GP_S_Gen5, 100k vCore-s/mo grant,
   `AutoPause` on exhaustion) is the **dev/rehearsal DB only** — it cannot power a 24/7 chatty
   orchestrator (~55 online-hours/month budget, burns out in ~3 days, then frozen for the month).
2. **Auth/network**: **Entra-only auth ON** (`az sql server ad-only-auth enable` — SQL admin
   `jtn5016` login dies; Entra admin `jtn5016@gmail.com` already set). Connection string uses
   `Authentication=Active Directory Default` → resolves via Azure CLI credential on this machine,
   via managed identity in Azure later (same code path). Dynamic IP handled by a firewall-rule
   **refresh script** (systemd ExecStartPre + 15-min timer). Create user-assigned MI `forge-mi`
   + contained DB user now so the ACA/AKS cutover is config-only.
3. **Migration scope**: **registry + keys only** — `project` rows, `secret` ciphertext (3 rows),
   all memory keys (134: vision, `workflow/live`, gates, model prefs, sprint memory). The 28MB
   of issue/agent_run/issue_event/spec/sprint history **stays behind in SQLite** (files kept as
   archive). Runbook gate: 9 non-terminal issues (4 Pending, 5 Blocked) must be drained,
   re-enqueued post-cutover, or carried via `--include-open-work`.
4. **Schema consolidation**: one database `forge`; **schema-per-project** (`dbo` = registry/
   primary, `proj_<id>` per project) — table names have zero collisions across stores today;
   per-project SQLite DBs (dataRoot = `~/.local/share/forge/projects/<id>/.forge/state/`) map
   1:1 to schemas. SQL Server provider **creates schema at CurrentSchemaVersion in one shot**
   (no legacy v1→v23 migration chain is ported — fresh-DB DDL only).

## Live state inventory (verified)
- dataRoot `~/.local/share/forge/projects/`: `default` = registry holder (1 project row,
  3 secrets, 400KB); `forge` = workload (386 issues, 134 memory keys, 28MB);
  `newproj`/`smoketest`/`synth` = test debris (not migrated).
- `~/Documents/forge/.portHorizon` + `~/repos/PortHorizon/.portHorizon/state` = stale/dev.
- Secrets: ASP.NET DataProtection, keyring `~/.aspnet/DataProtection-Keys/` — ciphertext
  migrates as-is (same machine). **Keyring is machine-local: ACA/AKS needs Key Vault — future.**
- Server: `forge-sql-server` (RG `forge`, centralus), Entra admin set, AADOnly currently False,
  public access Enabled, firewall = AllowAllWindowsAzureIps + current IP.

## Phase A — Azure resources (az CLI, RG `forge`)
1. `az sql db create -g forge -s forge-sql-server -n forge --edition Basic --capacity 5 --max-size 2GB`
   (Basic = 5 DTU flat; 2GB is 60× the registry+keys payload; upgrade path = online
   `az sql db update --edition Standard --service-objective S0`).
2. `az sql server ad-only-auth enable -g forge -s forge-sql-server` (do AFTER contained users exist? No —
   Entra admin retains access regardless; enable any time. Verify portal/query access via Entra before proceeding.)
3. `az identity create -g forge -n forge-mi` → then as Entra admin on DB `forge`:
   `CREATE USER [forge-mi] FROM EXTERNAL PROVIDER; ALTER ROLE db_owner ADD MEMBER [forge-mi];`
   (db_owner: app does DDL at startup. Unused until Azure hosting; provisioned now.)
4. Firewall: `scripts/refresh-sql-firewall.sh` creates/upserts rule `forge-dev-machine` with
   current egress IP. Keep `AllowAllWindowsAzureIps` (needed for future ACA outbound).
5. Optional: budget alert $10/mo on the subscription; `az sql db show-usage` in ops docs.
6. Free DB `free-sql-db-5815147`: untouched; used as `FORGE_DEV` rehearsal target.

## Phase B — provider seam + store ports (largest chunk; tests green at every step)
1. `Core/Db/`: `ForgeDbProvider { Sqlite, SqlServer }`, `IDbConnectionFactory`
   (`OpenAsync(ct)`, `Dialect`, `Qualifier`), `ISqlDialect` (identifier quoting, `Table(name)`
   → qualifier-qualified name, paging, upsert, insert-returning-id, bool/datetime DDL+DML
   mapping, `CREATE TABLE` guard), `SqliteDialect`, `SqlServerDialect`.
   **No Dapper/EF** — keep hand-rolled ADO per repo culture; stores become `DbConnection`-agnostic.
2. Add `Microsoft.Data.SqlClient` (6.x) to Forge.Core.csproj. Configure
   `SqlConnection.RetryLogicProvider = SqlConfigurableRetryFactory` (covers 40613 serverless
   resume, 10060, 40197) + factory-level open retry (5 tries, exponential, max ~60s) so a
   paused dev DB resumes transparently.
3. Port order (each = one commit, suite green): MemoryStore → IssueStore (T-SQL DDL at
   schema v23 equivalent) → ProjectStore/SecretStore/SkillStore/AgentStore → AgentRunStore/
   CostTracker/RecoveryReportStore → Sprint/Spec/Intake/ContextHandoff/DesignArtifact/
   ArtOutput/DesignerRun/GroomerRun/ArtistRun stores → Orchestrator stores (MemoryExtraction,
   SprintProposalAudit) → DeploymentStore. Program.cs direct `SqliteConnection` probes
   (lines ~635/673) move to factory.
4. Schema-per-project: factory carries `Qualifier`; `ProjectBootstrap` for provider=sqlserver
   lazily `CREATE SCHEMA proj_<id>` on project registration. Registry project → `dbo`.
5. Config: `db.provider` (`sqlite` default — tests/fresh clones unchanged),
   `db.connectionString` (no secrets in it — `Server=tcp:forge-sql-server.database.windows.net,1433;
   Initial Catalog=forge;Authentication=Active Directory Default;Encrypt=Strict`).
6. Resilience audit: every scheduler/dispatch tick catches DB-unavailable → log + skip cycle
   (never crash, never swallow silently); dashboard endpoints return 503-with-Retry-After on
   DB outage; `/api/health` (or --check path) reports DB connectivity.
7. `--check` gains a `db` section: connect, Entra token acquisition (names `az login` as the
   remediation on failure), schema version per store, round-trip latency.

## Phase C — migration tool + rehearsal
1. `dotnet run -- --migrate-db --target sqlserver [--connection-name dev] [--include-open-work] [--reset]`:
   - Copies `project`, `secret` (ciphertext as-is), all `memory` keys — upsert-idempotent.
   - `--include-open-work`: non-terminal issues + `issue_dep` edges + linked spec/sprint rows,
     FK-safe order (needed only if queue isn't drained at cutover).
   - `--reset`: drops per-project schemas for clean re-rehearsal on the dev DB.
   - Prints verification report (row counts per table, spot-check hashes of memory values).
2. Rehearsal against `free-sql-db-5815147`: migrate → run service with provider=sqlserver
   pointed at dev DB → dashboard + `--check` green → deliberately let dev DB auto-pause and
   verify resume-through-retry → tear down. This exercises the exact code path the Basic
   primary will use.

## Phase D — cutover runbook (operator-present)
1. Gate: drain or disposition the 9 non-terminal issues on `forge` (or pass `--include-open-work`).
2. Stop service → run `--migrate-db --target sqlserver` (Basic `forge` DB) → flip
   `db.provider=sqlserver` in `~/.config/forge/appsettings.json` → install ExecStartPre +
   `forge-sql-firewall.timer` → start → `--check` → verify dashboard renders + one canary
   task E2E (intake → groom → sprint → dispatch).
3. 48h soak. SQLite files stay untouched (rollback = flip provider back, restart). Archive
   (not delete) only after operator sign-off.

## Ops additions (this machine)
- `scripts/refresh-sql-firewall.sh`: egress IP (`ifconfig.me`) → upsert rule; also runs
  `az account get-access-token --resource https://database.windows.net` as a session keepalive.
- systemd: `ExecStartPre` calls the script; `forge-sql-firewall.timer` every 15 min (covers IP
  change while running + az token refresh window). az token expiry is the main auth failure
  mode — `--check` names `az login` as remediation; dashboard shows DB-auth banner on repeated
  failures.

## Testing
- All 953 existing tests stay on SQLite (unchanged behavior) — the seam keeps them fast.
- New: dialect unit tests (SQL text shaping per provider); store ports re-run existing store
  tests against both dialects where feasible via a parameterized harness.
- Opt-in integration suite (env-gated `FORGE_TEST_SQLSERVER=1`, points at dev DB; CI skips):
  schema creation, CRUD round-trip per store, migration tool round-trip + idempotency,
  40613 resume-through-retry.

## Risks / failure modes
- **az CLI session expiry** → new connections fail; keepalive timer + --check remediation + banner.
- **Firewall staleness mid-run** → retry layer keeps orchestrator alive-but-idle; 15-min timer heals.
- **Basic 2GB ceiling** → registry+keys is <2MB; monitor via `az sql db show-usage`; alert at 80%.
- **DataProtection keyring is machine-local** → secrets undecryptable anywhere else; Key Vault
  keyring is prerequisite for ACA/AKS (future work, listed below).
- **Single-writer invariant** unchanged: one orchestrator instance; AKS phase needs
  single-replica + leader election (future).
- **AGENTS.md module boundary** text changes: "Core has no I/O beyond SQLite" → "beyond the
  state database (SQLite | Azure SQL via the Core/Db seam)" — update AGENTS.md + CONTRIBUTING
  schema-migration section (dual-provider DDL convention: SQLite keeps migration chain,
  SQL Server fresh-creates at current version) + `docs/linux-deployment.md` addendum.

## Out of scope (future phases)
- ACA/AKS hosting, ephemeral agent pods, `forge-mi` actually being used.
- Key Vault-backed DataProtection keyring; Azure Arc; Private Link; multi-region.
- Migrating the 28MB history (can be revisited with `--include-open-work`-style bulk copy later).
