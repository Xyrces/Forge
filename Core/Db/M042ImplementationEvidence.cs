using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>
/// Persisted-format boundary for ImplementationVerified plans and their independently
/// reviewed evidence. There is no table change: schema-41 binaries cannot interpret
/// these records and must fail readiness after the explicit initializer stamps 42.
/// </summary>
public sealed class M042ImplementationEvidence : ISqlServerMigration
{
    public int Version => 42;
    public string Name => "implementation-evidence-format";

    // The existing migration runner stamps both profiles only after Up succeeds.
    public void Up(DbConnection connection, SqlServerDialect dialect, ForgeSchemaProfile profile) { }

    // SQLite's explicit initialization chain stamps CurrentSchemaVersion afterwards.
    public static void ApplySqlite(DbConnection connection) { }
}
