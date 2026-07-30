using System.Data.Common;
using Forge.Core.Db;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Forge.Core;

public enum IssueStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Blocked,
    Closed
}

public sealed record IssueRecord(
    string Id,
    string ShortId,
    string Type,
    string Title,
    string? Description,
    IssueStatus Status,
    int Priority,
    string? Assignee,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ClosedAt,
    string MetadataJson,
    string? ParentIssueId = null,
    DispatchCheckpoint? DispatchCheckpoint = null,
    DateTime? CheckpointAt = null,
    int RecoveryAttempts = 0)
{
    public string? GetMetadata(string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(MetadataJson) ? "{}" : MetadataJson);
            if (!doc.RootElement.TryGetProperty(key, out var v)) return null;
            // JSON null is the delete idiom: a cleared key reads as
            // absent, not as the literal string "null".
            return v.ValueKind == JsonValueKind.Null ? null : v.ToString();
        }
        catch
        {
            return null;
        }
    }
}

public sealed record NewIssue(
    string Type,
    string Title,
    string? Description = null,
    int Priority = 2,
    string? Assignee = null,
    IReadOnlyDictionary<string, object>? Metadata = null,
    string? ParentId = null);

public sealed record IssueFilter
{
    public IssueStatus? Status { get; init; }
    public string? Assignee { get; init; }
    public string? Type { get; init; }
}

/// <summary>
/// Kind of edge in the issue dependency graph. Only <see cref="Blocks"/>
/// gates dispatch (see <c>IssueStore.ReadyAsync</c> + <c>ClaimAsync</c>).
/// <see cref="Related"/> and <see cref="Duplicates"/> are informational
/// for the dashboard / graph view and do not affect the ready queue.
/// </summary>
public enum IssueDepKind
{
    Blocks,
    Related,
    Duplicates,
}

public static class IssueDepKindExtensions
{
    public static string ToDbValue(this IssueDepKind k) => k switch
    {
        IssueDepKind.Blocks => "blocks",
        IssueDepKind.Related => "related",
        IssueDepKind.Duplicates => "duplicates",
        _ => "blocks",
    };

    public static bool TryParseDb(string s, out IssueDepKind kind)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "blocks": kind = IssueDepKind.Blocks; return true;
            case "related": kind = IssueDepKind.Related; return true;
            case "duplicates": kind = IssueDepKind.Duplicates; return true;
            default: kind = IssueDepKind.Blocks; return false;
        }
    }
}

/// <summary>
/// One edge in the issue dependency graph. <see cref="BlockerId"/> must
/// resolve (terminal state for <c>Blocks</c>) before <see cref="BlockedId"/>
/// can be dispatched. <see cref="Kind"/> = <c>Related</c>/<c>Duplicates</c>
/// are advisory only.
/// </summary>
public sealed record IssueEdge(
    string BlockerId,
    string BlockedId,
    IssueDepKind Kind,
    DateTime CreatedAt);

public interface IIssueStore
{
    Task<IssueRecord> CreateAsync(NewIssue spec, CancellationToken ct = default);
    Task<IReadOnlyList<IssueRecord>> ListAsync(IssueFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<IssueRecord>> ReadyAsync(int limit, CancellationToken ct = default);
    Task<IReadOnlyList<IssueRecord>> ReadyAsync(int limit, string? sprintId, CancellationToken ct = default);
    Task<IssueRecord?> ClaimAsync(string id, string assignee, CancellationToken ct = default);
    Task<IssueRecord> TransitionAsync(string id, IssueStatus to, string? error, IDictionary<string, object>? metadata = null, CancellationToken ct = default);
    Task<IssueRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<IssueEventRecord> AddEventAsync(string id, string kind, string? detail = null, CancellationToken ct = default);

    // P4 Stage A — checkpoint-based recovery. See
    // docs/p4-restart-safety.md.
    /// <summary>
    /// Advance the dispatch checkpoint on an in-flight issue.
    /// Each engineering dispatch executor calls this BEFORE its
    /// side-effect so a recoverer knows "this side-effect has
    /// happened" on restart.
    /// </summary>
    Task SetCheckpointAsync(string id, DispatchCheckpoint checkpoint, CancellationToken ct = default);
    /// <summary>
    /// List every issue currently in <c>InProgress</c> with
    /// <c>assignee=forge</c>. These are the candidates the
    /// StartupRecovery pass inspects on a restart.
    /// </summary>
    Task<IReadOnlyList<IssueRecord>> ListInProgressForRecoveryAsync(CancellationToken ct = default);
    /// <summary>
    /// Increment the <c>recovery_attempts</c> counter on an
    /// issue. Used by the StartupRecovery pass to break loops
    /// (an issue that keeps crashing on commit is failed after
    /// a configurable number of attempts).
    /// </summary>
    Task<int> IncrementRecoveryAttemptsAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<IssueEventRecord>> ListEventsAsync(string issueId, int limit = 50, CancellationToken ct = default);

    // Dependency graph (Phase 2 of docs/embedded-issues.md).
    Task<IssueEdge> AddDependencyAsync(string blockerId, string blockedId, IssueDepKind kind, CancellationToken ct = default);
    Task<bool> RemoveDependencyAsync(string blockerId, string blockedId, IssueDepKind kind, CancellationToken ct = default);
    Task<IReadOnlyList<IssueEdge>> DependenciesAsync(string id, CancellationToken ct = default);
    /// <summary>
    /// True iff at least one open-blocker (kind=Blocks, blocker status in
    /// {Pending, InProgress, Blocked, Failed}) exists. <c>Completed</c> /
    /// <c>Closed</c> blockers do not gate; <c>Failed</c> does (until the
    /// operator clears it). Cheap: indexed lookup, single round-trip.
    /// </summary>
    Task<bool> IsBlockedAsync(string id, CancellationToken ct = default);
    /// <summary>
    /// The set of OPEN blocker ids across all <c>blocks</c> edges
    /// pointing at any of <paramref name="blockedIds"/> — used by
    /// sprint injection to find ad-hoc tasks that unblock ongoing
    /// sprint work. Open = blocker status NOT IN
    /// ('Completed','Closed') (Failed stays open until the operator
    /// clears it, per the ReadyAsync rule).
    /// </summary>
    Task<HashSet<string>> ListBlockersOfAsync(IReadOnlyCollection<string> blockedIds, CancellationToken ct = default);
}

/// <summary>
/// SQLite-backed issue store. WAL mode + busy_timeout make the store safe
/// for concurrent writers (orchestrator + enqueue CLI + dashboard reads)
/// without losing updates the way the previous state.json did.
///
/// Implements Phase 1 of docs/embedded-issues.md: just the issue/event/memory
/// tables + the four core methods. Stays additive to (not replacing) StateStore,
/// which still owns orchestrator settings + heartbeats.
/// </summary>
public sealed class IssueStore : IIssueStore, IAsyncDisposable
{
    public const int CurrentSchemaVersion = 25;
    public const string DateFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private readonly IDbConnectionFactory _db;
    private readonly string _dbPath;

    public string DbPath => _dbPath;

    /// <summary>Shared connection factory — sibling stores (ProjectStore,
    /// SecretStore, …) hang off the same logical database via this.</summary>
    public IDbConnectionFactory Db => _db;

    public IssueStore(string dbPath)
        : this(ForgeDb.Sqlite(BuildSqliteConnectionString(dbPath)))
    {
        _dbPath = dbPath;
    }

    private static string BuildSqliteConnectionString(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    public IssueStore(IDbConnectionFactory db)
    {
        _db = db;
        _dbPath = "";
        InitializeSchema();
    }

    /// <summary>
    /// Run the schema migration. Idempotent. Called from the
    /// constructor on first creation; also publicly callable so
    /// that other stores (e.g. <c>ContextHandoffStore</c>) can
    /// ensure the v12 migration is applied before they read the
    /// table.
    /// </summary>
    public void EnsureSchema() => InitializeSchema();

    private void InitializeSchema()
    {
        if (_db.Provider == ForgeDbProvider.SqlServer)
        {
            InitializeSchemaSqlServer();
            return;
        }
        InitializeSchemaSqlite();
    }

    private void InitializeSchemaSqlite()
    {
        using var conn = new SqliteConnection(_db.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS issue (
                id            TEXT PRIMARY KEY,
                short_id      TEXT NOT NULL,
                type          TEXT NOT NULL,
                title         TEXT NOT NULL,
                description   TEXT,
                status        TEXT NOT NULL,
                priority      INTEGER NOT NULL DEFAULT 2,
                assignee      TEXT,
                created_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL,
                closed_at     TEXT,
                metadata_json TEXT NOT NULL DEFAULT '{}',
                parent_issue_id TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_issue_status     ON issue(status);
            CREATE INDEX IF NOT EXISTS ix_issue_assignee   ON issue(assignee);
            CREATE INDEX IF NOT EXISTS ix_issue_updated_at ON issue(updated_at);
            CREATE INDEX IF NOT EXISTS ix_issue_type_short ON issue(type, short_id);
            CREATE INDEX IF NOT EXISTS ix_issue_parent     ON issue(parent_issue_id);

            CREATE TABLE IF NOT EXISTS issue_event (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                issue_id    TEXT NOT NULL REFERENCES issue(id) ON DELETE CASCADE,
                ts          TEXT NOT NULL,
                actor       TEXT,
                kind        TEXT NOT NULL,
                detail      TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_issue_event_issue ON issue_event(issue_id, ts);

            CREATE TABLE IF NOT EXISTS issue_seq (
                type TEXT PRIMARY KEY,
                next_short INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS schema_version (
                version    INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );

            
            -- v2 tables: agent, skill, sprint, sprint_issue
            CREATE TABLE IF NOT EXISTS agent (
                id           TEXT PRIMARY KEY,
                agent_name   TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                scope        TEXT NOT NULL DEFAULT '',
                description  TEXT,
                enabled      INTEGER NOT NULL DEFAULT 1,
                config_json  TEXT NOT NULL DEFAULT '{}',
                created_at   TEXT NOT NULL,
                updated_at   TEXT NOT NULL
            );
            -- v16: rename kilo_name -> agent_name on legacy DBs.
            -- (Fresh DBs already use agent_name, so the rename is a no-op
            -- via the IF EXISTS guard below.)
            -- v16: rename orchestrator runner assignee 'kilo' -> 'forge'.
            -- (Same idempotency guard.)

            -- v17: project registry table. Source of truth for projects
            -- that are registered at runtime via POST /api/projects; the
            -- appsettings.json projects[] list seeds the initial set on
            -- first boot (one-time copy). Idempotent.
            CREATE TABLE IF NOT EXISTS project (
                id              TEXT PRIMARY KEY,
                name            TEXT NOT NULL,
                repo_url        TEXT NOT NULL,
                default_branch  TEXT NOT NULL DEFAULT 'main',
                local_path      TEXT,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                last_synced_at  TEXT,
                last_sync_error TEXT,
                roles_json      TEXT NOT NULL DEFAULT '{}'
            );
            -- v18: per-project secret store. Ciphertext is opaque
            -- (encrypted with IDataProtector using a per-deployment
            -- master key). Kinds are an open string: 'github_token',
            -- 'kilo_gateway_api_key', 'meshy_api_key', etc. The
            -- (project_id, kind) pair is unique. When the row is
            -- missing for a given (project_id, kind), the orchestrator
            -- falls back to the global env/appsettings value so
            -- existing setups keep working.
            CREATE TABLE IF NOT EXISTS secret (
                id          TEXT PRIMARY KEY,
                project_id  TEXT NOT NULL,
                kind        TEXT NOT NULL,
                ciphertext  BLOB NOT NULL,
                created_at  TEXT NOT NULL,
                updated_at  TEXT NOT NULL,
                FOREIGN KEY (project_id) REFERENCES project(id) ON DELETE CASCADE,
                UNIQUE (project_id, kind)
            );

            -- v20: agent run registry + transcripts. One row per
            -- agent run (engineering roles via MafAgentRunner):
            -- written at run start (status='running') so the
            -- dashboard sees who is doing what in near real time,
            -- finished with outcome + the FULL conversation
            -- transcript (roles, text, tool calls + results) for the
            -- run-detail view. Retention (AgentRunStore.FinishAsync):
            -- 30 days / newest 50 per task — transcripts are the
            -- high-volume data and the 35GB budget is protected by
            -- pruning, not by truncating content.
            -- v25: phase (plan gate / implementing / verifying n/3 /
            -- reviewing — the dashboard's "what is the run doing
            -- right now" label) + resumed_session (the run resumed a
            -- persisted MAF session instead of starting cold).
            CREATE TABLE IF NOT EXISTS agent_run (
                id               TEXT PRIMARY KEY,
                task_id          TEXT,
                role             TEXT NOT NULL,
                model            TEXT,
                status           TEXT NOT NULL,
                started_at       TEXT NOT NULL,
                finished_at      TEXT,
                duration_ms      INTEGER,
                message_count    INTEGER,
                tool_call_count  INTEGER,
                text_chars       INTEGER,
                error            TEXT,
                transcript_json  TEXT,
                last_activity_at TEXT,
                phase            TEXT,
                resumed_session  INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_agent_run_started ON agent_run(started_at DESC);
            CREATE INDEX IF NOT EXISTS idx_agent_run_task ON agent_run(task_id);

            CREATE TABLE IF NOT EXISTS skill (
                id           TEXT PRIMARY KEY,
                name         TEXT NOT NULL,
                description  TEXT,
                body         TEXT NOT NULL,
                agent_id     TEXT REFERENCES agent(id) ON DELETE CASCADE,
                enabled      INTEGER NOT NULL DEFAULT 1,
                created_at   TEXT NOT NULL,
                updated_at   TEXT NOT NULL,
                UNIQUE (name, agent_id)
            );
            CREATE INDEX IF NOT EXISTS ix_skill_agent ON skill(agent_id);

            CREATE TABLE IF NOT EXISTS sprint (
                id           TEXT PRIMARY KEY,
                name         TEXT NOT NULL,
                goal         TEXT NOT NULL,
                start_date   TEXT NOT NULL,
                end_date     TEXT NOT NULL,
                status       TEXT NOT NULL DEFAULT 'active',
                created_at   TEXT NOT NULL,
                updated_at   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_sprint_status ON sprint(status);

            CREATE TABLE IF NOT EXISTS sprint_issue (
                sprint_id   TEXT NOT NULL REFERENCES sprint(id) ON DELETE CASCADE,
                issue_id    TEXT NOT NULL REFERENCES issue(id) ON DELETE CASCADE,
                added_at    TEXT NOT NULL,
                PRIMARY KEY (sprint_id, issue_id)
            );
            CREATE INDEX IF NOT EXISTS ix_sprint_issue_sprint ON sprint_issue(sprint_id);

            CREATE UNIQUE INDEX IF NOT EXISTS uq_sprint_active
                ON sprint(status) WHERE status = 'active';

            -- v3 tables: intake_session, intake_message
            CREATE TABLE IF NOT EXISTS intake_session (
                id         TEXT PRIMARY KEY,
                project_id TEXT NOT NULL,
                title      TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_intake_session_project ON intake_session(project_id, updated_at DESC);

            CREATE TABLE IF NOT EXISTS intake_message (
                id                   INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id           TEXT NOT NULL REFERENCES intake_session(id) ON DELETE CASCADE,
                role                 TEXT NOT NULL,
                content              TEXT NOT NULL,
                ts                   TEXT NOT NULL,
                proposed_epic_id     TEXT,
                proposed_epic_title  TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_intake_message_session ON intake_message(session_id, id);

            -- v4 tables: spec, spec_version
            CREATE TABLE IF NOT EXISTS spec (
                id              TEXT PRIMARY KEY,
                project_id      TEXT NOT NULL,
                title           TEXT NOT NULL,
                status          TEXT NOT NULL,
                parent_issue_id TEXT,
                parent_spec_id  TEXT,
                current_version INTEGER NOT NULL DEFAULT 1,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_spec_project ON spec(project_id, updated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_spec_status ON spec(status);

            CREATE TABLE IF NOT EXISTS spec_version (
                spec_id     TEXT NOT NULL REFERENCES spec(id) ON DELETE CASCADE,
                version     INTEGER NOT NULL,
                body        TEXT NOT NULL,
                author      TEXT,
                created_at  TEXT NOT NULL,
                PRIMARY KEY (spec_id, version)
            );
            CREATE INDEX IF NOT EXISTS ix_spec_version_spec ON spec_version(spec_id, version DESC);

            -- v5 tables: spec_diagram, spec_touches, spec_dep (derived
            -- from the spec body by SpecBodyExtractor). Plus the
            -- extracted_at column on spec itself.
            ALTER TABLE spec ADD COLUMN extracted_at TEXT;
            CREATE TABLE IF NOT EXISTS spec_diagram (
                spec_id  TEXT NOT NULL REFERENCES spec(id) ON DELETE CASCADE,
                ordinal  INTEGER NOT NULL,
                kind     TEXT NOT NULL,
                source   TEXT NOT NULL,
                title    TEXT,
                PRIMARY KEY (spec_id, ordinal)
            );
            CREATE TABLE IF NOT EXISTS spec_touches (
                spec_id     TEXT NOT NULL REFERENCES spec(id) ON DELETE CASCADE,
                module_id   TEXT NOT NULL,
                source      TEXT NOT NULL,
                rationale   TEXT,
                created_at  TEXT NOT NULL,
                PRIMARY KEY (spec_id, module_id, source)
            );
            CREATE INDEX IF NOT EXISTS ix_spec_touches_module ON spec_touches(module_id);
            CREATE TABLE IF NOT EXISTS spec_dep (
                from_spec_id  TEXT NOT NULL REFERENCES spec(id) ON DELETE CASCADE,
                to_spec_id    TEXT NOT NULL,
                kind          TEXT NOT NULL,
                rationale     TEXT,
                source        TEXT NOT NULL,
                created_at    TEXT NOT NULL,
                PRIMARY KEY (from_spec_id, to_spec_id, kind)
            );
            -- Note: to_spec_id has NO foreign key. Spec bodies may
            -- reference future specs that don't exist yet. The
            -- dashboard's Deps tab renders the spec id even if the
            -- target isn't in the catalog; cleanup runs when the target
            -- is deleted (see spec_dep_cascade_cleanup trigger below).
            CREATE INDEX IF NOT EXISTS ix_spec_dep_to ON spec_dep(to_spec_id);
            CREATE TABLE IF NOT EXISTS codebase_graph_cache (
                repo_sha    TEXT PRIMARY KEY,
                built_at    TEXT NOT NULL,
                file_count  INTEGER NOT NULL,
                edge_count  INTEGER NOT NULL
            );

            -- v5: issue.parent_issue_id is now part of the issue
            -- table (see CREATE TABLE above). Stories and tasks link
            -- to their spec via the spec's own parent_issue_id; a
            -- task links to its story via issue.parent_issue_id.

            -- v6: issue_dep table — the dependency graph.
            -- Edges, not columns, because one issue can block many and
            -- be blocked by many. The 'blocks' kind is the only one
            -- that gates dispatch (see ReadyAsync/ClaimAsync); the
            -- others are advisory for the dashboard's graph view.
            CREATE TABLE IF NOT EXISTS issue_dep (
                blocker_id  TEXT NOT NULL REFERENCES issue(id) ON DELETE CASCADE,
                blocked_id  TEXT NOT NULL REFERENCES issue(id) ON DELETE CASCADE,
                kind        TEXT NOT NULL DEFAULT 'blocks',
                created_at  TEXT NOT NULL,
                PRIMARY KEY (blocker_id, blocked_id, kind)
            );
            CREATE INDEX IF NOT EXISTS ix_issue_dep_blocked ON issue_dep(blocked_id, kind);
            CREATE INDEX IF NOT EXISTS ix_issue_dep_blocker ON issue_dep(blocker_id, kind);

            -- v7: memory table — persistent project memory (the
            -- `bd remember` analog). Keyed by string, optional TTL.
            CREATE TABLE IF NOT EXISTS memory (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                ts          TEXT NOT NULL,
                key         TEXT NOT NULL UNIQUE,
                body        TEXT NOT NULL,
                ttl_days    INTEGER
            );
            CREATE INDEX IF NOT EXISTS ix_memory_key ON memory(key);

            -- v8: issue_groomer_run table — every time the Groomer
            -- runs against a spec (manual via POST /api/specs/{id}/groom
            -- or scheduled via the IHostedService), a row is written
            -- here so the dashboard's Groomer timeline can show
            -- what happened, when, and what stories were produced.
            -- spec_id references spec.id (NOT issue.id); we don't
            -- enforce it as a SQL FK because the spec table is
            -- managed by SpecStore and lives in the same DB but
            -- has its own creation path. The GroomerRunStore + the
            -- UI's spec lookup are the source of truth for the
            -- referential relationship.
            CREATE TABLE IF NOT EXISTS issue_groomer_run (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ts                  TEXT NOT NULL,
                spec_id             TEXT NOT NULL,
                trigger_kind        TEXT NOT NULL,    -- 'manual' | 'scheduled'
                status              TEXT NOT NULL,    -- 'started' | 'succeeded' | 'failed'
                stories_produced    INTEGER NOT NULL DEFAULT 0,
                tasks_produced      INTEGER NOT NULL DEFAULT 0,
                error               TEXT,
                duration_ms         INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_issue_groomer_run_spec ON issue_groomer_run(spec_id, ts);
            CREATE INDEX IF NOT EXISTS ix_issue_groomer_run_ts ON issue_groomer_run(ts);

            -- v9: design_artifact + designer_run tables. The
            -- Designer agent runs after Product and before Groomer.
            -- design_artifact: visual artifacts (HTML wireframes,
            -- SVG mockups, markdown component specs, visual rules)
            -- attached to a spec. The dashboard renders them inline.
            -- designer_run: one row per Designer run with the hygiene
            -- verdict + the design_artifact ids it produced. The
            -- dashboard's Design tab shows the timeline.
            --
            -- Same FK-loose pattern as issue_groomer_run: spec_id
            -- references spec.id, but no SQL FK; the
            -- DesignArtifactStore + SpecStore + Designer agent are
            -- the source of truth for the relationship.
            CREATE TABLE IF NOT EXISTS design_artifact (
                id                  TEXT PRIMARY KEY,
                spec_id             TEXT NOT NULL,
                kind                TEXT NOT NULL,    -- 'wireframe' | 'mockup' | 'component-spec' | 'visual-rule'
                title               TEXT NOT NULL,
                body                TEXT NOT NULL,
                body_kind           TEXT NOT NULL,    -- 'html' | 'svg' | 'markdown'
                references_json     TEXT,             -- JSON array of {designArtifactId, why}
                parent_artifact_id  TEXT,
                status              TEXT NOT NULL DEFAULT 'draft',  -- 'draft' | 'approved' | 'superseded'
                author              TEXT NOT NULL,
                created_at          TEXT NOT NULL,
                updated_at          TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_design_artifact_spec
                ON design_artifact(spec_id, status);

            CREATE TABLE IF NOT EXISTS designer_run (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ts                  TEXT NOT NULL,
                spec_id             TEXT NOT NULL,
                trigger_kind        TEXT NOT NULL,    -- 'manual' | 'scheduled'
                status              TEXT NOT NULL,    -- 'started' | 'succeeded' | 'hygiene_failed' | 'llm_failed'
                new_spec_status     TEXT,             -- the spec status the designer set: 'designed' | 'approved' | 'needs_revision'
                design_artifact_ids TEXT,             -- JSON array of design_artifact.id
                hygiene_report      TEXT,             -- JSON: {passed, findings: [{rule, severity, message, fixSuggestion}]}
                error               TEXT,
                duration_ms         INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_designer_run_spec ON designer_run(spec_id, ts);
            CREATE INDEX IF NOT EXISTS ix_designer_run_ts ON designer_run(ts);

            -- v10: art_output + artist_run tables. The
            -- Artist agent runs after Designer and before Groomer.
            -- art_output: produced art assets (3D meshes from
            -- Meshy, PNG textures, MP4 animations, rig files)
            -- attached to a spec. The body column stores a local
            -- relative path under .portHorizon/art-output/; the
            -- dashboard's Art tab renders GLB/PNG inline.
            -- artist_run: one row per Artist run with the meshy
            -- task id + the art_output ids it produced.
            --
            -- Same FK-loose pattern as design_artifact /
            -- designer_run: spec_id references spec.id, but no
            -- SQL FK; ArtOutputStore + SpecStore + Artist agent
            -- are the source of truth.
            CREATE TABLE IF NOT EXISTS art_output (
                id                  TEXT PRIMARY KEY,
                spec_id             TEXT NOT NULL,
                kind                TEXT NOT NULL,    -- 'mesh' | 'texture' | 'animation' | 'rig'
                title               TEXT NOT NULL,
                body                TEXT NOT NULL,    -- relative path under .portHorizon/art-output/
                body_kind           TEXT NOT NULL,    -- 'glb' | 'fbx' | 'obj' | 'png' | 'mp4' | 'usdz'
                references_json     TEXT,             -- JSON array of {designArtifactId, meshyTaskId, why}
                parent_artifact_id  TEXT,
                status              TEXT NOT NULL DEFAULT 'draft',  -- 'draft' | 'approved' | 'superseded'
                author              TEXT NOT NULL,
                created_at          TEXT NOT NULL,
                updated_at          TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_art_output_spec
                ON art_output(spec_id, status);
            CREATE INDEX IF NOT EXISTS ix_art_output_spec_kind
                ON art_output(spec_id, kind);

            CREATE TABLE IF NOT EXISTS artist_run (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ts                  TEXT NOT NULL,
                spec_id             TEXT NOT NULL,
                trigger_kind        TEXT NOT NULL,    -- 'manual' | 'scheduled'
                status              TEXT NOT NULL,    -- 'started' | 'succeeded' | 'meshy_failed' | 'llm_failed'
                new_spec_status     TEXT,             -- the spec status the artist set: 'asset_ready'
                art_output_ids      TEXT,             -- JSON array of art_output.id
                meshy_tasks         TEXT,             -- JSON array of {id, mode, status, artOutputId}
                error               TEXT,
                duration_ms         INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_artist_run_spec ON artist_run(spec_id, ts);
            CREATE INDEX IF NOT EXISTS ix_artist_run_ts ON artist_run(ts);

            -- v11: P4 Stage A — checkpoint-based recovery.
            --
            -- The orchestrator's engineering dispatch workflow is
            -- Claim -> Worktree -> RunAgent -> CommitPushPr
            -- -> EnqueueWatch. On a clean shutdown every executor
            -- commits its side-effects to issue metadata. On a
            -- crash mid-workflow the orchestrator loses track of
            -- which side-effects have already happened.
            --
            -- We add three columns on `issue` and one new table.
            --
            --   dispatch_checkpoint: a string from the
            --     DispatchCheckpoint enum below. Each executor
            --     sets it BEFORE its side-effect so a recoverer
            --     reading the column knows "this side-effect has
            --     happened" (vs. "we got past it").
            --   checkpoint_at: when the checkpoint was set.
            --   recovery_attempts: counter incremented by the
            --     StartupRecovery pass. Used to break recovery
            --     loops (e.g. an issue that keeps crashing the
            --     workflow on commit is failed after N attempts).
            --
            -- The recovery_report table is the audit log. One row
            -- per StartupRecovery pass. The actions_json column
            -- is a JSON array of {issueId, beforeCheckpoint,
            -- afterCheckpoint, action} so the operator can see
            -- exactly what the recoverer did.
            ALTER TABLE issue ADD COLUMN dispatch_checkpoint TEXT;
            ALTER TABLE issue ADD COLUMN checkpoint_at TEXT;
            ALTER TABLE issue ADD COLUMN recovery_attempts INTEGER NOT NULL DEFAULT 0;
            CREATE INDEX IF NOT EXISTS ix_issue_checkpoint
                ON issue(dispatch_checkpoint, status);

            CREATE TABLE IF NOT EXISTS recovery_report (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ts              TEXT NOT NULL,
                spec_id         TEXT,           -- null = full sweep
                issues_scanned  INTEGER NOT NULL,
                issues_replayed INTEGER NOT NULL,
                issues_failed   INTEGER NOT NULL,
                actions_json    TEXT NOT NULL,  -- JSON array of {issueId, beforeCheckpoint, afterCheckpoint, action, error?}
                duration_ms     INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_recovery_report_ts ON recovery_report(ts);

            -- v12: P5 — native SharedContext.
            --
            -- The context_handoff table records which artifact ids
            -- each MAF agent (per task) actually read via
            -- ArtifactReadTool. Used for closed-loop debugging:
            -- "the Artist didn't see the wireframe" → look at this
            -- table to see whether the read happened. The
            -- ContextHandoffStore class has a separate
            -- EnsureCreatedAsync for tests; the production schema
            -- bootstrap covers operator runtime.
            --
            --   from_role / to_role: empty if the producer or
            --     consumer doesn't have a role name (e.g. the spec
            --     body is the source; an agent's first read might
            --     pre-date the role binding). The columns are not
            --     null so simple "WHERE to_role = 'designer'" works.
            --   artifact_kind: 'design' | 'spec' | 'art'. Redundant
            --     with artifact_id's prefix; stored for index
            --     efficiency on dashboards.
            --   consumed: 1 if the LLM actually read the body; 0
            --     if the lookup was a miss (we record misses too
            --     so we can detect "agent asked for an artifact
            --     that doesn't exist" — usually a stale id).
            CREATE TABLE IF NOT EXISTS context_handoff (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                ts              TEXT NOT NULL,
                task_id         TEXT NOT NULL,
                from_role       TEXT NOT NULL,
                to_role         TEXT NOT NULL,
                artifact_id     TEXT NOT NULL,
                artifact_kind   TEXT NOT NULL,
                consumed        INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_context_handoff_task
                ON context_handoff(task_id, ts);
            CREATE INDEX IF NOT EXISTS ix_context_handoff_artifact
                ON context_handoff(artifact_id, ts);

            -- v13: P5.5 — auto-extracted project memory.
            --
            -- Records each extraction run so the operator can
            -- audit what the LLM pulled from a model response
            -- and which keys actually landed in the memory
            -- table. The memory table itself is the store of
            -- record; this table is the audit log.
            --
            --   source_chars: how many chars the model's response
            --     contained when we asked the LLM to extract
            --     from it. Useful for "is the model talking at
            --     all?" sanity checks.
            --   persisted_keys_json: JSON array of keys we wrote
            --     to the memory table (post-sanitization). Empty
            --     if extraction returned 0 items or errored.
            --   error: null on success, exception summary on
            --     failure (advisory; the engineering dispatch
            --     continues regardless).
            CREATE TABLE IF NOT EXISTS memory_extraction (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                ts                  TEXT NOT NULL,
                task_id             TEXT NOT NULL,
                source_chars        INTEGER NOT NULL,
                extracted_count     INTEGER NOT NULL,
                persisted_keys_json TEXT NOT NULL,
                error               TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_memory_extraction_task
                ON memory_extraction(task_id, ts);

            -- v14: P6 Stage 8 — sprint proposal audit log.
            --
            -- Each call to /api/sprints/propose-next writes a row
            -- before returning the score breakdown. The dashboard's
            -- /api/sprints/{id}/scoring-audit reads these rows so
            -- the operator can audit what the DeterministicScorer
            -- recommended and what weights it used.
            --
            --   theme: optional sprint theme string (null = auto)
            --   goal: optional goal line (null = auto)
            --   weights_json: JSON map of the scorer weights used
            --     (priority/theme/age/downstream). Captured at the
            --     same time so future weight tweaks don't silently
            --     change past audit rows.
            --   candidates_json: JSON array of {taskId, title, score,
            --     breakdown: string[]} for every scored task.
            --   selected_task_ids_json: JSON array of taskIds that
            --     were proposed (top 7 by default).
            CREATE TABLE IF NOT EXISTS sprint_proposal_audit (
                id                      INTEGER PRIMARY KEY AUTOINCREMENT,
                ts                      TEXT NOT NULL,
                theme                   TEXT,
                goal                    TEXT,
                weights_json            TEXT NOT NULL,
                candidates_json         TEXT NOT NULL,
                selected_task_ids_json  TEXT NOT NULL,
                committed_sprint_id     TEXT,
                committed_by            TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_sprint_proposal_audit_ts
                ON sprint_proposal_audit(ts DESC);

            -- v15: P8 — deployment candidates. One row per operator
            -- "deploy this commit" request against a project's
            -- configured deployment pipeline (Configuration/DeploymentOptions.cs).
            -- DeploymentBuildRunner transitions Pending -> BuildRunning
            -- -> BuildPassed/BuildFailed; the /api/deployments approve
            -- endpoint transitions BuildPassed (or Pending, when the
            -- project skips the build gate) -> Approved -> Deploying
            -- -> Deployed/DeployFailed via the project's configured
            -- IDeploymentExecutor. Rejected is a terminal dead-end.
            --
            --   project_id: which registry entry this targets.
            --   commit_sha: the commit the operator chose to deploy
            --     (NOT necessarily the tip of main -- P8 intentionally
            --     decouples "merged" from "deployed" so a burst of
            --     merges doesn't force a redeploy on every push).
            --   commit_summary: `git log -1 --oneline` snapshot at
            --     request time, purely for display.
            --   build_log / deploy_log: captured stdout+stderr from
            --     the build check and the executor, respectively.
            CREATE TABLE IF NOT EXISTS deployment (
                id              TEXT PRIMARY KEY,
                project_id      TEXT NOT NULL,
                commit_sha      TEXT NOT NULL,
                commit_summary  TEXT,
                status          TEXT NOT NULL,
                requested_at    TEXT NOT NULL,
                requested_by    TEXT,
                build_log       TEXT,
                approved_at     TEXT,
                approved_by     TEXT,
                deployed_at     TEXT,
                deploy_log      TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_deployment_project
                ON deployment(project_id, requested_at DESC);

            INSERT OR IGNORE INTO schema_version(version, applied_at)
            VALUES ($version, $now);
            """;
        cmd.Parameters.AddWithValue("$version", CurrentSchemaVersion);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString(DateFormat));
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            // Diagnostic: locate the failure by character offset.
            var msg = ex.Message;
            var match = System.Text.RegularExpressions.Regex.Match(msg, @"at offset (\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var off))
            {
                var start = Math.Max(0, off - 200);
                var len = Math.Min(400, cmd.CommandText.Length - start);
                Console.Error.WriteLine($"[IssueStore] SQLite error near offset {off}: {cmd.CommandText.Substring(start, len)}");
            }
            else
            {
                Console.Error.WriteLine($"[IssueStore] InitializeSchema failed: {ex.GetType().Name}: {ex.Message}");
            }
            throw;
        }

        // v16 (post-init): the unconditional ALTER TABLE / UPDATE that
        // would belong inline above don't run when the new column name
        // is already in place (fresh DBs) or when no rows have the
        // legacy 'kilo' assignee. Gate each step on the legacy shape.
        ApplyLegacyKiloRenames(conn);

        // v19 (post-init): project.roles_json for DB-persisted role
        // caps. CREATE TABLE IF NOT EXISTS covers fresh DBs; existing
        // DBs need the guarded ALTER.
        ApplyV19ProjectRoles(conn);

        // v21 (post-init): agent_run.last_activity_at — the heartbeat
        // that makes "is this run alive or hung on the provider?"
        // answerable from the dashboard.
        ApplyV21AgentRunActivity(conn);

        // v22 (post-init): skill.role — role-name scoping for the
        // skill catalog (the legacy agent_id FK resolves through the
        // legacy agent table, which is empty in practice). NULL role
        // = global skill.
        ApplyV22SkillRole(conn);

        // v23 (post-init): skill.roles (JSON array) replaces the
        // single-valued role — skills are MANY-TO-MANY (one skill can
        // be given to any set of roles; an empty set means global).
        ApplyV23SkillRoles(conn);

        // v24 (post-init): skill.project_id + skill.source — per-project
        // skills. project_id NULL = global (every project's runs see it);
        // source 'forge' = UI-owned (dashboard edits win), 'repo' =
        // imported from a project's .kilo/skills (repo is the source of
        // truth; the dashboard is read-only for these).
        ApplyV24SkillProjectScope(conn);

        // v25 (post-init): agent_run.phase + agent_run.resumed_session —
        // the run's live phase label (plan gate / implementing /
        // verifying n/3 / reviewing) and the warm-session marker for
        // pause/resume runs.
        ApplyV25AgentRunPhase(conn);

        // Stamp AFTER migrations, as its own statement: the batch's
        // INSERT OR IGNORE does not reliably take effect on existing
        // DBs (observed live 2026-07-24: forge DB stamped v19 while
        // v21 columns were present and in use). Idempotent.
        using (var stamp = conn.CreateCommand())
        {
            stamp.CommandText = "INSERT OR IGNORE INTO schema_version(version, applied_at) VALUES ($version, $now)";
            stamp.Parameters.AddWithValue("$version", CurrentSchemaVersion);
            stamp.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString(DateFormat));
            stamp.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// SQL Server schema: fresh-create at <see cref="CurrentSchemaVersion"/>
    /// in one shot. The SQLite v1→v24 migration chain is NOT ported — Azure
    /// databases start at the final shape (roles_json, last_activity_at,
    /// skill.roles + skill.project_id/source, agent.agent_name all present
    /// from birth). Tables are
    /// created inside the factory's per-project schema qualifier.
    /// Integer column types match reader accessors exactly:
    /// GetInt32-read columns are INT, identity/GetInt64 columns BIGINT.
    /// </summary>
    private void InitializeSchemaSqlServer()
    {
        var d = (SqlServerDialect)_db.Dialect;
        var q = d.Qualifier;
        using var conn = _db.Open();
        using (var ensure = conn.CreateCommand())
        {
            ensure.CommandText = $"IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = '{q}') EXEC('CREATE SCHEMA [{q}]');";
            ensure.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        // Profile split (SQL Server): the Core schema gets only
        // registry/global tables (project, secret, agent, skill) — a
        // registry anchor never carries agent_run & co; a Workload
        // project schema gets only the 26 workload tables. schema_version
        // exists in both (per-schema version stamp, read by --check).
        if (_db.Profile == ForgeSchemaProfile.Core)
        {
            cmd.CommandText = $$"""
            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'schema_version')
            CREATE TABLE {{d.Table("schema_version")}} (
                version    BIGINT NOT NULL PRIMARY KEY,
                applied_at NVARCHAR(64) NOT NULL
            );

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'agent')
            CREATE TABLE {{d.Table("agent")}} (
                id           NVARCHAR(128) NOT NULL PRIMARY KEY,
                agent_name   NVARCHAR(128) NOT NULL UNIQUE,
                display_name NVARCHAR(MAX) NOT NULL,
                scope        NVARCHAR(MAX) NOT NULL DEFAULT '',
                description  NVARCHAR(MAX) NULL,
                enabled      INT           NOT NULL DEFAULT 1,
                config_json  NVARCHAR(MAX) NOT NULL DEFAULT '{}',
                created_at   NVARCHAR(64)  NOT NULL,
                updated_at   NVARCHAR(64)  NOT NULL
            );

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'project')
            CREATE TABLE {{d.Table("project")}} (
                id              NVARCHAR(128) NOT NULL PRIMARY KEY,
                name            NVARCHAR(MAX) NOT NULL,
                repo_url        NVARCHAR(MAX) NOT NULL,
                default_branch  NVARCHAR(128) NOT NULL DEFAULT 'main',
                local_path      NVARCHAR(MAX) NULL,
                created_at      NVARCHAR(64)  NOT NULL,
                updated_at      NVARCHAR(64)  NOT NULL,
                last_synced_at  NVARCHAR(64)  NULL,
                last_sync_error NVARCHAR(MAX) NULL,
                roles_json      NVARCHAR(MAX) NOT NULL DEFAULT '{}'
            );

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'secret')
            CREATE TABLE {{d.Table("secret")}} (
                id          NVARCHAR(128) NOT NULL PRIMARY KEY,
                project_id  NVARCHAR(128) NOT NULL REFERENCES {{d.Table("project")}}(id) ON DELETE CASCADE,
                kind        NVARCHAR(128) NOT NULL,
                ciphertext  VARBINARY(MAX) NOT NULL,
                created_at  NVARCHAR(64)  NOT NULL,
                updated_at  NVARCHAR(64)  NOT NULL,
                UNIQUE (project_id, kind)
            );

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'skill')
            CREATE TABLE {{d.Table("skill")}} (
                id           NVARCHAR(128) NOT NULL PRIMARY KEY,
                name         NVARCHAR(128) NOT NULL,
                description  NVARCHAR(MAX) NULL,
                body         NVARCHAR(MAX) NOT NULL,
                agent_id     NVARCHAR(128) NULL REFERENCES {{d.Table("agent")}}(id) ON DELETE CASCADE,
                enabled      INT           NOT NULL DEFAULT 1,
                created_at   NVARCHAR(64)  NOT NULL,
                updated_at   NVARCHAR(64)  NOT NULL,
                roles        NVARCHAR(MAX) NULL,
                project_id   NVARCHAR(128) NULL,
                source       NVARCHAR(16)  NOT NULL DEFAULT 'forge',
                UNIQUE (name, project_id)
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_skill_agent' AND object_id = OBJECT_ID('{{d.Table("skill")}}'))
                CREATE INDEX ix_skill_agent ON {{d.Table("skill")}}(agent_id);
            """;
            cmd.ExecuteNonQuery();
        }
        else
        {
        cmd.CommandText = $$"""
            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'issue')
            CREATE TABLE {{d.Table("issue")}} (
                id                NVARCHAR(128) NOT NULL PRIMARY KEY,
                short_id          NVARCHAR(128) NOT NULL,
                type              NVARCHAR(128) NOT NULL,
                title             NVARCHAR(MAX) NOT NULL,
                description       NVARCHAR(MAX) NULL,
                status            NVARCHAR(64)  NOT NULL,
                priority          INT           NOT NULL DEFAULT 2,
                assignee          NVARCHAR(128) NULL,
                created_at        NVARCHAR(64)  NOT NULL,
                updated_at        NVARCHAR(64)  NOT NULL,
                closed_at         NVARCHAR(64)  NULL,
                metadata_json     NVARCHAR(MAX) NOT NULL DEFAULT '{}',
                parent_issue_id   NVARCHAR(128) NULL,
                dispatch_checkpoint NVARCHAR(64) NULL,
                checkpoint_at     NVARCHAR(64)  NULL,
                recovery_attempts INT           NOT NULL DEFAULT 0
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_issue_status' AND object_id = OBJECT_ID('{{d.Table("issue")}}'))
                CREATE INDEX ix_issue_status     ON {{d.Table("issue")}}(status);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_issue_assignee' AND object_id = OBJECT_ID('{{d.Table("issue")}}'))
                CREATE INDEX ix_issue_assignee   ON {{d.Table("issue")}}(assignee);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_issue_updated_at' AND object_id = OBJECT_ID('{{d.Table("issue")}}'))
                CREATE INDEX ix_issue_updated_at ON {{d.Table("issue")}}(updated_at);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_issue_type_short' AND object_id = OBJECT_ID('{{d.Table("issue")}}'))
                CREATE INDEX ix_issue_type_short ON {{d.Table("issue")}}(type, short_id);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_issue_parent' AND object_id = OBJECT_ID('{{d.Table("issue")}}'))
                CREATE INDEX ix_issue_parent     ON {{d.Table("issue")}}(parent_issue_id);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_issue_checkpoint' AND object_id = OBJECT_ID('{{d.Table("issue")}}'))
                CREATE INDEX ix_issue_checkpoint ON {{d.Table("issue")}}(dispatch_checkpoint, status);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'issue_event')
            CREATE TABLE {{d.Table("issue_event")}} (
                id          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                issue_id    NVARCHAR(128) NOT NULL REFERENCES {{d.Table("issue")}}(id) ON DELETE CASCADE,
                ts          NVARCHAR(64)  NOT NULL,
                actor       NVARCHAR(128) NULL,
                kind        NVARCHAR(128) NOT NULL,
                detail      NVARCHAR(MAX) NULL
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_issue_event_issue' AND object_id = OBJECT_ID('{{d.Table("issue_event")}}'))
                CREATE INDEX ix_issue_event_issue ON {{d.Table("issue_event")}}(issue_id, ts);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'issue_seq')
            CREATE TABLE {{d.Table("issue_seq")}} (
                type       NVARCHAR(128) NOT NULL PRIMARY KEY,
                next_short BIGINT NOT NULL DEFAULT 1
            );

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'schema_version')
            CREATE TABLE {{d.Table("schema_version")}} (
                version    BIGINT NOT NULL PRIMARY KEY,
                applied_at NVARCHAR(64) NOT NULL
            );

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'agent_run')
            CREATE TABLE {{d.Table("agent_run")}} (
                id               NVARCHAR(128) NOT NULL PRIMARY KEY,
                task_id          NVARCHAR(128) NULL,
                role             NVARCHAR(128) NOT NULL,
                model            NVARCHAR(128) NULL,
                status           NVARCHAR(64)  NOT NULL,
                started_at       NVARCHAR(64)  NOT NULL,
                finished_at      NVARCHAR(64)  NULL,
                duration_ms      BIGINT        NULL,
                message_count    INT           NULL,
                tool_call_count  INT           NULL,
                text_chars       INT           NULL,
                error            NVARCHAR(MAX) NULL,
                transcript_json  NVARCHAR(MAX) NULL,
                last_activity_at NVARCHAR(64)  NULL,
                phase            NVARCHAR(64)  NULL,
                resumed_session  INT           NULL
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_agent_run_started' AND object_id = OBJECT_ID('{{d.Table("agent_run")}}'))
                CREATE INDEX idx_agent_run_started ON {{d.Table("agent_run")}}(started_at DESC);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_agent_run_task' AND object_id = OBJECT_ID('{{d.Table("agent_run")}}'))
                CREATE INDEX idx_agent_run_task ON {{d.Table("agent_run")}}(task_id);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'sprint')
            CREATE TABLE {{d.Table("sprint")}} (
                id           NVARCHAR(128) NOT NULL PRIMARY KEY,
                name         NVARCHAR(MAX) NOT NULL,
                goal         NVARCHAR(MAX) NOT NULL,
                start_date   NVARCHAR(64)  NOT NULL,
                end_date     NVARCHAR(64)  NOT NULL,
                status       NVARCHAR(64)  NOT NULL DEFAULT 'active',
                created_at   NVARCHAR(64)  NOT NULL,
                updated_at   NVARCHAR(64)  NOT NULL
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_sprint_status' AND object_id = OBJECT_ID('{{d.Table("sprint")}}'))
                CREATE INDEX ix_sprint_status ON {{d.Table("sprint")}}(status);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'uq_sprint_active' AND object_id = OBJECT_ID('{{d.Table("sprint")}}'))
                CREATE UNIQUE INDEX uq_sprint_active ON {{d.Table("sprint")}}(status) WHERE status = 'active';

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'sprint_issue')
            CREATE TABLE {{d.Table("sprint_issue")}} (
                sprint_id   NVARCHAR(128) NOT NULL REFERENCES {{d.Table("sprint")}}(id) ON DELETE CASCADE,
                issue_id    NVARCHAR(128) NOT NULL REFERENCES {{d.Table("issue")}}(id) ON DELETE NO ACTION,
                added_at    NVARCHAR(64)  NOT NULL,
                PRIMARY KEY (sprint_id, issue_id)
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_sprint_issue_sprint' AND object_id = OBJECT_ID('{{d.Table("sprint_issue")}}'))
                CREATE INDEX ix_sprint_issue_sprint ON {{d.Table("sprint_issue")}}(sprint_id);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'intake_session')
            CREATE TABLE {{d.Table("intake_session")}} (
                id         NVARCHAR(128) NOT NULL PRIMARY KEY,
                project_id NVARCHAR(128) NOT NULL,
                title      NVARCHAR(MAX) NOT NULL,
                created_at NVARCHAR(64)  NOT NULL,
                updated_at NVARCHAR(64)  NOT NULL
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_intake_session_project' AND object_id = OBJECT_ID('{{d.Table("intake_session")}}'))
                CREATE INDEX ix_intake_session_project ON {{d.Table("intake_session")}}(project_id, updated_at DESC);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'intake_message')
            CREATE TABLE {{d.Table("intake_message")}} (
                id                   BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                session_id           NVARCHAR(128) NOT NULL REFERENCES {{d.Table("intake_session")}}(id) ON DELETE CASCADE,
                role                 NVARCHAR(128) NOT NULL,
                content              NVARCHAR(MAX) NOT NULL,
                ts                   NVARCHAR(64)  NOT NULL,
                proposed_epic_id     NVARCHAR(128) NULL,
                proposed_epic_title  NVARCHAR(MAX) NULL
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_intake_message_session' AND object_id = OBJECT_ID('{{d.Table("intake_message")}}'))
                CREATE INDEX ix_intake_message_session ON {{d.Table("intake_message")}}(session_id, id);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'spec')
            CREATE TABLE {{d.Table("spec")}} (
                id              NVARCHAR(128) NOT NULL PRIMARY KEY,
                project_id      NVARCHAR(128) NOT NULL,
                title           NVARCHAR(MAX) NOT NULL,
                status          NVARCHAR(64)  NOT NULL,
                parent_issue_id NVARCHAR(128) NULL,
                parent_spec_id  NVARCHAR(128) NULL,
                current_version INT           NOT NULL DEFAULT 1,
                created_at      NVARCHAR(64)  NOT NULL,
                updated_at      NVARCHAR(64)  NOT NULL,
                extracted_at    NVARCHAR(64)  NULL
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_spec_project' AND object_id = OBJECT_ID('{{d.Table("spec")}}'))
                CREATE INDEX ix_spec_project ON {{d.Table("spec")}}(project_id, updated_at DESC);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_spec_status' AND object_id = OBJECT_ID('{{d.Table("spec")}}'))
                CREATE INDEX ix_spec_status ON {{d.Table("spec")}}(status);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'spec_version')
            CREATE TABLE {{d.Table("spec_version")}} (
                spec_id     NVARCHAR(128) NOT NULL REFERENCES {{d.Table("spec")}}(id) ON DELETE CASCADE,
                version     INT           NOT NULL,
                body        NVARCHAR(MAX) NOT NULL,
                author      NVARCHAR(128) NULL,
                created_at  NVARCHAR(64)  NOT NULL,
                PRIMARY KEY (spec_id, version)
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_spec_version_spec' AND object_id = OBJECT_ID('{{d.Table("spec_version")}}'))
                CREATE INDEX ix_spec_version_spec ON {{d.Table("spec_version")}}(spec_id, version DESC);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'spec_diagram')
            CREATE TABLE {{d.Table("spec_diagram")}} (
                spec_id  NVARCHAR(128) NOT NULL REFERENCES {{d.Table("spec")}}(id) ON DELETE CASCADE,
                ordinal  INT           NOT NULL,
                kind     NVARCHAR(128) NOT NULL,
                source   NVARCHAR(MAX) NOT NULL,
                title    NVARCHAR(MAX) NULL,
                PRIMARY KEY (spec_id, ordinal)
            );

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'spec_touches')
            CREATE TABLE {{d.Table("spec_touches")}} (
                spec_id     NVARCHAR(128) NOT NULL REFERENCES {{d.Table("spec")}}(id) ON DELETE CASCADE,
                module_id   NVARCHAR(128) NOT NULL,
                source      NVARCHAR(128) NOT NULL,
                rationale   NVARCHAR(MAX) NULL,
                created_at  NVARCHAR(64)  NOT NULL,
                PRIMARY KEY (spec_id, module_id, source)
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_spec_touches_module' AND object_id = OBJECT_ID('{{d.Table("spec_touches")}}'))
                CREATE INDEX ix_spec_touches_module ON {{d.Table("spec_touches")}}(module_id);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'spec_dep')
            CREATE TABLE {{d.Table("spec_dep")}} (
                from_spec_id  NVARCHAR(128) NOT NULL REFERENCES {{d.Table("spec")}}(id) ON DELETE CASCADE,
                to_spec_id    NVARCHAR(128) NOT NULL,
                kind          NVARCHAR(128) NOT NULL,
                rationale     NVARCHAR(MAX) NULL,
                source        NVARCHAR(128) NOT NULL,
                created_at    NVARCHAR(64)  NOT NULL,
                PRIMARY KEY (from_spec_id, to_spec_id, kind)
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_spec_dep_to' AND object_id = OBJECT_ID('{{d.Table("spec_dep")}}'))
                CREATE INDEX ix_spec_dep_to ON {{d.Table("spec_dep")}}(to_spec_id);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'codebase_graph_cache')
            CREATE TABLE {{d.Table("codebase_graph_cache")}} (
                repo_sha    NVARCHAR(128) NOT NULL PRIMARY KEY,
                built_at    NVARCHAR(64)  NOT NULL,
                file_count  INT           NOT NULL,
                edge_count  INT           NOT NULL
            );

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'issue_dep')
            CREATE TABLE {{d.Table("issue_dep")}} (
                blocker_id  NVARCHAR(128) NOT NULL REFERENCES {{d.Table("issue")}}(id) ON DELETE CASCADE,
                blocked_id  NVARCHAR(128) NOT NULL REFERENCES {{d.Table("issue")}}(id) ON DELETE NO ACTION,
                kind        NVARCHAR(64)  NOT NULL DEFAULT 'blocks',
                created_at  NVARCHAR(64)  NOT NULL,
                PRIMARY KEY (blocker_id, blocked_id, kind)
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_issue_dep_blocked' AND object_id = OBJECT_ID('{{d.Table("issue_dep")}}'))
                CREATE INDEX ix_issue_dep_blocked ON {{d.Table("issue_dep")}}(blocked_id, kind);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_issue_dep_blocker' AND object_id = OBJECT_ID('{{d.Table("issue_dep")}}'))
                CREATE INDEX ix_issue_dep_blocker ON {{d.Table("issue_dep")}}(blocker_id, kind);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'memory')
            CREATE TABLE {{d.Table("memory")}} (
                id          BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                ts          NVARCHAR(64)  NOT NULL,
                [key]       NVARCHAR(128) NOT NULL UNIQUE,
                body        NVARCHAR(MAX) NOT NULL,
                ttl_days    INT           NULL
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_memory_key' AND object_id = OBJECT_ID('{{d.Table("memory")}}'))
                CREATE INDEX ix_memory_key ON {{d.Table("memory")}}([key]);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'issue_groomer_run')
            CREATE TABLE {{d.Table("issue_groomer_run")}} (
                id                  BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                ts                  NVARCHAR(64)  NOT NULL,
                spec_id             NVARCHAR(128) NOT NULL,
                trigger_kind        NVARCHAR(64)  NOT NULL,
                status              NVARCHAR(64)  NOT NULL,
                stories_produced    INT           NOT NULL DEFAULT 0,
                tasks_produced      INT           NOT NULL DEFAULT 0,
                error               NVARCHAR(MAX) NULL,
                duration_ms         INT           NOT NULL DEFAULT 0
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_issue_groomer_run_spec' AND object_id = OBJECT_ID('{{d.Table("issue_groomer_run")}}'))
                CREATE INDEX ix_issue_groomer_run_spec ON {{d.Table("issue_groomer_run")}}(spec_id, ts);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_issue_groomer_run_ts' AND object_id = OBJECT_ID('{{d.Table("issue_groomer_run")}}'))
                CREATE INDEX ix_issue_groomer_run_ts ON {{d.Table("issue_groomer_run")}}(ts);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'design_artifact')
            CREATE TABLE {{d.Table("design_artifact")}} (
                id                  NVARCHAR(128) NOT NULL PRIMARY KEY,
                spec_id             NVARCHAR(128) NOT NULL,
                kind                NVARCHAR(64)  NOT NULL,
                title               NVARCHAR(MAX) NOT NULL,
                body                NVARCHAR(MAX) NOT NULL,
                body_kind           NVARCHAR(64)  NOT NULL,
                references_json     NVARCHAR(MAX) NULL,
                parent_artifact_id  NVARCHAR(128) NULL,
                status              NVARCHAR(64)  NOT NULL DEFAULT 'draft',
                author              NVARCHAR(128) NOT NULL,
                created_at          NVARCHAR(64)  NOT NULL,
                updated_at          NVARCHAR(64)  NOT NULL
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_design_artifact_spec' AND object_id = OBJECT_ID('{{d.Table("design_artifact")}}'))
                CREATE INDEX ix_design_artifact_spec ON {{d.Table("design_artifact")}}(spec_id, status);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'designer_run')
            CREATE TABLE {{d.Table("designer_run")}} (
                id                  BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                ts                  NVARCHAR(64)  NOT NULL,
                spec_id             NVARCHAR(128) NOT NULL,
                trigger_kind        NVARCHAR(64)  NOT NULL,
                status              NVARCHAR(64)  NOT NULL,
                new_spec_status     NVARCHAR(64)  NULL,
                design_artifact_ids NVARCHAR(MAX) NULL,
                hygiene_report      NVARCHAR(MAX) NULL,
                error               NVARCHAR(MAX) NULL,
                duration_ms         INT           NOT NULL DEFAULT 0
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_designer_run_spec' AND object_id = OBJECT_ID('{{d.Table("designer_run")}}'))
                CREATE INDEX ix_designer_run_spec ON {{d.Table("designer_run")}}(spec_id, ts);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_designer_run_ts' AND object_id = OBJECT_ID('{{d.Table("designer_run")}}'))
                CREATE INDEX ix_designer_run_ts ON {{d.Table("designer_run")}}(ts);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'art_output')
            CREATE TABLE {{d.Table("art_output")}} (
                id                  NVARCHAR(128) NOT NULL PRIMARY KEY,
                spec_id             NVARCHAR(128) NOT NULL,
                kind                NVARCHAR(64)  NOT NULL,
                title               NVARCHAR(MAX) NOT NULL,
                body                NVARCHAR(MAX) NOT NULL,
                body_kind           NVARCHAR(64)  NOT NULL,
                references_json     NVARCHAR(MAX) NULL,
                parent_artifact_id  NVARCHAR(128) NULL,
                status              NVARCHAR(64)  NOT NULL DEFAULT 'draft',
                author              NVARCHAR(128) NOT NULL,
                created_at          NVARCHAR(64)  NOT NULL,
                updated_at          NVARCHAR(64)  NOT NULL
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_art_output_spec' AND object_id = OBJECT_ID('{{d.Table("art_output")}}'))
                CREATE INDEX ix_art_output_spec ON {{d.Table("art_output")}}(spec_id, status);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_art_output_spec_kind' AND object_id = OBJECT_ID('{{d.Table("art_output")}}'))
                CREATE INDEX ix_art_output_spec_kind ON {{d.Table("art_output")}}(spec_id, kind);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'artist_run')
            CREATE TABLE {{d.Table("artist_run")}} (
                id                  BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                ts                  NVARCHAR(64)  NOT NULL,
                spec_id             NVARCHAR(128) NOT NULL,
                trigger_kind        NVARCHAR(64)  NOT NULL,
                status              NVARCHAR(64)  NOT NULL,
                new_spec_status     NVARCHAR(64)  NULL,
                art_output_ids      NVARCHAR(MAX) NULL,
                meshy_tasks         NVARCHAR(MAX) NULL,
                error               NVARCHAR(MAX) NULL,
                duration_ms         INT           NOT NULL DEFAULT 0
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_artist_run_spec' AND object_id = OBJECT_ID('{{d.Table("artist_run")}}'))
                CREATE INDEX ix_artist_run_spec ON {{d.Table("artist_run")}}(spec_id, ts);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_artist_run_ts' AND object_id = OBJECT_ID('{{d.Table("artist_run")}}'))
                CREATE INDEX ix_artist_run_ts ON {{d.Table("artist_run")}}(ts);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'recovery_report')
            CREATE TABLE {{d.Table("recovery_report")}} (
                id              BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                ts              NVARCHAR(64)  NOT NULL,
                spec_id         NVARCHAR(128) NULL,
                issues_scanned  INT           NOT NULL,
                issues_replayed INT           NOT NULL,
                issues_failed   INT           NOT NULL,
                actions_json    NVARCHAR(MAX) NOT NULL,
                duration_ms     INT           NOT NULL DEFAULT 0
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_recovery_report_ts' AND object_id = OBJECT_ID('{{d.Table("recovery_report")}}'))
                CREATE INDEX ix_recovery_report_ts ON {{d.Table("recovery_report")}}(ts);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'context_handoff')
            CREATE TABLE {{d.Table("context_handoff")}} (
                id              BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                ts              NVARCHAR(64)  NOT NULL,
                task_id         NVARCHAR(128) NOT NULL,
                from_role       NVARCHAR(128) NOT NULL,
                to_role         NVARCHAR(128) NOT NULL,
                artifact_id     NVARCHAR(128) NOT NULL,
                artifact_kind   NVARCHAR(64)  NOT NULL,
                consumed        INT           NOT NULL DEFAULT 0
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_context_handoff_task' AND object_id = OBJECT_ID('{{d.Table("context_handoff")}}'))
                CREATE INDEX ix_context_handoff_task ON {{d.Table("context_handoff")}}(task_id, ts);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_context_handoff_artifact' AND object_id = OBJECT_ID('{{d.Table("context_handoff")}}'))
                CREATE INDEX ix_context_handoff_artifact ON {{d.Table("context_handoff")}}(artifact_id, ts);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'memory_extraction')
            CREATE TABLE {{d.Table("memory_extraction")}} (
                id                  BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                ts                  NVARCHAR(64)  NOT NULL,
                task_id             NVARCHAR(128) NOT NULL,
                source_chars        INT           NOT NULL,
                extracted_count     INT           NOT NULL,
                persisted_keys_json NVARCHAR(MAX) NOT NULL,
                error               NVARCHAR(MAX) NULL
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_memory_extraction_task' AND object_id = OBJECT_ID('{{d.Table("memory_extraction")}}'))
                CREATE INDEX ix_memory_extraction_task ON {{d.Table("memory_extraction")}}(task_id, ts);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'sprint_proposal_audit')
            CREATE TABLE {{d.Table("sprint_proposal_audit")}} (
                id                      BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                ts                      NVARCHAR(64)  NOT NULL,
                theme                   NVARCHAR(MAX) NULL,
                goal                    NVARCHAR(MAX) NULL,
                weights_json            NVARCHAR(MAX) NOT NULL,
                candidates_json         NVARCHAR(MAX) NOT NULL,
                selected_task_ids_json  NVARCHAR(MAX) NOT NULL,
                committed_sprint_id     NVARCHAR(128) NULL,
                committed_by            NVARCHAR(128) NULL
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_sprint_proposal_audit_ts' AND object_id = OBJECT_ID('{{d.Table("sprint_proposal_audit")}}'))
                CREATE INDEX ix_sprint_proposal_audit_ts ON {{d.Table("sprint_proposal_audit")}}(ts DESC);

            IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{{q}}' AND t.name = 'deployment')
            CREATE TABLE {{d.Table("deployment")}} (
                id              NVARCHAR(128) NOT NULL PRIMARY KEY,
                project_id      NVARCHAR(128) NOT NULL,
                commit_sha      NVARCHAR(128) NOT NULL,
                commit_summary  NVARCHAR(MAX) NULL,
                status          NVARCHAR(64)  NOT NULL,
                requested_at    NVARCHAR(64)  NOT NULL,
                requested_by    NVARCHAR(128) NULL,
                build_log       NVARCHAR(MAX) NULL,
                approved_at     NVARCHAR(64)  NULL,
                approved_by     NVARCHAR(128) NULL,
                deployed_at     NVARCHAR(64)  NULL,
                deploy_log      NVARCHAR(MAX) NULL
            );
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'ix_deployment_project' AND object_id = OBJECT_ID('{{d.Table("deployment")}}'))
                CREATE INDEX ix_deployment_project ON {{d.Table("deployment")}}(project_id, requested_at DESC);
            """;
            cmd.ExecuteNonQuery();
        }

        // Ordered migrations for EXISTING databases (a schema just
        // fresh-created above is born at the current shape — the
        // migrations' guards make them no-ops there). Each applied
        // step is stamped; schema_version keeps full history and
        // --check reads MAX(version).
        var applied = 0;
        using (var ver = conn.CreateCommand())
        {
            ver.CommandText = $"SELECT COALESCE(MAX(version), 0) FROM {d.Table("schema_version")}";
            applied = Convert.ToInt32(ver.ExecuteScalar());
        }
        foreach (var migration in SqlServerMigrations.All
            .Where(m => m.Version > applied)
            .OrderBy(m => m.Version))
        {
            migration.Up(conn, d, _db.Profile);
            StampVersion(conn, d, migration.Version);
        }
        StampVersion(conn, d, SqlServerMigrations.ExpectedVersion);
    }

    private static void StampVersion(DbConnection conn, SqlServerDialect d, int version)
    {
        using var stamp = conn.CreateCommand();
        stamp.CommandText = $"""
            IF NOT EXISTS (SELECT 1 FROM {d.Table("schema_version")} WHERE version = @version)
                INSERT INTO {d.Table("schema_version")}(version, applied_at) VALUES (@version, @now);
            """;
        stamp.AddParam("@version", version);
        stamp.AddParam("@now", DateTime.UtcNow.ToString(DateFormat));
        stamp.ExecuteNonQuery();
    }

    private void ApplyV21AgentRunActivity(SqliteConnection conn)
    {
        using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM pragma_table_info('agent_run') WHERE name = 'last_activity_at' LIMIT 1";
        if (probe.ExecuteScalar() is not null) return;
        using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE agent_run ADD COLUMN last_activity_at TEXT";
        alter.ExecuteNonQuery();
    }

    private void ApplyV22SkillRole(SqliteConnection conn)
    {
        using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM pragma_table_info('skill') WHERE name = 'role' LIMIT 1";
        if (probe.ExecuteScalar() is not null) return;
        using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE skill ADD COLUMN role TEXT";
        alter.ExecuteNonQuery();
    }

    private void ApplyV23SkillRoles(SqliteConnection conn)
    {
        using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM pragma_table_info('skill') WHERE name = 'roles' LIMIT 1";
        if (probe.ExecuteScalar() is not null) return;

        // 1. Add the JSON-array column; migrate the single-valued role.
        using (var alter = conn.CreateCommand())
        {
            alter.CommandText = """
                ALTER TABLE skill ADD COLUMN roles TEXT;
                UPDATE skill SET roles = json_array(role) WHERE role IS NOT NULL;
                """;
            alter.ExecuteNonQuery();
        }
        // 2. Collapse the duplicates the single-role model forced
        //    (e.g. forge-completion-contract existed once per role):
        //    merge every same-name group into one row whose roles is
        //    the union, then delete the extras (keep the oldest row).
        using (var merge = conn.CreateCommand())
        {
            merge.CommandText = """
                UPDATE skill SET roles = (
                    SELECT json_group_array(value)
                    FROM (SELECT DISTINCT je.value AS value
                          FROM skill s2, json_each(s2.roles) je
                          WHERE s2.name = skill.name))
                WHERE EXISTS (SELECT 1 FROM skill s3 WHERE s3.name = skill.name AND s3.id <> skill.id);
                DELETE FROM skill WHERE rowid NOT IN (SELECT MIN(rowid) FROM skill GROUP BY name);
                """;
            merge.ExecuteNonQuery();
        }
        // 3. Drop the single-valued column.
        using var drop = conn.CreateCommand();
        drop.CommandText = "ALTER TABLE skill DROP COLUMN role";
        drop.ExecuteNonQuery();
    }

    private void ApplyV24SkillProjectScope(SqliteConnection conn)
    {
        using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM pragma_table_info('skill') WHERE name = 'project_id' LIMIT 1";
        if (probe.ExecuteScalar() is not null) return;
        using var alter = conn.CreateCommand();
        alter.CommandText = """
            ALTER TABLE skill ADD COLUMN project_id TEXT;
            ALTER TABLE skill ADD COLUMN source TEXT NOT NULL DEFAULT 'forge';
            """;
        alter.ExecuteNonQuery();
    }

    private void ApplyV19ProjectRoles(SqliteConnection conn)
    {
        using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM pragma_table_info('project') WHERE name = 'roles_json' LIMIT 1";
        if (probe.ExecuteScalar() is not null) return;
        using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE project ADD COLUMN roles_json TEXT NOT NULL DEFAULT '{}'";
        alter.ExecuteNonQuery();
    }

    private void ApplyV25AgentRunPhase(SqliteConnection conn)
    {
        using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM pragma_table_info('agent_run') WHERE name = 'phase' LIMIT 1";
        if (probe.ExecuteScalar() is not null) return;
        using var alter = conn.CreateCommand();
        alter.CommandText = """
            ALTER TABLE agent_run ADD COLUMN phase TEXT;
            ALTER TABLE agent_run ADD COLUMN resumed_session INTEGER;
            """;
        alter.ExecuteNonQuery();
    }

    private void ApplyLegacyKiloRenames(SqliteConnection conn)
    {
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM pragma_table_info('agent') WHERE name = 'kilo_name' LIMIT 1";
            var exists = probe.ExecuteScalar();
            if (exists is not null)
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE agent RENAME COLUMN kilo_name TO agent_name";
                alter.ExecuteNonQuery();
            }
        }
        using (var update = conn.CreateCommand())
        {
            update.CommandText = "UPDATE issue SET assignee = 'forge' WHERE assignee = 'kilo'";
            update.ExecuteNonQuery();
        }
    }

    private string T(string name) => _db.Dialect.Table(name);

    public async Task<IssueRecord> CreateAsync(NewIssue spec, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Per-type monotonic short_id; transaction keeps it race-safe.
        var shortId = await NextShortIdAsync(conn, tx, spec.Type);
        var shortIdStr = shortId.ToString();
        var id = $"{spec.Type}-{shortIdStr}";
        var now = DateTime.UtcNow;
        var metadataJson = SerializeMetadata(spec.Metadata);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {T("issue")} (id, short_id, type, title, description, status, priority, assignee, created_at, updated_at, metadata_json, parent_issue_id)
                VALUES (@id, @short, @type, @title, @desc, @status, @pri, @assignee, @now, @now, @meta, @parent);
                """;
            cmd.AddParam("@id", id);
            cmd.AddParam("@short", shortId);
            cmd.AddParam("@type", spec.Type);
            cmd.AddParam("@title", spec.Title);
            cmd.AddParam("@desc", (object?)spec.Description ?? DBNull.Value);
            cmd.AddParam("@status", "Pending");
            cmd.AddParam("@pri", spec.Priority);
            cmd.AddParam("@assignee", (object?)spec.Assignee ?? DBNull.Value);
            cmd.AddParam("@now", now.ToString(DateFormat));
            cmd.AddParam("@meta", metadataJson);
            cmd.AddParam("@parent", (object?)spec.ParentId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await InsertEventAsync(conn, tx, id, "created", spec.Description, ct);
        await tx.CommitAsync(ct);
        return new IssueRecord(id, shortIdStr, spec.Type, spec.Title, spec.Description,
            IssueStatus.Pending, spec.Priority, spec.Assignee, now, now, null, metadataJson);
    }

    public async Task<IReadOnlyList<IssueRecord>> ListAsync(IssueFilter filter, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);

        var sql = $"SELECT id, short_id, type, title, description, status, priority, assignee, created_at, updated_at, closed_at, metadata_json, parent_issue_id, dispatch_checkpoint, checkpoint_at, recovery_attempts FROM {T("issue")} WHERE 1=1";
        if (filter.Status is not null) sql += " AND status = @status";
        if (filter.Assignee is not null) sql += " AND assignee = @assignee";
        if (filter.Type is not null) sql += " AND type = @type";
        sql += " ORDER BY created_at";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (filter.Status is not null) cmd.AddParam("@status", filter.Status.ToString());
        if (filter.Assignee is not null) cmd.AddParam("@assignee", filter.Assignee);
        if (filter.Type is not null) cmd.AddParam("@type", filter.Type);

        var list = new List<IssueRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(ReadIssue(rd));
        return list;
    }

    public async Task<IReadOnlyList<IssueRecord>> ReadyAsync(int limit, CancellationToken ct = default)
        => await ReadyAsync(limit, sprintId: null, ct);

    /// <summary>
    /// Returns Pending issues that have no open 'blocks' edges, optionally
    /// filtered to those in a sprint. Pass null for sprintId to fall back
    /// to "all Pending" (no sprint filter). Blocked issues are excluded
    /// from the ready queue so the dispatcher doesn't pick them up.
    /// </summary>
    public async Task<IReadOnlyList<IssueRecord>> ReadyAsync(int limit, string? sprintId, CancellationToken ct = default)
    {
        // Open = blocker status NOT IN ('Completed', 'Closed'). Failed
        // is open-on-purpose: if a blocker failed, the operator must
        // explicitly clear it before dependents can proceed.
        var notBlockedPredicate = $"""
            NOT EXISTS (
                SELECT 1 FROM {T("issue_dep")} d
                INNER JOIN {T("issue")} b ON b.id = d.blocker_id
                WHERE d.blocked_id = i.id
                  AND d.kind = 'blocks'
                  AND b.status NOT IN ('Completed', 'Closed')
            )
            """;

        if (sprintId is null)
        {
            await using var conn = await _db.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT id, short_id, type, title, description, status, priority, assignee,
                       created_at, updated_at, closed_at, metadata_json, parent_issue_id,
                       dispatch_checkpoint, checkpoint_at, recovery_attempts
                FROM {T("issue")} i
                WHERE status = 'Pending' AND {notBlockedPredicate}
                ORDER BY priority ASC, created_at ASC
                """;
            var list = new List<IssueRecord>();
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) list.Add(ReadIssue(rd));
            return limit > 0 ? list.Take(limit).ToList() : list;
        }

        await using var conn2 = await _db.OpenAsync(ct);
        await using var cmd2 = conn2.CreateCommand();
        cmd2.CommandText = $"""
            SELECT i.id, i.short_id, i.type, i.title, i.description, i.status, i.priority, i.assignee,
                   i.created_at, i.updated_at, i.closed_at, i.metadata_json, i.parent_issue_id,
                   i.dispatch_checkpoint, i.checkpoint_at, i.recovery_attempts
            FROM {T("issue")} i
            INNER JOIN {T("sprint_issue")} si ON i.id = si.issue_id
            WHERE i.status = 'Pending' AND si.sprint_id = @sid AND {notBlockedPredicate}
            ORDER BY i.priority ASC, i.created_at ASC
            """;
        cmd2.AddParam("@sid", sprintId);
        var list2 = new List<IssueRecord>();
        await using var rd2 = await cmd2.ExecuteReaderAsync(ct);
        while (await rd2.ReadAsync(ct)) list2.Add(ReadIssue(rd2));
        return limit > 0 ? list2.Take(limit).ToList() : list2;
    }

    public async Task<IssueRecord?> ClaimAsync(string id, string assignee, CancellationToken ct = default)
    {
        // Atomic transition: only succeeds if status is currently 'Pending'
        // AND the issue is not blocked by an open 'blocks' edge. The two
        // predicates run inside one transaction so two dispatchers on the
        // same DB can't both claim a freshly-unblocked task in the same
        // tick.
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (await IsBlockedAsync(id, ct))
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        var now = DateTime.UtcNow;
        int rows;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE {T("issue")}
                SET status = 'InProgress', assignee = @assignee, updated_at = @now,
                    dispatch_checkpoint = @cp, checkpoint_at = @now
                WHERE id = @id AND status = 'Pending'
                """;
            cmd.AddParam("@id", id);
            cmd.AddParam("@assignee", assignee);
            cmd.AddParam("@now", now.ToString(DateFormat));
            cmd.AddParam("@cp", DispatchCheckpoint.Claimed.ToDbValue());
            rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows == 0)
            {
                await tx.RollbackAsync(ct);
                return null; // already claimed or not Pending
            }
        }

        await InsertEventAsync(conn, tx, id, "claimed", $"assignee={assignee}", ct);
        await tx.CommitAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<IssueRecord> TransitionAsync(
        string id,
        IssueStatus to,
        string? error,
        IDictionary<string, object>? metadata = null,
        CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);

        var now = DateTime.UtcNow;
        var isTerminal = to is IssueStatus.Completed or IssueStatus.Failed or IssueStatus.Blocked or IssueStatus.Closed;
        var current = await GetAsync(id, ct)
            ?? throw new InvalidOperationException($"Issue {id} not found");

        var metadataJson = metadata is { Count: > 0 }
            ? MergeAndSerializeMetadata(current.MetadataJson, metadata)
            : current.MetadataJson;

        await using (var cmd = conn.CreateCommand())
        {
            // Terminal transitions clear the dispatch_checkpoint so
            // a future StartupRecovery sweep doesn't try to replay
            // an issue that's already completed / failed / closed.
            cmd.CommandText = isTerminal
                ? $"""UPDATE {T("issue")} SET status=@to, updated_at=@now, closed_at=@now, metadata_json=@meta, dispatch_checkpoint=NULL, checkpoint_at=NULL WHERE id=@id"""
                : $"""UPDATE {T("issue")} SET status=@to, updated_at=@now, metadata_json=@meta WHERE id=@id""";
            cmd.AddParam("@to", to.ToString());
            cmd.AddParam("@now", now.ToString(DateFormat));
            cmd.AddParam("@meta", metadataJson);
            cmd.AddParam("@id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await InsertEventAsync(conn, null, id, "status_change",
            $"{current.Status}->{to}{(error is null ? "" : $" err={error}")}", ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task<IssueRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, short_id, type, title, description, status, priority, assignee, created_at, updated_at, closed_at, metadata_json, parent_issue_id, dispatch_checkpoint, checkpoint_at, recovery_attempts
            FROM {T("issue")} WHERE id = @id
            """;
        cmd.AddParam("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? ReadIssue(rd) : null;
    }

    public async Task<IssueEventRecord> AddEventAsync(string id, string kind, string? detail = null, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await InsertEventAsync(conn, null, id, kind, detail, ct);
    }

    // --- P4 Stage A — checkpoint-based recovery ---

    public async Task SetCheckpointAsync(string id, DispatchCheckpoint checkpoint, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {T("issue")}
            SET dispatch_checkpoint = @cp, checkpoint_at = @now, updated_at = @now
            WHERE id = @id
            """;
        cmd.AddParam("@id", id);
        cmd.AddParam("@cp", checkpoint.ToDbValue());
        cmd.AddParam("@now", now.ToString(DateFormat));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<IssueRecord>> ListInProgressForRecoveryAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // InProgress + assignee=forge are the dispatch candidates
        // the recoverer inspects. Assignee is the durable marker
        // identifying the orchestrator's runner; other agents
        // (reviewer, manual) don't
        // go through the EngineeringDispatchWorkflow so they
        // don't need recovery.
        cmd.CommandText = $"""
            SELECT id, short_id, type, title, description, status, priority, assignee, created_at, updated_at, closed_at, metadata_json, parent_issue_id, dispatch_checkpoint, checkpoint_at, recovery_attempts
            FROM {T("issue")}
            WHERE status = 'InProgress' AND assignee = 'forge'
            ORDER BY updated_at ASC
            """;
        var list = new List<IssueRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(ReadIssue(rd));
        return list;
    }

    public async Task<int> IncrementRecoveryAttemptsAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {T("issue")} SET recovery_attempts = recovery_attempts + 1, updated_at = @now
            WHERE id = @id
            """;
        cmd.AddParam("@id", id);
        cmd.AddParam("@now", DateTime.UtcNow.ToString(DateFormat));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<IssueEventRecord>> ListEventsAsync(string issueId, int limit = 50, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {_db.Dialect.TopParam("@limit")}id, issue_id, ts, actor, kind, detail
            FROM {T("issue_event")}
            WHERE issue_id = @id
            ORDER BY ts DESC, id DESC
            {_db.Dialect.LimitParam("@limit")}
            """;
        cmd.AddParam("@id", issueId);
        cmd.AddParam("@limit", limit);
        var list = new List<IssueEventRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new IssueEventRecord(
                rd.GetInt64(0),
                rd.GetString(1),
                DateTime.ParseExact(rd.GetString(2), DateFormat,
                    System.Globalization.CultureInfo.InvariantCulture),
                rd.GetString(3),
                rd.GetString(4),
                rd.IsDBNull(5) ? null : rd.GetString(5)));
        }
        return list;
    }

    // --- Dependency graph (Phase 2 of docs/embedded-issues.md) ---

    public async Task<IssueEdge> AddDependencyAsync(
        string blockerId, string blockedId, IssueDepKind kind, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blockerId)) throw new ArgumentException("blockerId required", nameof(blockerId));
        if (string.IsNullOrWhiteSpace(blockedId)) throw new ArgumentException("blockedId required", nameof(blockedId));
        if (blockerId == blockedId) throw new ArgumentException("an issue cannot block itself");

        await using var conn = await _db.OpenAsync(ct);

        // Validate both issues exist. Keeps FK violations as a 400-shaped
        // error rather than a SQLite constraint blow-up.
        var blocker = await GetAsync(blockerId, ct)
            ?? throw new InvalidOperationException($"blocker issue {blockerId} not found");
        var blocked = await GetAsync(blockedId, ct)
            ?? throw new InvalidOperationException($"blocked issue {blockedId} not found");

        var now = DateTime.UtcNow;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = _db.Provider == ForgeDbProvider.SqlServer
            ? $"""
                MERGE {T("issue_dep")} WITH (HOLDLOCK) AS t
                USING (SELECT @blocker AS blocker_id, @blocked AS blocked_id, @kind AS kind) AS s
                    ON t.blocker_id = s.blocker_id AND t.blocked_id = s.blocked_id AND t.kind = s.kind
                WHEN MATCHED THEN UPDATE SET created_at = @now
                WHEN NOT MATCHED THEN INSERT (blocker_id, blocked_id, kind, created_at)
                    VALUES (@blocker, @blocked, @kind, @now);
                """
            : """
                INSERT INTO issue_dep(blocker_id, blocked_id, kind, created_at)
                VALUES(@blocker, @blocked, @kind, @now)
                ON CONFLICT(blocker_id, blocked_id, kind) DO UPDATE SET created_at = @now
                """;
        cmd.AddParam("@blocker", blocker.Id);
        cmd.AddParam("@blocked", blocked.Id);
        cmd.AddParam("@kind", kind.ToDbValue());
        cmd.AddParam("@now", now.ToString(DateFormat));
        await cmd.ExecuteNonQueryAsync(ct);

        await InsertEventAsync(conn, null, blockedId,
            "dep_added", $"{kind.ToDbValue()} blocker={blockerId}", ct);

        return new IssueEdge(blocker.Id, blocked.Id, kind, now);
    }

    public async Task<bool> RemoveDependencyAsync(
        string blockerId, string blockedId, IssueDepKind kind, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM {T("issue_dep")}
            WHERE blocker_id = @blocker AND blocked_id = @blocked AND kind = @kind
            """;
        cmd.AddParam("@blocker", blockerId);
        cmd.AddParam("@blocked", blockedId);
        cmd.AddParam("@kind", kind.ToDbValue());
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows > 0)
        {
            await InsertEventAsync(conn, null, blockedId,
                "dep_removed", $"{kind.ToDbValue()} blocker={blockerId}", ct);
            return true;
        }
        return false;
    }

    public async Task<IReadOnlyList<IssueEdge>> DependenciesAsync(string id, CancellationToken ct = default)
    {
        // Returns both directions: edges where `id` is the blocker AND
        // edges where `id` is the blocked. Caller can filter by kind or
        // direction (this matches what a graph view needs).
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT blocker_id, blocked_id, kind, created_at
            FROM {T("issue_dep")}
            WHERE blocker_id = @id OR blocked_id = @id
            ORDER BY created_at ASC
            """;
        cmd.AddParam("@id", id);
        var list = new List<IssueEdge>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var kindStr = rd.GetString(2);
            IssueDepKindExtensions.TryParseDb(kindStr, out var kind);
            list.Add(new IssueEdge(
                BlockerId: rd.GetString(0),
                BlockedId: rd.GetString(1),
                Kind: kind,
                CreatedAt: ParseDate(rd.GetString(3))));
        }
        return list;
    }

    public async Task<bool> IsBlockedAsync(string id, CancellationToken ct = default)
    {
        // Open-blocker definition: a 'blocks' edge whose blocker issue is
        // NOT yet in a terminal-resolved state (Completed or Closed).
        // Failed is intentionally treated as open — if the blocker
        // failed, the operator must explicitly clear it (close or
        // remove the edge) before dependents can proceed.
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {_db.Dialect.Top(1)}1
            FROM {T("issue_dep")} d
            INNER JOIN {T("issue")} b ON b.id = d.blocker_id
            WHERE d.blocked_id = @id
              AND d.kind = 'blocks'
              AND b.status NOT IN ('Completed', 'Closed')
            {_db.Dialect.Limit(1)}
            """;
        cmd.AddParam("@id", id);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    public async Task<HashSet<string>> ListBlockersOfAsync(IReadOnlyCollection<string> blockedIds, CancellationToken ct = default)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (blockedIds.Count == 0) return set;
        await using var conn = await _db.OpenAsync(ct);
        // IN-list built from parameters (blockedIds are internal
        // sprint-member ids, never user text).
        var names = blockedIds.Select((_, i) => $"@id{i}").ToArray();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT DISTINCT d.blocker_id
            FROM {T("issue_dep")} d
            INNER JOIN {T("issue")} b ON b.id = d.blocker_id
            WHERE d.blocked_id IN ({string.Join(",", names)})
              AND d.kind = 'blocks'
              AND b.status NOT IN ('Completed', 'Closed')
            """;
        var i = 0;
        foreach (var id in blockedIds) cmd.AddParam(names[i++], id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) set.Add(rd.GetString(0));
        return set;
    }

    private async Task<int> NextShortIdAsync(DbConnection conn, DbTransaction tx, string type)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = _db.Provider == ForgeDbProvider.SqlServer
            ? $"""
                MERGE {T("issue_seq")} WITH (HOLDLOCK) AS t
                USING (SELECT @t AS type) AS s ON t.type = s.type
                WHEN MATCHED THEN UPDATE SET next_short = next_short + 1
                WHEN NOT MATCHED THEN INSERT (type, next_short) VALUES (@t, 2)
                OUTPUT INSERTED.next_short - 1;
                """
            : """
                INSERT INTO issue_seq(type, next_short) VALUES(@t, 2)
                ON CONFLICT(type) DO UPDATE SET next_short = next_short + 1
                RETURNING next_short - 1
                """;
        cmd.AddParam("@t", type);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private async Task<IssueEventRecord> InsertEventAsync(
        DbConnection conn, DbTransaction? tx, string issueId, string kind, string? detail, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = _db.Provider == ForgeDbProvider.SqlServer
            ? $"""
                INSERT INTO {T("issue_event")}(issue_id, ts, actor, kind, detail)
                OUTPUT INSERTED.id
                VALUES(@id, @ts, @actor, @kind, @detail);
                """
            : """
                INSERT INTO issue_event(issue_id, ts, actor, kind, detail)
                VALUES(@id, @ts, @actor, @kind, @detail);
                SELECT last_insert_rowid();
                """;
        cmd.AddParam("@id", issueId);
        cmd.AddParam("@ts", DateTime.UtcNow.ToString(DateFormat));
        cmd.AddParam("@actor", "orchestrator");
        cmd.AddParam("@kind", kind);
        cmd.AddParam("@detail", (object?)detail ?? DBNull.Value);
        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return new IssueEventRecord(id, issueId, DateTime.UtcNow, "orchestrator", kind, detail);
    }

    private static IssueRecord ReadIssue(DbDataReader rd)
        => new(
            Id: rd.GetString(0),
            ShortId: rd.GetString(1),
            Type: rd.GetString(2),
            Title: rd.GetString(3),
            Description: rd.IsDBNull(4) ? null : rd.GetString(4),
            Status: Enum.Parse<IssueStatus>(rd.GetString(5)),
            Priority: rd.GetInt32(6),
            Assignee: rd.IsDBNull(7) ? null : rd.GetString(7),
            CreatedAt: ParseDate(rd.GetString(8)),
            UpdatedAt: ParseDate(rd.GetString(9)),
            ClosedAt: rd.IsDBNull(10) ? null : ParseDate(rd.GetString(10)),
            MetadataJson: rd.GetString(11),
            ParentIssueId: rd.IsDBNull(12) ? null : rd.GetString(12),
            DispatchCheckpoint: rd.IsDBNull(13) ? null : ParseCheckpoint(rd.GetString(13)),
            CheckpointAt: rd.IsDBNull(14) ? null : ParseDate(rd.GetString(14)),
            RecoveryAttempts: rd.GetInt32(15));

    private static DispatchCheckpoint? ParseCheckpoint(string s)
    {
        DispatchCheckpointExtensions.TryParseDb(s, out var c);
        return c;
    }

    private static DateTime ParseDate(string s) => DateTime.ParseExact(s, DateFormat, System.Globalization.CultureInfo.InvariantCulture);

    private static string SerializeMetadata(IReadOnlyDictionary<string, object>? src)
    {
        var dict = src is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(src);
        return JsonSerializer.Serialize(dict);
    }

    private static string MergeAndSerializeMetadata(string existing, IDictionary<string, object> additions)
    {
        Dictionary<string, object> merged;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(existing) ? "{}" : existing);
            merged = new Dictionary<string, object>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                merged[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText())!;
        }
        catch
        {
            merged = new Dictionary<string, object>();
        }
        foreach (var kv in additions)
            merged[kv.Key] = kv.Value;
        return JsonSerializer.Serialize(merged);
    }

    public string ConnectionString => _db.ConnectionString;

    public static string DateFormatTime(DateTime dt) => dt.ToString(DateFormat, System.Globalization.CultureInfo.InvariantCulture);
    public static DateTime ParseTime(string s) => DateTime.ParseExact(s, DateFormat, System.Globalization.CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        // Connection pooling handles cleanup; nothing to do explicitly.
        await ValueTask.CompletedTask;
    }

    public void Dispose() { /* pooled connections */ }
}

public sealed record IssueEventRecord(
    long Id,
    string IssueId,
    DateTime Timestamp,
    string Actor,
    string Kind,
    string? Detail);










