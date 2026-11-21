using Forge.Core;
using Forge.Core.Db;

namespace Forge.Tests;

/// <summary>Explicit synthetic post-upgrade state for bridge reader/producer tests.</summary>
internal static class Format42Fixture
{
    internal static async Task UpgradeAsync(IssueStore issues)
    {
        issues.EnsureSchema();
        await using var connection = await issues.Db.OpenAsync();
        M042ImplementationEvidence.ApplySqlite(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO schema_version(version,applied_at) VALUES(42,'explicit format42 test fixture')";
        await command.ExecuteNonQueryAsync();
    }
}
