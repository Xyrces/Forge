using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Forge.Core.Db;

namespace Forge.Core.Initiatives;

public sealed record NewInitiativeProposal(string ProjectId, string SourceKey, string Origin,
    string Title, string Body, int Priority = 2, string SourceJson = "{}", string? SessionId = null);
public sealed record InitiativeProposal(string Id, string ProjectId, int Revision, string Hash,
    string Title, string Body, int Priority, string Origin, string SourceKey, string SourceJson,
    string? SessionId, DateTime CreatedAt, DateTime? ApprovedAt, string? ApprovedBy, string? EpicId, string? SpecId);
public sealed record InitiativeProposalSummary(string Id, string ProjectId, int Revision, string Hash,
    string Title, int Priority, string Origin, string SourceKey, string? SessionId, DateTime CreatedAt,
    DateTime? ApprovedAt, string? ApprovedBy, string? EpicId, string? SpecId);
public sealed record InitiativeSourceBinding(string Id, string SourceJson);

public sealed record InitiativeIngestion(string ProposalId, int Revision, string ProjectId, string Hash,
    string EpicId, string SpecId, string RefinementState, int Attempts, string? LastError, DateTime UpdatedAt);

internal static class InitiativeDb
{
    internal static DbCommand Command(DbConnection c, DbTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.AddParam(name, value);
        return cmd;
    }
    internal static async Task LockAsync(DbConnection c, DbTransaction tx, ISqlDialect d, CancellationToken ct)
    {
        // Take the write lock before reading: no SQLite deferred read->write upgrade or SQL phantom create race.
        using var cmd = Command(c, tx, $"UPDATE {d.Table("initiative_lock")} SET value=value WHERE id=1");
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Initiative schema not initialized.");
    }
    internal static string Hash(string project, string title, string body, int priority) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { project, title, body, priority })));
    internal static void Text(string value, string name, int max = 128)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
            throw new ArgumentException($"Invalid {name}.");
    }
    internal static void Payload(string title, string body, int priority)
    {
        Text(title, "title", 200);
        Text(body, "body", 1000000);
        if (priority is < 1 or > 5)
            throw new ArgumentException("Invalid priority.");
    }
    internal static string Now() => IssueStore.DateFormatTime(DateTime.UtcNow);
}
