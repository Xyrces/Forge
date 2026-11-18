using Forge.Core.Db;

namespace Forge.Core;

public sealed partial class IssueStore
{
    /// <summary>
    /// Patch review presentation without resetting status, ownership or dispatch
    /// checkpoints. Compare-and-swap protects against another metadata writer;
    /// the audit commits with the successful patch.
    /// </summary>
    public Task PatchReviewMetadataAsync(string taskId, IReadOnlyDictionary<string, object> fields,
        string? audit, CancellationToken ct = default, string? actor = null)
    {
        if (fields.Keys.Any(key => !key.StartsWith("humanReview", StringComparison.Ordinal)
            && !key.StartsWith("review", StringComparison.Ordinal) && key != "awaitingHumanReview"))
            throw new ArgumentException("Only review metadata may be patched through this operation.", nameof(fields));
        return PatchMetadataOnlyAsync(taskId, fields, audit, ct, actor);
    }

    private async Task PatchMetadataOnlyAsync(string taskId, IReadOnlyDictionary<string, object> fields,
        string? audit, CancellationToken ct, string? actor = null)
    {
        if (audit?.Length > 4000 || actor?.Length > 256) throw new ArgumentException("Review audit exceeds the allowed length.");
        var patch = new Dictionary<string, object>(fields);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await using var connection = await _db.OpenAsync(ct);
            string before;
            using (var read = connection.CreateCommand())
            {
                read.CommandText = $"SELECT metadata_json FROM {T("issue")} WHERE id=@id";
                read.AddParam("@id", taskId);
                before = await read.ExecuteScalarAsync(ct) as string
                    ?? throw new InvalidOperationException($"Issue {taskId} not found.");
            }
            var after = MergeAndSerializeMetadata(before, patch);
            if (audit is null && string.Equals(before, after, StringComparison.Ordinal)) return;
            await using var transaction = await connection.BeginTransactionAsync(ct);
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                var sameMetadata = _db.Provider == ForgeDbProvider.SqlServer
                    ? "metadata_json COLLATE Latin1_General_100_BIN2 = @before COLLATE Latin1_General_100_BIN2"
                    : "metadata_json=@before";
                update.CommandText = $"UPDATE {T("issue")} SET metadata_json=@after,updated_at=@now WHERE id=@id AND {sameMetadata}";
                update.AddParam("@after", after); update.AddParam("@now", DateFormatTime(DateTime.UtcNow));
                update.AddParam("@id", taskId); update.AddParam("@before", before);
                if (await update.ExecuteNonQueryAsync(ct) == 0)
                {
                    await transaction.RollbackAsync(ct);
                    continue;
                }
            }
            if (audit is not null)
                await InsertEventAsync(connection, transaction, taskId, "human_review", audit, ct, actor);
            await transaction.CommitAsync(ct);
            return;
        }
        throw new InvalidOperationException("Review metadata changed repeatedly; reload and retry.");
    }
}
