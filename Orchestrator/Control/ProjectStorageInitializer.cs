using Forge.Configuration;
using Forge.Core;
using Forge.Core.Control;
using Forge.Core.Db;

namespace Forge.Orchestrator.Control;

/// <summary>Initializer-only provisioning of one workload; never changes registry visibility or existing workload versions.</summary>
internal static class ProjectStorageInitializer
{
    internal static async Task<int> RunAsync(AgentOptions options, CancellationToken ct)
    {
        if (options.Bootstrap.ReviewAuthorityOnly || options.Bootstrap.MigrationFromVersion is not null || options.Bootstrap.MigrationToVersion is not null)
            throw new InvalidOperationException("Project provisioning cannot also migrate or run authority-only initialization.");
        var registry = ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.RegistrySchema, ForgeSchemaProfile.Core, initializeSchema: false);
        await ProvisionAsync(registry, options.Controller.DatasetId, options.Bootstrap.ProjectId,
            options.Bootstrap.WorkerPrincipal, options.Bootstrap.ControllerPrincipal, ct);
        return 0;
    }

    internal static string Schema(string id)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9_-]{0,31}$"))
            throw new ArgumentException("Project id must be a canonical lowercase slug.");
        return new SqlServerDialect(ForgeDb.SchemaForProject(id)).Qualifier;
    }

    internal static async Task ProvisionAsync(IDbConnectionFactory registry, string dataset, string projectId,
        string worker, string controller, CancellationToken ct)
    {
        var schema = Schema(projectId);
        _ = ControllerInitializer.BudgetReconciliationPermissionsSql(controller, worker);
        await ControllerHost.VerifySchemaAsync(registry, ct);
        if (await new ControlLeaseStore(registry).ReadAsync(dataset, ct) is null)
            throw new InvalidOperationException("Project provisioning requires the existing initialized dataset.");
        await using var lockConnection = await registry.OpenAsync(ct);
        await using var command = lockConnection.CreateCommand();
        command.CommandText = "DECLARE @r int; EXEC @r=sp_getapplock @Resource=N'forge-schema-migration',@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=30000; SELECT @r;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) < 0)
            throw new InvalidOperationException("Another storage operation is running. Retry registration.");
        try
        {
            await using var registryIssues = new IssueStore(registry);
            await using var projects = new ProjectStore(registryIssues);
            var registered = await projects.ListAsync(ct);
            if (registered.Any(p => p.Id != projectId && string.Equals(Schema(p.Id), schema, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Another project owns this schema name.");
            // Claim the schema atomically before table creation. A stopped initializer can
            // safely resume; an unrelated preexisting schema is never silently adopted.
            command.Parameters.Clear();
            command.CommandText = $"""
                SET XACT_ABORT ON;
                BEGIN TRY
                BEGIN TRANSACTION;
                DECLARE @existed bit=CASE WHEN SCHEMA_ID(@schema) IS NULL THEN 0 ELSE 1 END;
                IF @existed=0 EXEC('CREATE SCHEMA [{schema}]');
                DECLARE @owner nvarchar(4000)=(SELECT CONVERT(nvarchar(4000),value) FROM sys.extended_properties
                  WHERE class=3 AND major_id=SCHEMA_ID(@schema) AND name=N'forge-project-owner');
                IF @owner IS NULL
                BEGIN
                  IF @existed=1 AND @registered=0
                    THROW 51045,'Existing schema is not owned by this project registration.',1;
                  EXEC sys.sp_addextendedproperty @name=N'forge-project-owner',@value=@identity,@level0type=N'SCHEMA',@level0name=@schema;
                END
                ELSE IF @owner COLLATE Latin1_General_100_BIN2 <> @identity COLLATE Latin1_General_100_BIN2
                  THROW 51045,'Schema belongs to another project or dataset.',1;
                COMMIT;
                END TRY
                BEGIN CATCH
                  IF @@TRANCOUNT > 0 ROLLBACK;
                  THROW;
                END CATCH;
                """;
            command.AddParam("@schema", schema); command.AddParam("@identity", dataset + "/" + projectId);
            command.AddParam("@registered", registered.Any(p => p.Id == projectId) ? 1 : 0);
            await command.ExecuteNonQueryAsync(ct);
            command.Parameters.Clear();
            command.CommandText = $"IF OBJECT_ID(N'[{schema}].[schema_version]',N'U') IS NOT NULL SELECT MAX(version) FROM [{schema}].[schema_version];";
            var version = await command.ExecuteScalarAsync(ct);
            if (version is not null && version != DBNull.Value
                && (Convert.ToInt32(version) > IssueStore.CurrentSchemaVersion
                    || (registered.Any(p => p.Id == projectId) && Convert.ToInt32(version) != IssueStore.CurrentSchemaVersion)))
                throw new InvalidOperationException("An existing project schema needs an explicit release migration, not registration.");
            var workload = ForgeDb.SqlServer(registry.ConnectionString, schema);
            await using (var initialized = new IssueStore(workload, projectId)) { }
            command.CommandText = $"GRANT SELECT,INSERT,UPDATE,DELETE ON SCHEMA::[{schema}] TO [{controller}];"
                + ControllerInitializer.InitiativeWorkerPermissionsSql(worker, schema);
            await command.ExecuteNonQueryAsync(ct);
            var authority = ForgeDb.SqlServer(registry.ConnectionString, schema, initializeSchema: false);
            await ReviewAuthorityInitializer.InstallAsync(authority, worker, controller, ct);
            await MemoryAuthoritySql.VerifyAsync(authority, worker, ct);
            // The worker receives broad workload DML only after all authority guards
            // and object DENYs are installed and independently attested.
            command.CommandText = $"GRANT SELECT,INSERT,UPDATE,DELETE ON SCHEMA::[{schema}] TO [{worker}];";
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await using var release = lockConnection.CreateCommand();
            release.CommandText = "EXEC sp_releaseapplock @Resource=N'forge-schema-migration',@LockOwner='Session';";
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
