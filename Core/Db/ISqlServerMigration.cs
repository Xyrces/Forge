using System.Data.Common;

namespace Forge.Core.Db;

/// <summary>
/// One ordered, idempotent SQL Server schema/data migration. The runner in
/// <see cref="IssueStore"/> applies pending migrations after the guarded
/// fresh-create (new databases/schemas are born at the current shape and
/// skip straight to <see cref="SqlServerMigrations.ExpectedVersion"/>).
/// Migrations run per (schema, profile): use the supplied
/// <paramref name="profile"/> to no-op steps that don't apply to the
/// schema being initialized, and write every statement existence-guarded
/// so a partially-applied or manually-repaired database converges instead
/// of crashing.
/// </summary>
public interface ISqlServerMigration
{
    /// <summary>Absolute version, continuing the SQLite chain numbering
    /// (first SQL Server migration = 24).</summary>
    int Version { get; }
    string Name { get; }
    void Up(DbConnection conn, SqlServerDialect dialect, ForgeSchemaProfile profile);
}

/// <summary>
/// The ordered SQL Server migration list. SQLite keeps its own chain in
/// <c>Core/IssueStore.cs</c> (InitializeSchemaSqlite); SQL Server
/// fresh-creates at the current shape and applies these to existing
/// databases. To change the SQL Server schema: add a migration here,
/// update the fresh-create DDL to the final shape, and bump nothing —
/// <see cref="ExpectedVersion"/> derives from the list.
/// </summary>
public static class SqlServerMigrations
{
    public static IReadOnlyList<ISqlServerMigration> All { get; } = new ISqlServerMigration[]
    {
        new M024ProfileSplit(),
        new M025SkillProjectScope(),
        new M026AgentRunPhase(),
        new M027AgentRunProject(),
        new M028FollowUpDraft(),
        new M029WatchdogFinding(),
        new M030FollowUpDraftDisposition(),
        new M031AgentRunDispatchId(),
        new M032AgentRunTokens(),
        new M033IntakeMessageQuestions(),
        new M034IntakeMessageProposalPayload(),
    };

    public static int ExpectedVersion =>
        All.Count == 0 ? IssueStore.CurrentSchemaVersion
                       : Math.Max(IssueStore.CurrentSchemaVersion, All.Max(m => m.Version));
}
