using Microsoft.Data.Sqlite;

namespace Forge.Deploy;

// P8: typed access layer over the `deployment` table (schema v15,
// Core/IssueStore.cs). Lives in the SAME sqlite file as a project's
// issues -- one DeploymentStore per project, constructed against that
// project's issues.db path, exactly like SprintProposalAuditStore.
// The table itself is created by IssueStore's migration script, so any
// IssueStore construction against a given db path (which every project
// already does at startup) guarantees this table exists before a
// DeploymentStore touches it.
public sealed class DeploymentStore
{
    public const string DateFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private readonly string _connectionString;

    public DeploymentStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    public async Task<DeploymentCandidate> CreateAsync(
        string projectId, string commitSha, string? commitSummary, string? requestedBy,
        CancellationToken ct = default)
    {
        var id = $"deploy-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow.ToString(DateFormat);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO deployment(
                id, project_id, commit_sha, commit_summary, status,
                requested_at, requested_by)
            VALUES ($id, $pid, $sha, $summary, $status, $now, $by)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$pid", projectId);
        cmd.Parameters.AddWithValue("$sha", commitSha);
        cmd.Parameters.AddWithValue("$summary", (object?)commitSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", DeploymentStatus.Pending.ToString());
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$by", (object?)requestedBy ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);

        return new DeploymentCandidate(
            id, projectId, commitSha, commitSummary, DeploymentStatus.Pending,
            DateTime.Parse(now), requestedBy, BuildLog: null,
            ApprovedAt: null, ApprovedBy: null, DeployedAt: null, DeployLog: null);
    }

    public async Task<DeploymentCandidate?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns + " WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? Read(rd) : null;
    }

    public async Task<IReadOnlyList<DeploymentCandidate>> ListAsync(
        string? projectId = null, int limit = 100, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectColumns +
            (projectId is null ? "" : " WHERE project_id = $pid") +
            " ORDER BY requested_at DESC LIMIT $limit";
        if (projectId is not null) cmd.Parameters.AddWithValue("$pid", projectId);
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<DeploymentCandidate>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(Read(rd));
        return list;
    }

    public Task SetStatusAsync(string id, DeploymentStatus status, CancellationToken ct = default) =>
        ExecAsync("UPDATE deployment SET status = $s WHERE id = $id",
            c => { c.Parameters.AddWithValue("$s", status.ToString()); c.Parameters.AddWithValue("$id", id); }, ct);

    public Task AppendBuildLogAsync(string id, DeploymentStatus status, string log, CancellationToken ct = default) =>
        ExecAsync(
            "UPDATE deployment SET status = $s, build_log = $log WHERE id = $id",
            c =>
            {
                c.Parameters.AddWithValue("$s", status.ToString());
                c.Parameters.AddWithValue("$log", log);
                c.Parameters.AddWithValue("$id", id);
            }, ct);

    // Atomic CAS: the UPDATE's WHERE clause re-checks status at the
    // exact moment of the write, so two concurrent approve calls for
    // the same id can never both succeed -- the loser gets 0 affected
    // rows back and the caller treats that as a conflict instead of
    // launching a second executor. A separate read-then-write pattern
    // (read the row, check status client-side, then write) cannot make
    // this guarantee against a second request racing in between.
    public async Task<bool> TryApproveAsync(string id, string? approvedBy, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE deployment
            SET status = $s, approved_at = $now, approved_by = $by
            WHERE id = $id AND status IN ($fromPending, $fromBuildPassed)
            """;
        cmd.Parameters.AddWithValue("$s", DeploymentStatus.Approved.ToString());
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString(DateFormat));
        cmd.Parameters.AddWithValue("$by", (object?)approvedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$fromPending", DeploymentStatus.Pending.ToString());
        cmd.Parameters.AddWithValue("$fromBuildPassed", DeploymentStatus.BuildPassed.ToString());
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    // Same CAS shape as TryApproveAsync. Rejecting is only meaningful
    // before an operator has committed to deploying -- once a row is
    // Approved/Deploying/Deployed/DeployFailed it would silently
    // clobber the outcome of (or race with) the deployment executor
    // and destroy the audit trail, so those transitions are refused.
    public async Task<bool> TryRejectAsync(string id, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE deployment SET status = $s
            WHERE id = $id AND status IN ($p1, $p2, $p3, $p4)
            """;
        cmd.Parameters.AddWithValue("$s", DeploymentStatus.Rejected.ToString());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$p1", DeploymentStatus.Pending.ToString());
        cmd.Parameters.AddWithValue("$p2", DeploymentStatus.BuildRunning.ToString());
        cmd.Parameters.AddWithValue("$p3", DeploymentStatus.BuildPassed.ToString());
        cmd.Parameters.AddWithValue("$p4", DeploymentStatus.BuildFailed.ToString());
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public Task MarkDeployedAsync(string id, string deployLog, CancellationToken ct = default) =>
        ExecAsync(
            "UPDATE deployment SET status = $s, deployed_at = $now, deploy_log = $log WHERE id = $id",
            c =>
            {
                c.Parameters.AddWithValue("$s", DeploymentStatus.Deployed.ToString());
                c.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString(DateFormat));
                c.Parameters.AddWithValue("$log", deployLog);
                c.Parameters.AddWithValue("$id", id);
            }, ct);

    public Task MarkDeployFailedAsync(string id, string deployLog, CancellationToken ct = default) =>
        ExecAsync(
            "UPDATE deployment SET status = $s, deploy_log = $log WHERE id = $id",
            c =>
            {
                c.Parameters.AddWithValue("$s", DeploymentStatus.DeployFailed.ToString());
                c.Parameters.AddWithValue("$log", deployLog);
                c.Parameters.AddWithValue("$id", id);
            }, ct);

    private async Task ExecAsync(string sql, Action<SqliteCommand> bind, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private const string SelectColumns = """
        SELECT id, project_id, commit_sha, commit_summary, status,
               requested_at, requested_by, build_log, approved_at,
               approved_by, deployed_at, deploy_log
        FROM deployment
        """;

    private static DeploymentCandidate Read(SqliteDataReader rd) => new(
        Id: rd.GetString(0),
        ProjectId: rd.GetString(1),
        CommitSha: rd.GetString(2),
        CommitSummary: rd.IsDBNull(3) ? null : rd.GetString(3),
        Status: Enum.Parse<DeploymentStatus>(rd.GetString(4)),
        RequestedAt: DateTime.Parse(rd.GetString(5)),
        RequestedBy: rd.IsDBNull(6) ? null : rd.GetString(6),
        BuildLog: rd.IsDBNull(7) ? null : rd.GetString(7),
        ApprovedAt: rd.IsDBNull(8) ? null : DateTime.Parse(rd.GetString(8)),
        ApprovedBy: rd.IsDBNull(9) ? null : rd.GetString(9),
        DeployedAt: rd.IsDBNull(10) ? null : DateTime.Parse(rd.GetString(10)),
        DeployLog: rd.IsDBNull(11) ? null : rd.GetString(11));
}

public enum DeploymentStatus
{
    Pending,
    BuildRunning,
    BuildPassed,
    BuildFailed,
    Approved,
    Deploying,
    Deployed,
    DeployFailed,
    Rejected,
}

public sealed record DeploymentCandidate(
    string Id,
    string ProjectId,
    string CommitSha,
    string? CommitSummary,
    DeploymentStatus Status,
    DateTime RequestedAt,
    string? RequestedBy,
    string? BuildLog,
    DateTime? ApprovedAt,
    string? ApprovedBy,
    DateTime? DeployedAt,
    string? DeployLog);
