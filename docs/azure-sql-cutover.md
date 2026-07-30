# Azure SQL cutover runbook

How to move Forge's state database from local SQLite files to Azure SQL.
The rehearsal (against the free serverless dev DB) has already been
executed end-to-end on 2026-07-27: schema creation, migration, `--check`,
and a full-service run (registry, per-project stores, secrets, memory,
skill seeding, recovery pass, dashboard endpoints) all passed.

## Resources (already provisioned)

| Resource | Value |
|---|---|
| Server | `forge-sql-server.database.windows.net` (RG `forge`, centralus) |
| Production DB | `forge` — Basic tier (5 DTU, 2GB, ~$5/mo flat, always online) |
| Dev/rehearsal DB | `free-sql-db-5815147` — free-offer serverless (0.5–2 vCore, auto-pause 60 min, 100k vCore-s/month grant). **Not viable as a 24/7 primary** — a chatty always-on orchestrator burns the grant in ~3 days and `AutoPause` then freezes the DB for the rest of the month. |
| Auth | Entra-only (`AzureAdOnlyAuthentication=true`; SQL logins rejected). Entra admin: `jtn5016@gmail.com` |
| Managed identity | `forge-mi` (user-assigned, RG `forge`). Contained user + `db_owner` created in both DBs — reserved for the future ACA/AKS cutover; this machine authenticates as the Entra admin via Azure CLI credential |
| Firewall | `forge-dev-machine` rule auto-refreshed by `scripts/refresh-sql-firewall.sh` (systemd ExecStartPre + `forge-sql-firewall.timer` every 15 min, also refreshes the az token) |

## Config shape

```json
"db": {
  "provider": "sqlserver",
  "connectionString": "Server=tcp:forge-sql-server.database.windows.net,1433;Initial Catalog=forge;Authentication=Active Directory Default;Encrypt=True;Connect Timeout=30"
}
```

`db.provider` defaults to `sqlite` (tests, fresh clones). On SQL
Server the database is split into two schema profiles, both created
lazily on first store construction:

| Profile | Schema | Holds |
|---|---|---|
| Core | `core` | Registry: `project`, `secret`, `agent`, `skill` (one per database) |
| Workload | `proj_<id>` | Everything else: `issue`, `issue_event`, `sprint`, `spec`, `memory`, `agent_run`, `codebase_graph_cache`, `design_artifact`, etc. |

There is no `proj_default` anymore — the registry (project + secret
+ agent + skill) lives in `core`, and each workload project's schema
materializes the first time a store is built against it. The v23 →
v24 transition is handled by the migration runner (see below), so
databases cut over before the split keep their existing data.

SQL Server schema changes for EXISTING databases flow through ordered
migrations in `Core/Db/Migrations/` (registered in
`SqlServerMigrations.All`; v24 is the profile split itself). The
runner applies `version > MAX(schema_version)` per schema and stamps
it; migrations are idempotent and profile-aware (Core vs Workload get
different steps). Fresh databases skip the chain entirely — they
are born at the current shape via the final DDL in
`InitializeSchemaSqlServer()`. See `CONTRIBUTING.md` → "Schema
migrations" for the rules, index/constraint gotchas, and how to add
the next migration. Files that stay local regardless: repos/
worktrees, logs, issues.jsonl (viewer artifact), art output, codebase
graph cache, DataProtection keyring (`~/.aspnet/DataProtection-Keys/`).

## Cutover (operator-present)

1. **Gate: drain or disposition open work.** Registry + keys migration
   does NOT carry task history. Non-terminal issues (Pending/Blocked/
   InProgress) are either (a) allowed to finish, (b) dropped and
   re-enqueued post-cutover, or (c) carried with `--include-open-work`.
2. Stop the service: `systemctl --user stop forge`
3. Migrate:
   ```bash
   dotnet run --project Forge.Core/Forge.Core.csproj -- \
     --config ~/.config/forge/appsettings.json \
     --migrate-db --target sqlserver \
     [--include-open-work] \
     --connection-string "Server=tcp:forge-sql-server.database.windows.net,1433;Initial Catalog=forge;Authentication=Active Directory Default;Encrypt=True;Connect Timeout=30"
   ```
   Idempotent — safe to re-run. `--reset` drops every `proj_*` schema
   and the `core` registry schema first (rehearsal only, never against
   the production DB after cutover).
4. Flip `db.provider`/`db.connectionString` in
   `~/.config/forge/appsettings.json` (shape above; production DB name
   `forge`, not the dev one).
5. `systemctl --user start forge` → `--check` → verify the dashboard
   renders and one canary task flows intake → groom → sprint → dispatch.
6. **48h soak.** SQLite files stay untouched — rollback = flip
   `db.provider` back to `sqlite` and restart. Archive (not delete) the
   .db files only after explicit sign-off.

## Failure modes and where to look

| Symptom | Cause | Remediation |
|---|---|---|
| New connections fail with AADSTS / AzureCliCredential errors | az CLI session expired | `az login` (the 15-min timer + `--check` both surface this) |
| Connections hang/fail after an IP change | firewall rule stale | heals within 15 min via `forge-sql-firewall.timer`; or run `scripts/refresh-sql-firewall.sh` |
| First query after dev-DB idle takes ~60s | serverless auto-pause resume | expected; the connection retry provider (10 tries / 90s) rides it out. Production Basic tier never pauses |
| `--check` reports schema mismatch | app upgraded before first start | run the orchestrator once, then re-check |

## ACA/AKS cutover (future)

Config-only by design: attach `forge-mi` to the compute, set the same
connection string (Active Directory Default picks up the managed
identity automatically), drop the firewall-refresh timer (use
AllowAllWindowsAzureIps or a private endpoint). Prerequisites not yet
built: Key Vault-backed DataProtection keyring (secrets are
machine-encrypted today), single-replica orchestrator / leader
election, ephemeral-agent work placement.
