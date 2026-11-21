using System.Data;
using System.Text.Json;
using Forge.Core.Completion;
using Forge.Core.Db;

namespace Forge.Core;

public sealed partial class MemoryStore
{
    /// <summary>Immutable controller-owned integrated-head provenance, protected by existing origins SQL guards.</summary>
    internal async Task RecordIntegrationVerificationAsync(IntegratedHeadVerificationEvidence proof, CancellationToken ct)
    {
        var key = TaskDeliveryEvidence.IntegrationAuthorityKey(proof.ProjectId, proof.TaskId, proof.VerifiedHeadSha);
        var json = JsonSerializer.Serialize(proof);
        await using var connection = await _db.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        using var read = connection.CreateCommand(); read.Transaction = tx;
        var locked = _db.Provider == ForgeDbProvider.SqlServer ? " WITH (UPDLOCK,HOLDLOCK)" : "";
        read.CommandText = $"SELECT body FROM {T("memory")}{locked} WHERE [key]=@key";
        read.AddParam("@key", key);
        var existing = await read.ExecuteScalarAsync(ct) as string;
        if (existing is not null && existing != json)
            throw new InvalidOperationException("Integrated-head authority cannot be replaced by a different receipt.");
        if (existing is null) await UpsertAsync(connection, tx, key, json, null, DateTime.UtcNow, ct);
        await tx.CommitAsync(ct);
    }
}
