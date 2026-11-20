using System.Globalization;
using System.Text.Json;
using static Forge.Core.Initiatives.InitiativeDb;

namespace Forge.Core.Initiatives;

public sealed record IntakeInboxItem(string Kind, string Id, string? SessionId, string Title, string Origin,
    int? Revision, DateTime? ApprovedAt, DateTime UpdatedAt, string Group);
public sealed record IntakeInboxPage(IReadOnlyList<IntakeInboxItem> Items, string? NextCursor);
public sealed record InitiativeRevision(int Revision, string Title, string Body, int Priority, string Hash);

public sealed partial class InitiativeProposalStore
{
    private sealed record InboxCursor(string Project, int Bucket, string Activity, string Id);
    private static readonly string[] InboxGroups = ["review", "suggestion", "discussion", "planning"];

    public async Task<InitiativeRevision?> ReadRevisionAsync(string projectId, string id, int revision, CancellationToken ct)
    {
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null, $"""
            SELECT r.revision,r.title,r.body,r.priority,r.content_hash
            FROM {T("initiative_proposal")} p JOIN {T("initiative_revision")} r ON r.proposal_id=p.id
            WHERE p.project_id=@project AND p.id=@id AND r.revision=@revision
            """, ("@project", projectId), ("@id", id), ("@revision", revision));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4));
    }

    // Intake sessions and proposal revisions share the intake registry; ingestion receipts live
    // in the workload's own database. The inbox only needs the authoritative approval here.
    public async Task<IntakeInboxPage> ReadInboxAsync(string projectId, string? cursor, CancellationToken ct, int limit = 20)
    {
        var after = DecodeInboxCursor(projectId, cursor);
        limit = Math.Clamp(limit, 1, 100);
        var dialect = issues.Db.Dialect;
        var predicate = after is null ? "" : """
            WHERE bucket > @bucket OR (bucket = @bucket AND
                  (activity < @activity OR (activity = @activity AND id > @after)))
            """;
        await using var connection = await issues.Db.OpenAsync(ct);
        using var command = Command(connection, null, $"""
            WITH inbox AS (
                SELECT 'proposal' AS kind, p.id, p.session_id, r.title, p.origin, r.revision, r.approved_at,
                       CASE WHEN s.updated_at > COALESCE(r.approved_at,r.created_at) THEN s.updated_at
                            ELSE COALESCE(r.approved_at,r.created_at) END AS activity,
                       CASE WHEN r.approved_at IS NOT NULL THEN 3 WHEN p.origin='forge' AND p.session_id IS NULL THEN 1 ELSE 0 END AS bucket
                FROM {T("initiative_proposal")} p JOIN {T("initiative_revision")} r
                    ON r.proposal_id=p.id AND r.revision=p.current_revision
                LEFT JOIN {T("intake_session")} s ON s.id=p.session_id AND s.project_id=p.project_id
                WHERE p.project_id=@project
                UNION ALL
                SELECT 'conversation', s.id, s.id,
                       CASE WHEN s.title IN ('New session','New intake','New idea') THEN COALESCE(
                           (SELECT {dialect.Top(1)}SUBSTRING(m.content,1,100) FROM {T("intake_message")} m
                            WHERE m.session_id=s.id AND m.role='User' ORDER BY m.id{dialect.Limit(1)}),s.title) ELSE s.title END,
                       'user', NULL, NULL, s.updated_at, 2
                FROM {T("intake_session")} s
                WHERE s.project_id=@project AND NOT EXISTS (
                    SELECT 1 FROM {T("initiative_proposal")} p WHERE p.project_id=s.project_id AND p.session_id=s.id)
            )
            SELECT {dialect.Top(limit + 1)}kind,id,session_id,title,origin,revision,approved_at,activity,bucket
            FROM inbox {predicate} ORDER BY bucket,activity DESC,id{dialect.Limit(limit + 1)}
            """, ("@project", projectId), ("@bucket", after?.Bucket), ("@activity", after?.Activity), ("@after", after?.Id));
        var items = new List<IntakeInboxItem>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        InboxCursor? last = null;
        while (await reader.ReadAsync(ct))
        {
            if (items.Count == limit)
                return new(items, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(last!)));
            var bucket = reader.GetInt32(8);
            var id = reader.GetString(1);
            var activity = reader.GetString(7);
            items.Add(new(reader.GetString(0), id, reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : IssueStore.ParseTime(reader.GetString(6)), IssueStore.ParseTime(activity), InboxGroups[bucket]));
            last = new(projectId, bucket, activity, id);
        }
        return new(items, null);
    }

    private static InboxCursor? DecodeInboxCursor(string projectId, string? value)
    {
        if (value is null) return null;
        try
        {
            if (value.Length > 2048) throw new FormatException();
            var cursor = JsonSerializer.Deserialize<InboxCursor>(Convert.FromBase64String(value));
            if (cursor is null || cursor.Project != projectId || cursor.Bucket < 0 || cursor.Bucket >= InboxGroups.Length
                || string.IsNullOrWhiteSpace(cursor.Id) || !DateTime.TryParseExact(cursor.Activity, IssueStore.DateFormat,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new FormatException();
            return cursor;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The inbox page has expired or belongs to another project.", nameof(value), ex);
        }
    }
}
