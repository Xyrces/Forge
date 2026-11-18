using System.Data;
using Forge.Core.Control;
using Forge.Core.Db;

namespace Forge.Core;

public sealed partial class MemoryStore
{
    // The prior controller only reads gate/merge. Preserve the operator's
    // actual setting separately so normal promotion does not stop new work.
    internal async Task WriteMergeGateAsync(bool? operatorHold, string? releaseOperation, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var now = DateTime.UtcNow;
        string? previous = null;
        string? current = null;
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            var hint = _db.Provider == ForgeDbProvider.SqlServer ? " WITH (UPDLOCK,HOLDLOCK)" : "";
            read.CommandText = $"SELECT [key],body,ts,ttl_days FROM {T("memory")}{hint} WHERE [key] IN (@legacy,@current)";
            read.AddParam("@legacy", DeploymentMergeFence.LegacyKey);
            read.AddParam("@current", DeploymentMergeFence.OperatorKey);
            await using var rows = await read.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
            {
                if (!rows.IsDBNull(3) && ParseDate(rows.GetString(2)).AddDays(rows.GetInt32(3)) <= now)
                    continue;
                if (string.Equals(rows.GetString(0), DeploymentMergeFence.LegacyKey, StringComparison.OrdinalIgnoreCase))
                    previous = rows.GetString(1);
                else
                    current = rows.GetString(1);
            }
        }

        var intent = operatorHold switch { true => "hold", false => "open", null => current ?? previous ?? "open" };
        if (intent is not ("hold" or "open"))
            throw new InvalidDataException("Cannot preserve an unrecognized merge gate setting during deployment.");
        await UpsertAsync(conn, tx, DeploymentMergeFence.LegacyKey, "hold", null, now, ct);
        await UpsertAsync(conn, tx, DeploymentMergeFence.OperatorKey, intent, null, now, ct);
        if (releaseOperation is not null)
            await UpsertAsync(conn, tx, DeploymentMergeFence.AuditKey, releaseOperation, null, now, ct);
        await tx.CommitAsync(ct);
    }
}
