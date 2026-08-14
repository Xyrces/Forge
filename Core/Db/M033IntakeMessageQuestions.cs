using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>
/// v33 — intake_message.questions_json: structured clarifying
/// questions attached to an assistant intake message (the
/// ask_questions tool, or parsed from the reply text as a fallback),
/// rendered as clickable cards on the intake page (2026-08-12).
/// Idempotent.
/// </summary>
public sealed class M033IntakeMessageQuestions : ISqlServerMigration
{
    public int Version => 33;
    public string Name => "intake-message-questions";

    public void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile)
    {
        if (profile != ForgeSchemaProfile.Workload) return;
        var table = dialect.Table("intake_message");
        var q = dialect.Qualifier;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '{q}' AND t.name = 'intake_message' AND c.name = 'questions_json')
                ALTER TABLE {table} ADD questions_json NVARCHAR(MAX) NULL;
            """;
        cmd.ExecuteNonQuery();
    }
}
