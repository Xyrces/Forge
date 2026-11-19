using Forge.Configuration;
using Forge.Core;
using Forge.Core.Control;
using Forge.Core.Db;
using Microsoft.Extensions.Logging;

namespace Forge.Orchestrator.Control;

/// <summary>One explicit initialization/migration operation, never called by standby or agent workers.</summary>
public static class ControllerInitializer
{
    public static async Task<int> RunAsync(AgentOptions options, ILoggerFactory logs, CancellationToken ct = default)
    {
        ControllerHost.Validate(options);
        if (options.Bootstrap.ProjectProvisionOnly)
            return await ProjectStorageInitializer.RunAsync(options, ct);
        if (options.Bootstrap.ReviewAuthorityOnly)
            return await ReviewAuthorityInitializer.RunAsync(options, ct);
        ValidateMigrationContract(options.Bootstrap);
        // Validate both configured identities before acquiring a connection or running DDL.
        var reconciliationPermissions = BudgetReconciliationPermissionsSql(
            options.Bootstrap.ControllerPrincipal, options.Bootstrap.WorkerPrincipal);
        if (options.Bootstrap.ProjectId != "forge")
            throw new InvalidOperationException("The initial installer bootstraps only the Forge project.");
        var registryFactory = ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.RegistrySchema, ForgeSchemaProfile.Core);
        await using var lockConnection = await registryFactory.OpenAsync(ct);
        await using var migrationLock = lockConnection.CreateCommand();
        migrationLock.CommandText = "DECLARE @result int; EXEC @result=sp_getapplock @Resource=N'forge-schema-migration',@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=30000; SELECT @result;";
        if (Convert.ToInt32(await migrationLock.ExecuteScalarAsync(ct)) < 0)
            throw new InvalidOperationException("Another migration owns the schema lock.");
        await using (var ownerProbe = lockConnection.CreateCommand())
        {
            ownerProbe.CommandText = "IF OBJECT_ID(N'core.control_dataset',N'U') IS NOT NULL SELECT owner_instance_id,dataset_id,budget_limit_microusd FROM core.control_dataset WHERE singleton=1;";
            await using var reader = await ownerProbe.ExecuteReaderAsync(ct);
            if (reader.FieldCount > 0 && await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0))
                    throw new InvalidOperationException("Stop/drain the existing controller before any schema changes.");
                if (reader.GetString(1) != options.Controller.DatasetId
                    || reader.GetInt64(2) != checked((long)(options.Budget.LimitUsd * 1_000_000m)))
                    throw new InvalidOperationException("Existing dataset identity or budget differs; no schema changes permitted.");
            }
        }
        // This runs under the same exclusive schema lock, before any store can
        // perform DDL. A completed Job may be adopted/retried only on from/to.
        await using (var workers = lockConnection.CreateCommand())
        {
            workers.CommandText = "IF OBJECT_ID(N'core.worker_attempt',N'U') IS NOT NULL "
                + ActiveWorkerAttemptsQuery(registryFactory.Dialect) + "; ELSE SELECT 0;";
            if (Convert.ToInt32(await workers.ExecuteScalarAsync(ct)) != 0)
                throw new InvalidOperationException("Queued or running worker attempts prohibit schema initialization.");
        }
        var additionalProjects = new List<string>();
        if (options.Bootstrap.MigrationFromVersion is not null)
        {
            await using var preflight = lockConnection.CreateCommand();
            preflight.CommandText = "SELECT COUNT(*) FROM core.control_dataset WHERE singleton=1 AND dataset_id=@dataset AND budget_limit_microusd=@budget AND owner_instance_id IS NULL;";
            preflight.AddParam("@dataset", options.Controller.DatasetId);
            preflight.AddParam("@budget", checked((long)(options.Budget.LimitUsd * 1_000_000m)));
            if (Convert.ToInt32(await preflight.ExecuteScalarAsync(ct)) != 1)
                throw new InvalidOperationException("Schema upgrade requires the unchanged initialized dataset and budget without an owner.");
            preflight.Parameters.Clear();
            preflight.CommandText = "SELECT COUNT(*) FROM core.project WHERE id='forge' AND repo_url=@repo AND default_branch=@branch;";
            preflight.AddParam("@repo", options.Bootstrap.RepoUrl);
            preflight.AddParam("@branch", options.Bootstrap.DefaultBranch);
            if (Convert.ToInt32(await preflight.ExecuteScalarAsync(ct)) != 1)
                throw new InvalidOperationException("Existing Forge project differs from the immutable upgrade profile.");
            preflight.Parameters.Clear();
            preflight.CommandText = "SELECT MAX(version) FROM core.schema_version;";
            var coreVersion = Convert.ToInt32(await preflight.ExecuteScalarAsync(ct));
            preflight.CommandText = "SELECT MAX(version) FROM proj_forge.schema_version;";
            var workloadVersion = Convert.ToInt32(await preflight.ExecuteScalarAsync(ct));
            ValidateMigrationVersions(options.Bootstrap, coreVersion, workloadVersion);
            preflight.CommandText = "SELECT id FROM core.project WHERE id<>'forge';";
            await using (var reader = await preflight.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) additionalProjects.Add(reader.GetString(0));
            foreach (var projectId in additionalProjects)
            {
                var schema = ProjectStorageInitializer.Schema(projectId);
                preflight.CommandText = $"SELECT MAX(version) FROM [{schema}].schema_version;";
                ValidateMigrationVersions(options.Bootstrap, coreVersion, Convert.ToInt32(await preflight.ExecuteScalarAsync(ct)));
            }
        }
        var registry = new IssueStore(registryFactory);
        var leases = new ControlLeaseStore(registryFactory);
        var before = await leases.ReadAsync(options.Controller.DatasetId, ct);
        if (before?.OwnerInstanceId is not null)
            throw new InvalidOperationException("Stop/drain the existing controller before schema initialization.");
        await leases.InitializeDatasetAsync(options.Controller.DatasetId, options.Budget.LimitUsd, ct);
        var projects = new ProjectStore(registry);
        var existing = await projects.GetAsync("forge", ct);
        if (existing is not null && (existing.RepoUrl != options.Bootstrap.RepoUrl || existing.DefaultBranch != options.Bootstrap.DefaultBranch))
            throw new InvalidOperationException("Existing project identity differs from the immutable bootstrap profile.");
        var workload = ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.SchemaForProject("forge"));
        _ = new IssueStore(workload, "forge");
        var memory = new MemoryStore(workload);
        var completionKey = "bootstrap/complete/" + options.Controller.DatasetId;
        var completed = (await memory.RecallAsync(completionKey, ct)).Any(row => row.Body == "complete");
        if (!completed)
        {
            await projects.UpsertAsync(new("forge", "Forge", options.Bootstrap.RepoUrl, options.Bootstrap.DefaultBranch), ct);
            await projects.UpdateRolesAsync("forge", new Dictionary<string, int>
            { ["coredev"] = 1, ["clientdev"] = 1, ["reviewer"] = 1, ["qa"] = 1 }, ct);
            await projects.UpdateTerritoriesAsync("forge", new Dictionary<string, RoleTerritory>
            {
                ["coredev"] = new(["Agents/", "AgentTools/", "Core/", "Configuration/", "Dashboard/", "Orchestrator/",
                    "Projects/", "DeploymentPipeline/", "Messaging/", "Codebase/", "Reviewer/", "Forge.Core/", "tests/",
                    "tools/", "scripts/", "deploy/", "docs/", "agents/", ".github/", ".kilo/"], true),
                ["clientdev"] = new(["Forge.UI/", "tests/"], false)
            }, ct);
            await projects.UpdateVerifyCommandsAsync("forge", ["dotnet build Forge.sln -c Release", "dotnet test tests/Forge.Tests/Forge.Tests.csproj -c Release --no-build --filter \"FullyQualifiedName!~RealLlmIntegrationTests&FullyQualifiedName!~MafAgentRunnerLiveTests&FullyQualifiedName!~MeshyLiveSmokeTests\""], ct);
            var gates = new StageGates(memory);
            foreach (var stage in StageGates.All) await gates.HoldAsync(stage, ct);
        }
        if (!string.IsNullOrWhiteSpace(options.GitHub.Token))
        {
            var secrets = new SecretStore(registry, Program.CreateSecretProtection(options));
            if (await secrets.GetMetadataAsync("forge", SecretKinds.GitHubToken, ct) is null)
                await secrets.UpsertAsync(new("forge", SecretKinds.GitHubToken, System.Text.Encoding.UTF8.GetBytes(options.GitHub.Token)), ct);
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(options.Bootstrap.WorkerPrincipal, "^[a-zA-Z][a-zA-Z0-9_-]{0,127}$"))
            throw new InvalidOperationException("Worker database principal name is invalid.");
        await using (var grants = lockConnection.CreateCommand())
        {
            var principal = options.Bootstrap.WorkerPrincipal;
            grants.CommandText = $"GRANT SELECT ON SCHEMA::[core] TO [{principal}]; GRANT SELECT,INSERT,UPDATE,DELETE ON SCHEMA::[proj_forge] TO [{principal}];"
                + $"GRANT SELECT,UPDATE ON OBJECT::[core].[worker_attempt] TO [{principal}];"
                + $"GRANT SELECT,INSERT,UPDATE ON OBJECT::[core].[budget_reservation] TO [{principal}];"
                + $"GRANT SELECT,UPDATE ON OBJECT::[core].[budget_lock] TO [{principal}];"
                + $"REVOKE DELETE,INSERT ON OBJECT::[core].[worker_attempt] FROM [{principal}];"
                + $"REVOKE DELETE ON OBJECT::[core].[budget_reservation] FROM [{principal}];"
                + reconciliationPermissions
                + InitiativeWorkerPermissionsSql(principal);
            await grants.ExecuteNonQueryAsync(ct);
        }
        // Install even when the dataset was already initialized. Older controllers
        // retain these same memory keys, so downgrade does not remove enforcement.
        await ReviewAuthorityInitializer.InstallAsync(ForgeDb.SqlServer(options.Db.ConnectionString, "proj_forge", initializeSchema: false),
            options.Bootstrap.WorkerPrincipal, options.Bootstrap.ControllerPrincipal, ct);
        foreach (var projectId in additionalProjects)
        {
            var schema = ProjectStorageInitializer.Schema(projectId);
            await using (var migrated = new IssueStore(ForgeDb.SqlServer(options.Db.ConnectionString, schema), projectId)) { }
            await using var grants = lockConnection.CreateCommand();
            grants.CommandText = InitiativeWorkerPermissionsSql(options.Bootstrap.WorkerPrincipal, schema);
            await grants.ExecuteNonQueryAsync(ct);
            await ReviewAuthorityInitializer.InstallAsync(ForgeDb.SqlServer(options.Db.ConnectionString, schema, initializeSchema: false),
                options.Bootstrap.WorkerPrincipal, options.Bootstrap.ControllerPrincipal, ct);
        }
        if (!completed) await memory.RememberAsync(completionKey, "complete", ttlDays: null, ct);
        logs.CreateLogger("Forge.Initialize").LogInformation("Initialized dataset {Dataset}; project Forge, SQL schema {Version}; no dispatch started.",
            options.Controller.DatasetId, SqlServerMigrations.ExpectedVersion);
        return 0;
    }

    internal static string ActiveWorkerAttemptsQuery(ISqlDialect dialect)
        => $"SELECT COUNT(*) FROM {dialect.Table("worker_attempt")} WHERE state IN ('queued','running')";

    internal static void ValidateMigrationContract(BootstrapOptions bootstrap)
    {
        if (bootstrap.MigrationFromVersion is null && bootstrap.MigrationToVersion is null) return;
        if (bootstrap.MigrationFromVersion is not > 0 || bootstrap.MigrationToVersion != SqlServerMigrations.ExpectedVersion
            || bootstrap.MigrationFromVersion >= bootstrap.MigrationToVersion)
            throw new InvalidOperationException("Initializer upgrade versions must bind an earlier source to this binary's exact schema version.");
    }

    internal static void ValidateMigrationVersions(BootstrapOptions bootstrap, int coreVersion, int workloadVersion)
    {
        ValidateMigrationContract(bootstrap);
        if (bootstrap.MigrationFromVersion is null)
            throw new InvalidOperationException("Upgrade versions are required for schema preflight.");
        foreach (var version in new[] { coreVersion, workloadVersion })
            if (version != bootstrap.MigrationFromVersion && version != bootstrap.MigrationToVersion)
                throw new InvalidOperationException("Registry or Forge workload schema is outside the qualified additive transition.");
    }

    internal static string InitiativeWorkerPermissionsSql(string workerPrincipal, string schema = "proj_forge")
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(workerPrincipal, "^[a-zA-Z][a-zA-Z0-9_-]{0,127}$"))
            throw new InvalidOperationException("Worker database principal name is invalid.");
        _ = new SqlServerDialect(schema);
        // Worker tools may read approved context, but only the controller's
        // operator-facing approval path may change authority or ingestion receipts.
        // The existence guard keeps the schema-39 compatibility bridge usable.
        return string.Concat(new[] { "initiative_proposal", "initiative_revision", "initiative_ingestion", "initiative_lock",
                "sprint", "sprint_completion", "completion_triage", "completion_draft_claim", "completion_lock", "completion_criterion_evidence", "deployment", "deployment_environment_policy", "sprint_proposal_audit", "owned_artifact_history" }
            .Select(table => $"IF OBJECT_ID(N'{schema}.{table}',N'U') IS NOT NULL BEGIN "
                + $"GRANT SELECT ON OBJECT::[{schema}].[{table}] TO [{workerPrincipal}];"
                + $"DENY INSERT,UPDATE,DELETE ON OBJECT::[{schema}].[{table}] TO [{workerPrincipal}]; END;"));
    }
    internal static string BudgetReconciliationPermissionsSql(string controllerPrincipal, string workerPrincipal)
    {
        foreach (var principal in new[] { controllerPrincipal, workerPrincipal })
            if (!System.Text.RegularExpressions.Regex.IsMatch(principal, "^[a-zA-Z][a-zA-Z0-9_-]{0,127}$"))
                throw new InvalidOperationException("Explicit controller and worker database principal names are required and must be valid SQL identifiers.");
        if (string.Equals(controllerPrincipal, workerPrincipal, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Controller and worker database identities must be distinct.");

        // Object DENY overrides existing schema-level DML grants. REVOKE alone
        // would leave inherited runtime writes enabled. Only the initializer's
        // separate operator connection may append this accounting evidence.
        return string.Concat(new[] { controllerPrincipal, workerPrincipal }.SelectMany(principal =>
            new[] { "budget_reconciliation", "budget_reconciliation_request" }.Select(table =>
                $"GRANT SELECT ON OBJECT::[core].[{table}] TO [{principal}];"
                + $"DENY INSERT,UPDATE,DELETE ON OBJECT::[core].[{table}] TO [{principal}];")));
    }
}
