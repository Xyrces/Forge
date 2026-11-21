using Forge.Configuration;
using Forge.Core;
using Forge.Core.Control;
using Forge.Core.Db;

namespace Forge.Orchestrator.Control;

/// <summary>Additive security prerequisite; does not initialize data, migrate a schema,
/// change leases or interrupt the currently active controller.</summary>
internal static class ReviewAuthorityInitializer
{
    internal static async Task<int> RunAsync(AgentOptions options, CancellationToken ct)
    {
        _ = ControllerInitializer.BudgetReconciliationPermissionsSql(options.Bootstrap.ControllerPrincipal, options.Bootstrap.WorkerPrincipal);
        if (options.Bootstrap.MigrationFromVersion is not null || options.Bootstrap.MigrationToVersion is not null)
            throw new InvalidOperationException("Authority-only initialization cannot perform a schema migration.");
        var registry = ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.RegistrySchema, ForgeSchemaProfile.Core, initializeSchema: false);
        await ControllerHost.VerifySchemaAsync(registry, ct);
        if (await new ControlLeaseStore(registry).ReadAsync(options.Controller.DatasetId, ct) is null)
            throw new InvalidOperationException("Authority initialization requires the existing dataset identity.");
        await using var issues = new IssueStore(registry);
        await using var projects = new ProjectStore(issues);
        var registered = await projects.ListAsync(ct);
        if (registered.Count == 0) throw new InvalidOperationException("No initialized project is registered.");
        foreach (var project in registered)
        {
            var db = ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.SchemaForProject(project.Id), initializeSchema: false);
            await ControllerHost.VerifySchemaAsync(db, ct);
            await InstallAsync(db, options.Bootstrap.WorkerPrincipal, options.Bootstrap.ControllerPrincipal, ct);
        }
        return 0;
    }

    internal static async Task InstallAsync(IDbConnectionFactory db, string worker, string controller, CancellationToken ct)
    {
        _ = ControllerInitializer.BudgetReconciliationPermissionsSql(controller, worker);
        await MemoryAuthoritySql.InstallAsync(db, worker, controller, ct);
    }
}
