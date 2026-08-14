using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>
/// v34 — intake_message.proposed_epic_description + proposed_epic_priority:
/// the full draft payload so an intake proposal can live as a
/// session-scoped draft WITHOUT an issue row (operator rule 2026-08-14:
/// "we should not be creating the epics until they are accepted" —
/// unaccepted drafts sat on the board as Pending work). The epic row is
/// created at accept time from this payload. Idempotent.
/// </summary>
public sealed class M034IntakeMessageProposalPayload : ISqlServerMigration
{
    public int Version => 34;
    public string Name => "intake-message-proposal-payload";

    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile != ForgeSchemaProfile.Workload) return;
        var table = dialect.Table("intake_message");
        var q = dialect.Qualifier;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'intake_message' AND c.name = 'proposed_epic_description')
                ALTER TABLE {table} ADD proposed_epic_description NVARCHAR(MAX) NULL;
            IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'intake_message' AND c.name = 'proposed_epic_priority')
                ALTER TABLE {table} ADD proposed_epic_priority INT NULL;
            """;
        cmd.ExecuteNonQuery();
    }
}
