# Intake browser qualification

This disposable host serves the production Blazor Intake page, IntakeAgent,
project-scoped endpoints, InitiativeApprovalService, and real proposal/ingestion,
epic/spec stores. It does not load deployment configuration, secrets or schedulers.
Only the model provider and unrelated shell status responses are fixtures.
The deterministic provider answers a conversation and calls the real create_epic
tool to shape a linked unapproved brief; seeded real proposal revisions
exercise comparison. No request reaches an external model or incurs API charges.

On Linux, build `tools/intake-rehearsal/IntakeRehearsal.csproj -c Release`, then run
its DLL with `ASPNETCORE_ENVIRONMENT=Development`, `--state /new/disposable/path`
and optionally `--port 5127`. The state directory must not already exist. Each
project has its own SQLite fixture database: this is test infrastructure and does
not replace production Azure SQL. Keep the host bound to its default loopback.

Run `NODE_PATH=/path/to/playwright/node_modules node tools/intake-rehearsal/verify.cjs
/path/to/evidence`. Set `CHROMIUM_EXECUTABLE_PATH` for the installed browser and
`INTAKE_URL` if changing the host port. The script needs a fresh host/state for
repeat runs. It saves screenshots and a JSON receipt with real persisted outcomes.

Checks include inbox/default/return, new conversation and reload, multiple briefs,
revision comparison, exact revision/hash approval, real one-epic/one-spec ingestion,
Pending refinement receipt, repeat-request idempotency, stale revision rejection,
explicit refresh, project isolation, scoped handoff, and mobile layout. It does
not claim live-model editorial quality, completed refinement, Azure SQL coverage,
or downstream scheduling. The Pending receipt is intentionally genuine: there
is no scheduler pretending to finish the work.

The `/__qualification/*` endpoints exist only in this isolated executable. Do not
add them to the production host or run this host against existing state.

For a single command with automatic host cleanup, set DOTNET and NODE_PATH as
needed and run `bash tools/intake-rehearsal/run.sh /new/evidence/path`.
The read-only `smoke-readonly.cjs URL evidence-directory` checks the deployed
inbox at desktop/mobile widths and blocks all browser API writes.

For visual inspection of real long-form content, pass `--replay /private/export.json`
to the disposable host. The export contains `session`, `previous` (revision 1),
`proposal` (the detail endpoint's revision 2 response), and optionally `spec`.
The host copies these into its fresh fixture database using domain stores.
This mode preserves source wording for screenshots. Its timestamps and record IDs
belong to the disposable replay, and its refinement is seeded, not model-produced.
Keep exports outside source control. Run the ordinary approval rehearsal without
`--replay`, because its zero-work assertions assume the default empty workload.
