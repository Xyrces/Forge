namespace Forge.Core.Db;

/// <summary>Project-scoped SQL enforcement for rollback-compatible operator memory.
/// Workers retain ordinary memory DML, but cannot alter controls or audit receipts.</summary>
public static class MemoryAuthoritySql
{
    public static async Task InstallAsync(IDbConnectionFactory db, string worker, string controller, CancellationToken ct = default)
    {
        var sql = Protect(db.Qualifier, worker);
        _ = Protect(db.Qualifier, controller);
        if (worker.Equals(controller, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Controller and worker SQL identities must be distinct.");
        await using var connection = await db.OpenAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }
        await using (var grant = connection.CreateCommand())
        {
            grant.CommandText = $"ENABLE TRIGGER [{db.Qualifier}].[protect_operator_memory] ON [{db.Qualifier}].[memory];"
                + $"GRANT VIEW DEFINITION ON OBJECT::[{db.Qualifier}].[memory] TO [{controller}]";
            await grant.ExecuteNonQueryAsync(ct);
        }
        await VerifyAsync(db, worker, ct);
    }

    public static async Task VerifyAsync(IDbConnectionFactory db, string workerPrincipal, CancellationToken ct = default)
    {
        var expected = Protect(db.Qualifier, workerPrincipal);
        await using var connection = await db.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tr.is_disabled,tr.is_instead_of_trigger,sm.definition FROM sys.triggers tr JOIN sys.sql_modules sm ON sm.object_id=tr.object_id WHERE tr.object_id=OBJECT_ID(@trigger) AND tr.parent_id=OBJECT_ID(@table)";
        command.AddParam("@trigger", $"{db.Qualifier}.protect_operator_memory");
        command.AddParam("@table", $"{db.Qualifier}.memory");
        await using var reader = await command.ExecuteReaderAsync(ct);
        // SQL Server rewrites CREATE OR ALTER to CREATE with preserved padding.
        // Normalize only this DDL prefix, never predicate/body text.
        static string Canonical(string text) => System.Text.RegularExpressions.Regex.Replace(
            text.Replace("\r\n", "\n").Trim(), @"\A(?:CREATE(?:\s+OR\s+ALTER)?|ALTER)\s+TRIGGER\b", "CREATE TRIGGER");
        if (!await reader.ReadAsync(ct) || reader.GetBoolean(0) || reader.GetBoolean(1)
            || reader.IsDBNull(2) || Canonical(reader.GetString(2)) != Canonical(expected))
            throw new InvalidOperationException("The project SQL review authority guard is absent, disabled, unreadable or differs from this controller. Run the release initializer before activation.");
    }

    public static string Protect(string schema, string workerPrincipal)
    {
        foreach (var name in new[] { schema, workerPrincipal })
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-zA-Z][a-zA-Z0-9_-]{0,127}$"))
                throw new ArgumentException("Invalid authority identifier.");
        return $"""
            CREATE OR ALTER TRIGGER [{schema}].[protect_operator_memory]
            ON [{schema}].[memory] AFTER INSERT,UPDATE,DELETE AS
            BEGIN
              SET NOCOUNT ON;
              IF USER_NAME() = N'{workerPrincipal}' AND EXISTS (
                SELECT 1 FROM (SELECT [key] FROM inserted UNION ALL SELECT [key] FROM deleted) AS touched
                WHERE [key] COLLATE Latin1_General_100_CI_AI LIKE N'review/%'
                   OR [key] COLLATE Latin1_General_100_CI_AI LIKE N'gate/%'
                   OR [key] COLLATE Latin1_General_100_CI_AI LIKE N'workflow/%'
                   OR [key] COLLATE Latin1_General_100_CI_AI = N'deployment/merge-fence')
                THROW 51042, 'Operator control memory is read-only to workers.', 1;
              IF EXISTS (SELECT 1 FROM (SELECT [key] FROM inserted UNION ALL SELECT [key] FROM deleted) touched
                  WHERE [key] COLLATE Latin1_General_100_CI_AI IN (N'gate/merge',N'deployment/merge-fence')
                     OR [key] COLLATE Latin1_General_100_CI_AI LIKE N'workflow/live%')
                 AND (EXISTS (SELECT 1 FROM [{schema}].[memory] WITH (UPDLOCK,HOLDLOCK)
                              WHERE [key] COLLATE Latin1_General_100_CI_AI=N'deployment/merge-fence')
                      OR EXISTS (SELECT 1 FROM deleted WHERE [key] COLLATE Latin1_General_100_CI_AI=N'deployment/merge-fence'))
              BEGIN
                IF NOT EXISTS (SELECT 1 FROM [{schema}].[memory]
                    WHERE [key] COLLATE Latin1_General_100_CI_AI=N'deployment/merge-fence' AND ttl_days IS NULL AND DATALENGTH(body)>0)
                  THROW 51043, 'Compatibility merge fence cannot be removed. Upgrade to the current controller to reconcile review controls.', 1;
                IF NOT EXISTS (SELECT 1 FROM [{schema}].[memory]
                    WHERE [key] COLLATE Latin1_General_100_CI_AI=N'gate/merge' AND ttl_days IS NULL
                      AND body COLLATE Latin1_General_100_BIN2=N'hold' AND DATALENGTH(body)=8)
                  THROW 51043, 'Legacy merge gate is protected by saved review controls. Upgrade to the current controller to change operator intent.', 1;
                IF EXISTS (SELECT 1 FROM [{schema}].[memory]
                    WHERE [key] COLLATE Latin1_General_100_CI_AI LIKE N'workflow/live%' AND ISJSON(body)<>1)
                  THROW 51043, 'A fenced workflow must retain readable merge-gate enforcement.', 1;
                IF EXISTS (SELECT 1 FROM [{schema}].[memory] workflow
                    WHERE workflow.[key] COLLATE Latin1_General_100_CI_AI LIKE N'workflow/live%'
                      AND NOT EXISTS (
                        SELECT 1 FROM OPENJSON(workflow.body,'$.steps') step
                        CROSS APPLY OPENJSON(step.value,'$.gates') gate
                        WHERE JSON_VALUE(step.value,'$.enabled')=N'true'
                          AND gate.value COLLATE Latin1_General_100_CI_AS=N'merge'))
                  THROW 51043, 'A fenced workflow cannot disable the legacy merge gate. Upgrade to the current controller.', 1;
              END;
            END;
            """;
    }
}
