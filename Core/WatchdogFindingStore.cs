namespace Forge.Core;

/// <summary>A persisted watchdog finding row.</summary>
public sealed record WatchdogFinding(
    long Id, string Kind, string TargetId, string Severity, string Detail,
    string Status, DateTime FirstSeenAt, DateTime LastSeenAt, DateTime? ResolvedAt);

/// <summary>
/// Per-project watchdog finding store. Dedupe model: one OPEN row
/// per (kind, target) — repeat sightings update last_seen/detail;
/// when a condition stops appearing in a scan, its open rows resolve.
/// </summary>
public sealed class WatchdogFindingStore
{
    private readonly IssueStore _issues;

    public WatchdogFindingStore(IssueStore issues)
    {
        _issues = issues;
    }

    private string T(string table) => _issues.Db.Dialect.Table(table);
    private bool IsSql => _issues.Db.Provider == Db.ForgeDbProvider.SqlServer;
    private string P(string name) => IsSql ? "@" + name : "$" + name;

    public sealed record SyncResult(int Added, int Updated, int Resolved, IReadOnlyList<WatchdogFinding> NewFindings);

    /// <summary>Reconcile a scan with the stored findings: insert new
    /// (kind, target)s, touch repeats, resolve clears.</summary>
    public async Task<SyncResult> SyncAsync(
        IReadOnlyList<WatchdogScanner.Finding> findings, DateTime utcNow, CancellationToken ct = default)
    {
        var open = await ListOpenAsync(ct);
        var openByKey = open.ToDictionary(f => Key(f.Kind, f.TargetId), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = 0; var updated = 0;
        var newFindings = new List<WatchdogFinding>();
        var now = utcNow.ToString(IssueStore.DateFormat);

        foreach (var f in findings)
        {
            var key = Key(f.Kind, f.TargetId);
            seen.Add(key);
            if (openByKey.TryGetValue(key, out var existing))
            {
                // Escalation (operator-approved 2026-07-31): a finding
                // open for more than a day is no longer a transient —
                // it sorts with the fails on the attention feed.
                var severity = existing.Severity == "warn"
                    && utcNow - existing.FirstSeenAt > TimeSpan.FromHours(24)
                        ? "fail"
                        : f.Severity;
                await Exec($"""
                    UPDATE {T("watchdog_finding")}
                    SET last_seen_at = {P("now")}, detail = {P("detail")}, severity = {P("sev")}
                    WHERE id = {P("id")}
                    """,
                    ("now", now), ("detail", f.Detail), ("sev", severity), ("id", existing.Id));
                updated++;
            }
            else
            {
                var id = await InsertAsync(new WatchdogFinding(0, f.Kind, f.TargetId, f.Severity, f.Detail,
                    "open", utcNow, utcNow, null), ct);
                added++;
                newFindings.Add(new WatchdogFinding(id, f.Kind, f.TargetId, f.Severity, f.Detail,
                    "open", utcNow, utcNow, null));
            }
        }

        var resolved = 0;
        foreach (var f in open)
        {
            if (seen.Contains(Key(f.Kind, f.TargetId))) continue;
            await Exec($"""
                UPDATE {T("watchdog_finding")}
                SET status = 'resolved', resolved_at = {P("now")}
                WHERE id = {P("id")}
                """,
                ("now", now), ("id", f.Id));
            resolved++;
        }

        return new SyncResult(added, updated, resolved, newFindings);
    }

    public async Task<IReadOnlyList<WatchdogFinding>> ListOpenAsync(CancellationToken ct = default)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, kind, target_id, severity, detail, status, first_seen_at, last_seen_at, resolved_at
            FROM {T("watchdog_finding")} WHERE status = 'open' ORDER BY id
            """;
        var list = new List<WatchdogFinding>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Map(rd));
        return list;
    }

    private async Task<long> InsertAsync(WatchdogFinding f, CancellationToken ct)
    {
        await using var conn = await _issues.Db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSql
            ? $"""
                INSERT INTO {T("watchdog_finding")} (kind, target_id, severity, detail, status, first_seen_at, last_seen_at)
                OUTPUT INSERTED.id VALUES (@kind, @target, @sev, @detail, 'open', @first, @last);
                """
            : $"""
                INSERT INTO {T("watchdog_finding")} (kind, target_id, severity, detail, status, first_seen_at, last_seen_at)
                VALUES ($kind, $target, $sev, $detail, 'open', $first, $last);
                SELECT last_insert_rowid();
                """;
        void Add(string n, object v) { var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = v; cmd.Parameters.Add(p); }
        Add(P("kind"), f.Kind); Add(P("target"), f.TargetId); Add(P("sev"), f.Severity);
        Add(P("detail"), f.Detail);
        Add(P("first"), f.FirstSeenAt.ToString(IssueStore.DateFormat));
        Add(P("last"), f.LastSeenAt.ToString(IssueStore.DateFormat));
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
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

    private static string Key(string kind, string targetId) => kind + "|" + targetId;

    private static WatchdogFinding Map(System.Data.Common.DbDataReader rd) =>
        new(Convert.ToInt64(rd.GetValue(0)), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4),
            rd.GetString(5), IssueStore.ParseTime(rd.GetString(6)), IssueStore.ParseTime(rd.GetString(7)),
            rd.IsDBNull(8) ? null : IssueStore.ParseTime(rd.GetString(8)));
}
