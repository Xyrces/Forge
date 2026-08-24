using Forge.Core.Db;

namespace Forge.Core;

/// <summary>Phase-1 clearance actions on a ledger row.</summary>
public static class FailureTriageActions
{
    public const string OperatorRequeue = "operator-requeue";
    public const string OperatorClose = "operator-close";
    public const string OperatorResetStrikes = "operator-reset-strikes";
    public const string AgedSweep = "aged-sweep";

    /// <summary>Phase 2: the triage agent requeued the task with
    /// guidance written from the failure evidence.</summary>
    public const string TriageRequeue = "triage-requeue";
    /// <summary>Phase 2: the triage agent (or the deterministic
    /// guardrails) parked the task for the operator — judgment calls
    /// stay human.</summary>
    public const string TriagePark = "triage-park";
    /// <summary>Phase 2: the triage agent flagged the failure signature
    /// as a suspected product bug. Ledger flag only — no issue is
    /// created (operator constraint).</summary>
    public const string TriageFlagBug = "triage-flag-bug";
    /// <summary>Phase 3: the triage agent escalated the task's next dev
    /// run to the role's configured escalation model. Writes the
    /// single-shot llm/taskModel marker and requeues the task (spends
    /// a strike round, like a triage requeue).</summary>
    public const string TriageEscalateModel = "escalate_model";
}

/// <summary>Ledger actors.</summary>
public static class FailureTriageActors
{
    public const string Operator = "operator";
    public const string Triage = "triage";
}

/// <summary>Row outcomes: NULL while open; 'pending' once an action is
/// recorded and the redispatch result is awaited.</summary>
public static class FailureTriageOutcomes
{
    public const string Pending = "pending";
    public const string Succeeded = "succeeded";
    public const string FailedAgain = "failed-again";
}

/// <summary>A persisted failure-ledger row.</summary>
public sealed record FailureTriageEntry(
    long Id, string TaskId, DateTime FailedAt, string Signature, string Classification,
    string? ErrorExcerpt, string? Action, string? Actor, DateTime? ActedAt,
    string? Outcome, string? EscalatedProvider, string? EscalatedModel);

/// <summary>
/// Per-project failure ledger (schema v35). Phase 1 is observability
/// only: the triage consumer opens a row when a task transitions to
/// Failed/Blocked, records the operator's clearance action, and closes
/// the outcome from the redispatch result. "Open" = the failure still
/// needs attention or its clearance is unproven: action IS NULL
/// (uncleared) OR outcome = 'pending' (cleared, redispatch in flight).
/// </summary>
public sealed class FailureTriageStore
{
    private readonly IssueStore _issues;

    public FailureTriageStore(IssueStore issues)
    {
        _issues = issues;
    }

    private string T(string table) => _issues.Db.Dialect.Table(table);
    private bool IsSql => _issues.Db.Provider == Db.ForgeDbProvider.SqlServer;
    private string P(string name) => IsSql ? "@" + name : "$" + name;

    public async Task<long> OpenAsync(
        string taskId, DateTime failedAt, string signature, string classification,
        string? errorExcerpt, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSql
            ? $"""
                INSERT INTO {T("failure_triage")} (task_id, failed_at, signature, classification, error_excerpt)
                OUTPUT INSERTED.id VALUES (@task, @failed, @sig, @cls, @excerpt);
                """
            : $"""
                INSERT INTO {T("failure_triage")} (task_id, failed_at, signature, classification, error_excerpt)
                VALUES ($task, $failed, $sig, $cls, $excerpt);
                SELECT last_insert_rowid();
                """;
        void Add(string n, object? v) { var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = v ?? DBNull.Value; cmd.Parameters.Add(p); }
        Add(P("task"), taskId); Add(P("failed"), failedAt.ToString(IssueStore.DateFormat));
        Add(P("sig"), signature); Add(P("cls"), classification); Add(P("excerpt"), errorExcerpt);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>The task's open row (uncleared, or cleared-but-
    /// unproven), if any. At most one is expected; the newest wins if
    /// a crash left two.</summary>
    public async Task<FailureTriageEntry?> GetOpenForTaskAsync(string taskId, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, task_id, failed_at, signature, classification, error_excerpt,
                   action, actor, acted_at, outcome, escalated_provider, escalated_model
            FROM {T("failure_triage")}
            WHERE task_id = {P("task")} AND (action IS NULL OR outcome = {P("pending")})
            ORDER BY id DESC
            """;
        cmd.AddParam(P("task"), taskId);
        cmd.AddParam(P("pending"), FailureTriageOutcomes.Pending);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Map(rd) : null;
    }

    /// <summary>Refresh an uncleared open row (redelivery of the same
    /// failure hint, or a Failed→Blocked re-failure before clearance).</summary>
    public async Task UpdateOpenAsync(
        long id, DateTime failedAt, string signature, string classification,
        string? errorExcerpt, CancellationToken ct = default)
    {
        await Exec($"""
            UPDATE {T("failure_triage")}
            SET failed_at = {P("failed")}, signature = {P("sig")},
                classification = {P("cls")}, error_excerpt = {P("excerpt")}
            WHERE id = {P("id")} AND action IS NULL
            """,
            ("failed", failedAt.ToString(IssueStore.DateFormat)), ("sig", signature),
            ("cls", classification), ("excerpt", (object?)errorExcerpt ?? DBNull.Value), ("id", id));
    }

    /// <summary>Record the clearance action on the row. Requeue-style
    /// actions set outcome='pending' (the redispatch result closes it);
    /// operator-close leaves the outcome NULL (no redispatch follows).
    /// The action IS NULL guard makes redelivery idempotent.</summary>
    public async Task RecordActionAsync(
        long id, string action, string actor, DateTime actedAt, string? outcome, CancellationToken ct = default)
    {
        await Exec($"""
            UPDATE {T("failure_triage")}
            SET action = {P("action")}, actor = {P("actor")}, acted_at = {P("acted")}, outcome = {P("outcome")}
            WHERE id = {P("id")} AND action IS NULL
            """,
            ("action", action), ("actor", actor), ("acted", actedAt.ToString(IssueStore.DateFormat)),
            ("outcome", (object?)outcome ?? DBNull.Value), ("id", id));
    }

    /// <summary>Close the outcome on an actioned row ('succeeded' /
    /// 'failed-again'). The outcome='pending' guard makes redelivery
    /// idempotent.</summary>
    public async Task CloseOutcomeAsync(long id, string outcome, CancellationToken ct = default)
    {
        await Exec($"""
            UPDATE {T("failure_triage")}
            SET outcome = {P("outcome")}
            WHERE id = {P("id")} AND outcome = {P("pending")}
            """,
            ("outcome", outcome), ("pending", FailureTriageOutcomes.Pending), ("id", id));
    }

    /// <summary>Newest-first rows, optionally bounded to failures at or
    /// after <paramref name="failedSince"/>.</summary>
    public async Task<IReadOnlyList<FailureTriageEntry>> ListAsync(
        DateTime? failedSince = null, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = failedSince is null
            ? $"""
                SELECT id, task_id, failed_at, signature, classification, error_excerpt,
                       action, actor, acted_at, outcome, escalated_provider, escalated_model
                FROM {T("failure_triage")} ORDER BY id DESC
                """
            : $"""
                SELECT id, task_id, failed_at, signature, classification, error_excerpt,
                       action, actor, acted_at, outcome, escalated_provider, escalated_model
                FROM {T("failure_triage")} WHERE failed_at >= {P("since")} ORDER BY id DESC
                """;
        if (failedSince is not null)
            cmd.AddParam(P("since"), failedSince.Value.ToString(IssueStore.DateFormat));
        var list = new List<FailureTriageEntry>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Map(rd));
        return list;
    }

    /// <summary>Newest-first rows for ONE task — the guardrail
    /// evaluator's and the per-task dashboard strip's view.</summary>
    public async Task<IReadOnlyList<FailureTriageEntry>> ListForTaskAsync(
        string taskId, CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, task_id, failed_at, signature, classification, error_excerpt,
                   action, actor, acted_at, outcome, escalated_provider, escalated_model
            FROM {T("failure_triage")} WHERE task_id = {P("task")} ORDER BY id DESC
            """;
        cmd.AddParam(P("task"), taskId);
        var list = new List<FailureTriageEntry>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Map(rd));
        return list;
    }

    private async Task Exec(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = await _issues.Db.OpenAsync(CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = P(n);
            p.Value = v;
            cmd.Parameters.Add(p);
        }
        await cmd.ExecuteNonQueryAsync();
    }

    private static FailureTriageEntry Map(System.Data.Common.DbDataReader rd) =>
        new(Convert.ToInt64(rd.GetValue(0)), rd.GetString(1), IssueStore.ParseTime(rd.GetString(2)),
            rd.GetString(3), rd.GetString(4),
            rd.IsDBNull(5) ? null : rd.GetString(5),
            rd.IsDBNull(6) ? null : rd.GetString(6),
            rd.IsDBNull(7) ? null : rd.GetString(7),
            rd.IsDBNull(8) ? null : IssueStore.ParseTime(rd.GetString(8)),
            rd.IsDBNull(9) ? null : rd.GetString(9),
            rd.IsDBNull(10) ? null : rd.GetString(10),
            rd.IsDBNull(11) ? null : rd.GetString(11));
}
