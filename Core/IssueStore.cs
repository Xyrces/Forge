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
            return doc.RootElement.TryGetProperty(key, out var v) ? v.ToString() : null;
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
    /// <c>assignee=kilo</c>. These are the candidates the
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
    public const int CurrentSchemaVersion = 13;
    public const string DateFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private readonly string _connectionString;
    private readonly string _dbPath;

    public IssueStore(string dbPath)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
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
        using var conn = Open();
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
                kilo_name    TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                scope        TEXT NOT NULL DEFAULT '',
                description  TEXT,
                enabled      INTEGER NOT NULL DEFAULT 1,
                config_json  TEXT NOT NULL DEFAULT '{}',
                created_at   TEXT NOT NULL,
                updated_at   TEXT NOT NULL
            );

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
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public async Task<IssueRecord> CreateAsync(NewIssue spec, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        // Per-type monotonic short_id; transaction keeps it race-safe.
        var shortId = await NextShortIdAsync(conn, tx, spec.Type);
        var shortIdStr = shortId.ToString();
        var id = $"{spec.Type}-{shortIdStr}";
        var now = DateTime.UtcNow;
        var metadataJson = SerializeMetadata(spec.Metadata);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO issue (id, short_id, type, title, description, status, priority, assignee, created_at, updated_at, metadata_json, parent_issue_id)
                VALUES ($id, $short, $type, $title, $desc, $status, $pri, $assignee, $now, $now, $meta, $parent);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$short", shortId);
            cmd.Parameters.AddWithValue("$type", spec.Type);
            cmd.Parameters.AddWithValue("$title", spec.Title);
            cmd.Parameters.AddWithValue("$desc", (object?)spec.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", "Pending");
            cmd.Parameters.AddWithValue("$pri", spec.Priority);
            cmd.Parameters.AddWithValue("$assignee", (object?)spec.Assignee ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now.ToString(DateFormat));
            cmd.Parameters.AddWithValue("$meta", metadataJson);
            cmd.Parameters.AddWithValue("$parent", (object?)spec.ParentId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await InsertEventAsync(conn, tx, id, "created", spec.Description, ct);
        await tx.CommitAsync(ct);
        return new IssueRecord(id, shortIdStr, spec.Type, spec.Title, spec.Description,
            IssueStatus.Pending, spec.Priority, spec.Assignee, now, now, null, metadataJson);
    }

    public async Task<IReadOnlyList<IssueRecord>> ListAsync(IssueFilter filter, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = "SELECT id, short_id, type, title, description, status, priority, assignee, created_at, updated_at, closed_at, metadata_json, parent_issue_id, dispatch_checkpoint, checkpoint_at, recovery_attempts FROM issue WHERE 1=1";
        if (filter.Status is not null) sql += " AND status = $status";
        if (filter.Assignee is not null) sql += " AND assignee = $assignee";
        if (filter.Type is not null) sql += " AND type = $type";
        sql += " ORDER BY created_at";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (filter.Status is not null) cmd.Parameters.AddWithValue("$status", filter.Status.ToString());
        if (filter.Assignee is not null) cmd.Parameters.AddWithValue("$assignee", filter.Assignee);
        if (filter.Type is not null) cmd.Parameters.AddWithValue("$type", filter.Type);

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
        const string notBlockedPredicate = """
            NOT EXISTS (
                SELECT 1 FROM issue_dep d
                INNER JOIN issue b ON b.id = d.blocker_id
                WHERE d.blocked_id = i.id
                  AND d.kind = 'blocks'
                  AND b.status NOT IN ('Completed', 'Closed')
            )
            """;

        if (sprintId is null)
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT id, short_id, type, title, description, status, priority, assignee,
                       created_at, updated_at, closed_at, metadata_json, parent_issue_id,
                       dispatch_checkpoint, checkpoint_at, recovery_attempts
                FROM issue i
                WHERE status = 'Pending' AND {notBlockedPredicate}
                ORDER BY priority ASC, created_at ASC
                """;
            var list = new List<IssueRecord>();
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) list.Add(ReadIssue(rd));
            return limit > 0 ? list.Take(limit).ToList() : list;
        }

        await using var conn2 = new SqliteConnection(_connectionString);
        await conn2.OpenAsync(ct);
        await using var cmd2 = conn2.CreateCommand();
        cmd2.CommandText = $"""
            SELECT i.id, i.short_id, i.type, i.title, i.description, i.status, i.priority, i.assignee,
                   i.created_at, i.updated_at, i.closed_at, i.metadata_json, i.parent_issue_id,
                   i.dispatch_checkpoint, i.checkpoint_at, i.recovery_attempts
            FROM issue i
            INNER JOIN sprint_issue si ON i.id = si.issue_id
            WHERE i.status = 'Pending' AND si.sprint_id = $sid AND {notBlockedPredicate}
            ORDER BY i.priority ASC, i.created_at ASC
            """;
        cmd2.Parameters.AddWithValue("$sid", sprintId);
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
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

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
            cmd.CommandText = """
                UPDATE issue
                SET status = 'InProgress', assignee = $assignee, updated_at = $now,
                    dispatch_checkpoint = $cp, checkpoint_at = $now
                WHERE id = $id AND status = 'Pending'
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$assignee", assignee);
            cmd.Parameters.AddWithValue("$now", now.ToString(DateFormat));
            cmd.Parameters.AddWithValue("$cp", DispatchCheckpoint.Claimed.ToDbValue());
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
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

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
                ? """UPDATE issue SET status=$to, updated_at=$now, closed_at=$now, metadata_json=$meta, dispatch_checkpoint=NULL, checkpoint_at=NULL WHERE id=$id"""
                : """UPDATE issue SET status=$to, updated_at=$now, metadata_json=$meta WHERE id=$id""";
            cmd.Parameters.AddWithValue("$to", to.ToString());
            cmd.Parameters.AddWithValue("$now", now.ToString(DateFormat));
            cmd.Parameters.AddWithValue("$meta", metadataJson);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await InsertEventAsync(conn, null, id, "status_change",
            $"{current.Status}->{to}{(error is null ? "" : $" err={error}")}", ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task<IssueRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, short_id, type, title, description, status, priority, assignee, created_at, updated_at, closed_at, metadata_json, parent_issue_id, dispatch_checkpoint, checkpoint_at, recovery_attempts
            FROM issue WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? ReadIssue(rd) : null;
    }

    public async Task<IssueEventRecord> AddEventAsync(string id, string kind, string? detail = null, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return await InsertEventAsync(conn, null, id, kind, detail, ct);
    }

    // --- P4 Stage A — checkpoint-based recovery ---

    public async Task SetCheckpointAsync(string id, DispatchCheckpoint checkpoint, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE issue
            SET dispatch_checkpoint = $cp, checkpoint_at = $now, updated_at = $now
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$cp", checkpoint.ToDbValue());
        cmd.Parameters.AddWithValue("$now", now.ToString(DateFormat));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<IssueRecord>> ListInProgressForRecoveryAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // InProgress + assignee=kilo are the dispatch candidates
        // the recoverer inspects. Assignee is the durable marker
        // (the kilo JWT); other agents (reviewer, manual) don't
        // go through the EngineeringDispatchWorkflow so they
        // don't need recovery.
        cmd.CommandText = """
            SELECT id, short_id, type, title, description, status, priority, assignee, created_at, updated_at, closed_at, metadata_json, parent_issue_id, dispatch_checkpoint, checkpoint_at, recovery_attempts
            FROM issue
            WHERE status = 'InProgress' AND assignee = 'kilo'
            ORDER BY updated_at ASC
            """;
        var list = new List<IssueRecord>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(ReadIssue(rd));
        return list;
    }

    public async Task<int> IncrementRecoveryAttemptsAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE issue SET recovery_attempts = recovery_attempts + 1, updated_at = $now
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString(DateFormat));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // --- Dependency graph (Phase 2 of docs/embedded-issues.md) ---

    public async Task<IssueEdge> AddDependencyAsync(
        string blockerId, string blockedId, IssueDepKind kind, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(blockerId)) throw new ArgumentException("blockerId required", nameof(blockerId));
        if (string.IsNullOrWhiteSpace(blockedId)) throw new ArgumentException("blockedId required", nameof(blockedId));
        if (blockerId == blockedId) throw new ArgumentException("an issue cannot block itself");

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Validate both issues exist. Keeps FK violations as a 400-shaped
        // error rather than a SQLite constraint blow-up.
        var blocker = await GetAsync(blockerId, ct)
            ?? throw new InvalidOperationException($"blocker issue {blockerId} not found");
        var blocked = await GetAsync(blockedId, ct)
            ?? throw new InvalidOperationException($"blocked issue {blockedId} not found");

        var now = DateTime.UtcNow;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO issue_dep(blocker_id, blocked_id, kind, created_at)
            VALUES($blocker, $blocked, $kind, $now)
            ON CONFLICT(blocker_id, blocked_id, kind) DO UPDATE SET created_at = $now
            """;
        cmd.Parameters.AddWithValue("$blocker", blocker.Id);
        cmd.Parameters.AddWithValue("$blocked", blocked.Id);
        cmd.Parameters.AddWithValue("$kind", kind.ToDbValue());
        cmd.Parameters.AddWithValue("$now", now.ToString(DateFormat));
        await cmd.ExecuteNonQueryAsync(ct);

        await InsertEventAsync(conn, null, blockedId,
            "dep_added", $"{kind.ToDbValue()} blocker={blockerId}", ct);

        return new IssueEdge(blocker.Id, blocked.Id, kind, now);
    }

    public async Task<bool> RemoveDependencyAsync(
        string blockerId, string blockedId, IssueDepKind kind, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM issue_dep
            WHERE blocker_id = $blocker AND blocked_id = $blocked AND kind = $kind
            """;
        cmd.Parameters.AddWithValue("$blocker", blockerId);
        cmd.Parameters.AddWithValue("$blocked", blockedId);
        cmd.Parameters.AddWithValue("$kind", kind.ToDbValue());
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
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT blocker_id, blocked_id, kind, created_at
            FROM issue_dep
            WHERE blocker_id = $id OR blocked_id = $id
            ORDER BY created_at ASC
            """;
        cmd.Parameters.AddWithValue("$id", id);
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
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1
            FROM issue_dep d
            INNER JOIN issue b ON b.id = d.blocker_id
            WHERE d.blocked_id = $id
              AND d.kind = 'blocks'
              AND b.status NOT IN ('Completed', 'Closed')
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", id);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    private static async Task<int> NextShortIdAsync(SqliteConnection conn, SqliteTransaction tx, string type)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO issue_seq(type, next_short) VALUES($t, 2)
            ON CONFLICT(type) DO UPDATE SET next_short = next_short + 1
            RETURNING next_short - 1
            """;
        cmd.Parameters.AddWithValue("$t", type);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    private static async Task<IssueEventRecord> InsertEventAsync(
        SqliteConnection conn, SqliteTransaction? tx, string issueId, string kind, string? detail, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO issue_event(issue_id, ts, actor, kind, detail)
            VALUES($id, $ts, $actor, $kind, $detail);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$id", issueId);
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString(DateFormat));
        cmd.Parameters.AddWithValue("$actor", "orchestrator");
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        return new IssueEventRecord(id, issueId, DateTime.UtcNow, "orchestrator", kind, detail);
    }

    private static IssueRecord ReadIssue(SqliteDataReader rd)
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

    public string ConnectionString => _connectionString;

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










