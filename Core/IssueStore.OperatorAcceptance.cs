using System.Data.Common;
using Forge.Core.Db;

namespace Forge.Core;

public sealed partial class IssueStore
{
    // Canonical IssueStore-owned compound mutation: status, timestamps, metadata and
    // issue_event commit together. No lifecycle success or delivered evidence is fabricated.
    internal async Task ApplyOperatorAcceptanceRepairAsync(DbConnection connection, DbTransaction transaction,
        IssueRecord expected, IDictionary<string, object> additions, bool retire, string actor, CancellationToken ct)
    {
        var metadata = MergeAndSerializeMetadata(expected.MetadataJson, additions);
        var now = DateFormatTime(DateTime.UtcNow);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = retire
            ? $"UPDATE {T("issue")} SET status='Closed',closed_at=@now,updated_at=@now,assignee=NULL,dispatch_checkpoint=NULL,checkpoint_at=NULL,metadata_json=@meta WHERE id=@id AND status='Pending' AND updated_at=@expected AND metadata_json=@old AND assignee IS NULL AND dispatch_checkpoint IS NULL"
            : $"UPDATE {T("issue")} SET updated_at=@now,metadata_json=@meta WHERE id=@id AND status='Pending' AND updated_at=@expected AND metadata_json=@old AND assignee IS NULL AND dispatch_checkpoint IS NULL";
        command.AddParam("@now", now); command.AddParam("@meta", metadata); command.AddParam("@id", expected.Id);
        command.AddParam("@expected", DateFormatTime(expected.UpdatedAt)); command.AddParam("@old", expected.MetadataJson);
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Task changed during operator acceptance repair.");
        await InsertEventAsync(connection, transaction, expected.Id, retire ? "status_change" : "grooming_metadata",
            retire ? $"Pending->Closed (Cancelled): operator checks moved to outcome acceptance; actor={actor}"
                : $"Operator acceptance criteria attached to existing delivery unit; actor={actor}", ct);
    }
}
