# Contributing

How the code is organized, what conventions to follow, and what to read first.

## Module layout

```
Forge/
├── Agents/                    # MAF agent implementations (Intake, Product, Groomer, Runner)
│   ├── MafAgentRunner.cs      # MAF ChatClientAgent + tool plumbing
│   ├── GroomerAgent.cs        # Spec -> stories+tasks decomposition
│   └── RoleAgentRegistry.cs   # task-type -> role mapping
├── AgentTools/                # AIFunctions exposed to the agent
│   └── BashTool.cs            # cmd.exe /c <command> with timeout
├── Core/                      # Domain types + stores (no I/O concerns beyond DB)
│   ├── IssueStore.cs          # SQLite: issue + issue_dep + issue_event (schema v7)
│   ├── MemoryStore.cs         # SQLite: memory (schema v7, lives in IssueStore's migration)
│   ├── SpecStore.cs           # specs + versions
│   ├── IssueEventRecord.cs    # event-log row
│   └── IssueDepTests/         # adjacency helpers
├── Configuration/             # appsettings.json shape
├── Dashboard/                 # Kestrel + static HTML + minimal-API endpoints
│   ├── DashboardHost.cs       # WebApplication composition root
│   ├── DashboardEndpoints.cs  # /api/state + /api/state/issues/{id}
│   ├── SpecEndpoints.cs       # /api/specs + /api/specs/{id}/groom
│   ├── MemoryEndpoints.cs     # /api/memory
│   ├── IssuesJsonlEndpoints.cs# /api/issues.jsonl streaming
│   └── wwwroot/index.html     # single-file SPA
├── Orchestrator/              # Dispatch loop + PR watch
│   ├── OrchestratorAgent.cs   # sequential dispatch (production path)
│   ├── Workflow/              # MAF Workflows implementation (parallel)
│   │   ├── EngineeringDispatchWorkflow.cs
│   │   ├── ClaimExecutor.cs
│   │   ├── WorktreeExecutor.cs
│   │   ├── RunAgentExecutor.cs
│   │   ├── CommitPushPrExecutor.cs
│   │   └── EnqueueWatchExecutor.cs
│   └── PRWatcher.cs
├── Reviewer/                  # Future: code-review agent
├── Program.cs                 # CLI entry point
└── appsettings.json           # gitignored; copy from appsettings.example.json
```

## Boundaries

- **Core/ has no I/O beyond the SQLite DB.** Stores expose `Task`-returning methods, but no HTTP, no GitHub, no LLM. The store classes take their paths via the constructor; they don't read env vars.
- **Agents/ depends on Core/ + Configuration/ + Dashboard/.** They publish `DashboardEvent` and call into stores. They don't read `appsettings.json` directly.
- **Orchestrator/ depends on Agents/ + Core/.** The dispatch loop's job is to glue stores + agent + git + GitHub together.
- **Dashboard/ depends on Core/ + Configuration/.** It reads stores and publishes/subscribes events. The `IDashboardEventBus` interface keeps the boundary clean.
- **Reviewer/ is empty today.** It's reserved for the future designer / artist agent roles.

If a new class needs to read `IOptions<AgentOptions>` AND write to `IssueStore` AND make HTTP calls, that's a code smell — split the responsibilities across Core / Agents / Orchestrator.

## Conventions

### Code style

- `TreatWarningsAsErrors=true` in the main project. The test project allows warnings (so test fixtures can be loose). New code must compile cleanly.
- `LangVersion=14`, `<Nullable>enable</Nullable>`. Use `string?`, not `string` for nullable params.
- Use `Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance` when you need a no-op logger in tests.
- Tests use xUnit + FluentAssertions-light (xUnit's `Assert.Equal` etc.). No Moq; we hand-roll fakes. Fakes are typed classes, not extension methods on `Mock<T>`.

### UI consistency (enforced, not aspirational)

- **Shared concepts render through shared components.** A concurrency slot is `<RoleSlotMeter>` everywhere (project drill-down AND the Agents page). If a concept appears on two pages, it gets ONE component in `Forge.UI/Components/`; pages compose, they don't re-render their own version.
- **No inline `style=` for shared visuals.** Cards, pills, grids, meters, banners, form controls come from the design-system classes in `Forge.UI/wwwroot/app.css` (`.card`, `.pill--*`, `.data-grid`, `.slot-card`, `.banner--*`, `.role-*`). New shared visuals get a class in `app.css` (documented with a comment), not a page-local inline style.
- **Cross-link related surfaces instead of duplicating them.** Role caps live on the project drill-down; role models/prompts live on `/agents`. Each links to the other. A setting edited in two places is a bug.
- Pages poll for live data with `PeriodicTimer` + `CancellationTokenSource` cancelled in `Dispose` (see `Agents.razor`).

### Schema migrations

Forge is dual-provider: SQLite (default; tests + local dev) and SQL
Server / Azure SQL (`db.provider=sqlserver`). The provider seam lives in
`Core/Db/` (factory + dialect). Rules for schema changes:

1. Bump `CurrentSchemaVersion` in `Core/IssueStore.cs`.
2. **SQLite**: add the migration to the raw-SQL block in
   `InitializeSchemaSqlite()` (and any guarded post-init `ApplyV*` step).
   Use `CREATE TABLE IF NOT EXISTS` / `CREATE INDEX IF NOT EXISTS` so
   re-runs are no-ops. The full v1→N chain is preserved for existing
   databases.
3. **SQL Server**: add the FINAL shape to `InitializeSchemaSqlServer()`.
   Azure databases are fresh-created at the current version — there is
   no T-SQL migration chain. Tables are guarded by
   `IF NOT EXISTS (SELECT 1 FROM sys.tables ...)`; indexes by
   `IF NOT EXISTS (SELECT 1 FROM sys.indexes ...)`. Constraints to know:
   - No multiple cascade paths (FKs with two parents get `ON DELETE
     NO ACTION` on the side that never deletes in practice).
   - Composite index keys must fit 900 bytes (clustered) / 1700 bytes
     (nonclustered) — id columns are `NVARCHAR(128)`.
   - `key` is reserved — quote as `[key]` (SQLite accepts brackets too,
     so use them everywhere in shared queries).
4. Queries must be provider-neutral: `@` params only, table names via
   `Dialect.Table()`, row caps via `Dialect.Top/Limit(Param)`. Branch
   per call-site for upserts (`MERGE ... WITH (HOLDLOCK)` vs
   `ON CONFLICT`) and identity return (`OUTPUT INSERTED.id` vs
   `RETURNING` / `last_insert_rowid()`). Use `DbCommandExtensions.AddParam`
   (not provider-specific `Parameters.AddWithValue`) in ported code.
5. Integer column types must match reader accessors exactly
   (`GetInt32` ↔ INT, `GetInt64` ↔ BIGINT) — SQLite is permissive,
   SqlClient is not.
6. Run the orchestrator once against an existing SQLite DB to apply the
   migration, then `--check`. For SQL Server, rehearse with
   `--migrate-db --target sqlserver --reset` against the dev database.
7. If you need to read a new column, extend the record AND every
   `SELECT` that materializes it.

### Adding a new AIFunction

AIFunctions are how the model invokes code on the host. To add one:

1. Create the class in `AgentTools/` (or wherever the function lives).
2. Implement the work. Use `[Description("...")]` on every parameter — the model sees these.
3. If parameters are optional, give them C# default values (`string? param = null`, not just `string? param` — the MAF binder requires defaults, otherwise it throws `ArgumentException` when the model omits the param).
4. Wire it into `MafAgentRunner.RunAsync` via `AIFunctionFactory.Create(...)` and add to the `tools` list.
5. Test by hand-rolling a `ScriptedChatClient` that returns a `FunctionCallContent` and asserting the function ran.

### Adding a new role

1. Add the enum value to `IAgent.AgentType`.
2. Register the role in `Agents/RoleAgentRegistry.cs` (agent name, project subdir, allowed tools).
3. Add a `llm.roles.<NewRole>` block in `appsettings.json`.
4. Drop a system-prompt template at `<workspace>/agents/<newrole>.md`.
5. Update `RoleAgentRegistry.FromTaskType` if you want certain task types to map to the new role.

### Adding a new workflow executor

1. Create a new file in `Orchestrator/Workflow/` named `<Name>Executor.cs`.
2. Inherit from `FunctionExecutor<TIn, TOut>`. Pick the input type from the previous executor's output and define the output record.
3. Implement the work in a `public static ValueTask<TOut> HandleAsync(TIn input, ...deps..., CancellationToken ct)` so tests can call it without a `WorkflowHost`.
4. Add it to `EngineeringDispatchWorkflow`'s constructor + `Build()`.
5. Test by calling `HandleAsync` directly + by running the full `Build()` graph against a real temp git repo.

### Adding a new minimal-API endpoint

1. Add the method to the appropriate `*Endpoints.cs` file (`DashboardEndpoints.cs` for `/api/state/*`, `SpecEndpoints.cs` for `/api/specs/*`, `MemoryEndpoints.cs` for `/api/memory`, `IssuesJsonlEndpoints.cs` for the JSONL stream).
2. Use `Results.Json(...)` for JSON returns, `Results.NotFound()` for 404, `Results.BadRequest(new { error = "..." })` for 400.
3. Try/catch around any I/O and log the error; never let an exception escape as an empty-body 500.
4. Test by spinning up a Kestrel `WebApplication` with the real endpoints and asserting on `HttpClient.GetAsync` / `PostAsJsonAsync` / etc.

## Testing patterns

### Store tests (IssueStore, MemoryStore, SpecStore)

- One test class per store.
- Each test uses a fresh per-test DB path (`Path.GetTempPath()/ph-...-{Guid:N}.db`) — never share state.
- `Dispose()` removes the DB + WAL/SHM sidecars.
- For tests that need the schema but aren't really testing the schema bootstrap, do `_ = new IssueStore(_dbPath)` in the constructor and discard.

### Workflow executor tests

- Tests call the executor's `public static HandleAsync` directly. The executor's `FunctionExecutor` base class exists for MAF runtime compatibility; the tests bypass it because spinning up a `WorkflowHost` per test is overkill.
- For executors that hit git, use a real temp git repo (`InitRepo(dir)`) + a fresh `GitWorktreeService` against it.
- For executors that hit the LLM, use a `ScriptedChatClient` (a fake `IChatClient` that returns canned `ChatResponse` objects).

### Endpoint tests

- Use `WebApplication.CreateBuilder()` + a random ephemeral port.
- Build a real `WebApplication` (not `TestServer`) — the live `Program.cs` uses Kestrel, and the test mirrors that.
- `_host.Start()` + `HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") }` + `Dispose` in the test's `Dispose`.
- Pre-seed the DB on disk if your test depends on existing state.

### Live demo

After a non-trivial change, run the live demo:
1. Wipe the DB (`rm .portHorizon/state/issues.db*`).
2. Start the orchestrator.
3. Enqueue a task.
4. Watch the dashboard + JSONL file.
5. Confirm the PR was opened.

The live demo is the most thorough test. It exercises the full pipeline end-to-end, including things unit tests don't catch (process startup order, file locking, network timing, model behavior).

## Things to avoid

- **`Moq`, `NSubstitute`, or any mocking framework.** Hand-roll fakes. The interfaces are small enough that fakes are clearer than mocks.
- **`Task.Run` to "fix" async signatures.** If a method is `async`, make it actually await. If it's sync, don't make it look async.
- **Swallowing exceptions in production paths.** Log them. If the operation is non-critical, log + return early. Don't `try { ... } catch (Exception) { }`.
- **Adding `--dashboard-only`-style escape hatches** without an operator's explicit approval. They tend to outlast their original purpose (we're keeping `--dashboard-only` because removing it would break a working CLI flag, but future flags need user sign-off).
- **Cross-test shared state.** Use `Path.GetTempPath() + Guid.NewGuid()` for any DB or temp directory.

## Reading order if you're new

1. `docs/system-flow.md` — what runs when a task is dispatched
2. `docs/vision-status.md` — what each phase of the design doc looks like in the live system
3. `docs/operator-cookbook.md` — how an operator uses it
4. `Program.cs` — the CLI entry point
5. `Core/IssueStore.cs` — the heart of the system; everything else hangs off this
6. `Orchestrator/OrchestratorAgent.cs::DispatchSingleTaskAsync` — the dispatch loop
7. `Agents/MafAgentRunner.cs` — how the agent runs
8. `Orchestrator/Workflow/EngineeringDispatchWorkflow.cs` — the MAF-Workflows version (parallel impl, dormant)
9. `Dashboard/DashboardHost.cs` — how the HTTP surface composes
10. The test suite — `tests/Forge.Tests/IssueStoreTests.cs` is the best entry point

## Asking for help

If you're stuck, the first thing to do is run `dotnet run --project Forge -- --check`. If that passes, the bug is somewhere in the dispatch path. If it fails, the failure message will tell you which subsystem to investigate.
