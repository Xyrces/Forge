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
                + $"REVOKE DELETE ON OBJECT::[core].[budget_reservation] FROM [{principal}];";
            await grants.ExecuteNonQueryAsync(ct);
        }
        if (!completed) await memory.RememberAsync(completionKey, "complete", ttlDays: null, ct);
        logs.CreateLogger("Forge.Initialize").LogInformation("Initialized dataset {Dataset}; project Forge, SQL schema {Version}; no dispatch started.",
            options.Controller.DatasetId, SqlServerMigrations.ExpectedVersion);
        return 0;
    }
}
