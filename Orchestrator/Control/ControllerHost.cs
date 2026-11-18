using System.Security.Cryptography;
using System.Text;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Control;
using Forge.Core.Db;
using Forge.Projects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Forge.Orchestrator.Control;

public static class ControllerHost
{
    public sealed record Activation(string OperationId, string? ExpectedPreviousOwner = null);

    /// <summary>Read-only deployed-state check: no schema initialization, ownership acquisition, or model generation.</summary>
    public static Task<int> CheckAsync(AgentOptions options, ILoggerFactory loggers, CancellationToken ct = default)
        => CheckAsync(options, loggers, token => ReadCheckStateAsync(options, token),
            (projects, token) => CheckGitHubAsync(options, loggers, projects, token), ct);

    // The production entrypoint and tests share orchestration; only external read boundaries vary.
    internal static async Task<int> CheckAsync(AgentOptions options, ILoggerFactory loggers,
        Func<CancellationToken, Task<(int Schema, IReadOnlyList<ProjectOptions> Projects)>> readState,
        Func<IReadOnlyList<ProjectOptions>, CancellationToken, Task<GitHubPreflight.Result>> checkGitHub,
        CancellationToken ct = default)
    {
        var logger = loggers.CreateLogger("Forge.Check");
        try
        {
            var (registryVersion, registered) = await readState(ct);
            var github = await checkGitHub(registered, ct);
            foreach (var message in github.Passed) logger.LogInformation("{Detail}", message);
            foreach (var message in github.Failed) logger.LogError("{Detail}", message);
            if (github.Failed.Count > 0) return 1;
            var config = Agents.LlmConfigAdapter.FromOptions(options.Llm);
            _ = config.Resolve(AgentType.CoreDev);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var models = config.Roles.Values.Append(new Agents.RoleModel(config.DefaultProvider,
                config.Providers.First(p => p.Name.Equals(config.DefaultProvider, StringComparison.OrdinalIgnoreCase)).DefaultModel));
            foreach (var model in models.Distinct())
            {
                var provider = config.Providers.First(p => p.Name.Equals(model.ProviderName, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(provider.ApiKey))
                {
                    logger.LogInformation("Model metadata probe skipped for {Provider}/{Model}: no inline key; no secret mutation or generation attempted", provider.Name, model.Model);
                    continue;
                }
                var probe = await Agents.ModelCatalogProbe.CheckAsync(http, provider, model.Model, ct);
                if (!probe.Success) throw new InvalidOperationException(probe.Detail);
                logger.LogInformation("{Detail}", probe.Detail);
            }
            logger.LogInformation("Read-only check passed: registry schema {Schema}, dataset {Dataset}, {Count} projects; no model generation", registryVersion, options.Controller.DatasetId, registered.Count);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Read-only deployment check failed");
            return 1;
        }
    }

    private static async Task<(int Schema, IReadOnlyList<ProjectOptions> Projects)> ReadCheckStateAsync(
        AgentOptions options, CancellationToken ct)
    {
        if (!options.Db.IsSqlServer || !Guid.TryParse(options.Controller.DatasetId, out _))
            throw new InvalidOperationException("Distributed check requires SQL Server and a stable dataset UUID.");
        var registry = ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.RegistrySchema,
            ForgeSchemaProfile.Core, initializeSchema: false);
        var registryVersion = await VerifySchemaAsync(registry, ct);
        if (await new ControlLeaseStore(registry).ReadAsync(options.Controller.DatasetId, ct) is null)
            throw new InvalidOperationException("Configured dataset is not initialized in this database.");
        await using var issues = new IssueStore(registry);
        await using var projects = new ProjectStore(issues);
        var registered = await projects.ListAsync(ct);
        if (registered.Count == 0) throw new InvalidOperationException("No project is registered in the initialized dataset.");
        foreach (var project in registered)
        {
            await VerifySchemaAsync(ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.SchemaForProject(project.Id),
                initializeSchema: false), ct);
            await MemoryAuthoritySql.VerifyAsync(ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.SchemaForProject(project.Id),
                initializeSchema: false), options.Bootstrap.WorkerPrincipal, ct);
        }
        return (registryVersion, registered.Select(project => new ProjectOptions
        {
            Id = project.Id,
            RepoUrl = project.RepoUrl
        }).ToArray());
    }

    private static async Task<GitHubPreflight.Result> CheckGitHubAsync(AgentOptions options,
        ILoggerFactory loggers, IReadOnlyList<ProjectOptions> projects, CancellationToken ct)
    {
        var registry = ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.RegistrySchema,
            ForgeSchemaProfile.Core, initializeSchema: false);
        await using var issues = new IssueStore(registry);
        // Read existing ciphertext only: check must never create a new keyring or encryption key.
        var protection = DataProtectionProvider.Create(new DirectoryInfo(options.Controller.KeyringPath),
            builder => builder.SetApplicationName("forge.secrets").DisableAutomaticKeyGeneration());
        var secrets = new SecretStore(issues, protection, loggers.CreateLogger<SecretStore>());
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        return await new GitHubPreflight(http, new GitHubPreflightCredentials(options.GitHub, secrets,
                id => ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.SchemaForProject(id), initializeSchema: false)))
            .CheckAsync(projects, options.GitHub, ct);
    }

    public static async Task<int> RunAsync(AgentOptions options, ILoggerFactory loggers,
        Func<Action, Func<CancellationToken, Task>, CancellationToken, Task> run)
    {
        Validate(options);
        options.Db.AutoMigrate = false;
        var factory = ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.RegistrySchema,
            ForgeSchemaProfile.Core, initializeSchema: false);
        var token = (await File.ReadAllTextAsync(options.Controller.TokenFile)).Trim();
        if (token.Length < 32) throw new InvalidOperationException("Controller authentication token must contain at least 32 characters.");
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + token));
        await using var runtime = new ControllerRuntime(new ControlLeaseStore(factory), options.Controller.DatasetId,
            options.Controller.InstanceId, run, loggers.CreateLogger<ControllerRuntime>(),
            TimeSpan.FromSeconds(Math.Max(1, options.Controller.HeartbeatSeconds)));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(options.Controller.ListenUrl);
        var app = builder.Build();
        app.MapGet("/healthz", async (CancellationToken ct) =>
        {
            try { await VerifyReviewAuthorityAsync(options, ct); return Results.Ok(new { live = true, reviewAuthorityReady = true }); }
            catch (InvalidOperationException) { return Results.StatusCode(503); }
        });
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/controller"))
            {
                var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
                if (!CryptographicOperations.FixedTimeEquals(tokenHash, supplied))
                { context.Response.StatusCode = 401; return; }
            }
            await next();
        });
        app.MapGet("/api/controller/status", async (CancellationToken ct) =>
        {
            var schemaVersion = await VerifySchemaAsync(factory, ct);
            var status = (await runtime.StatusAsync(ct)) with { SchemaVersion = schemaVersion };
            try { await VerifyReviewAuthorityAsync(options, ct); }
            catch (InvalidOperationException) { return Results.Ok(status with { Ready = false, AdmissionOpen = false }); }
            return Results.Ok(status);
        });
        app.MapPost("/api/controller/activate", async (Activation activation, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(activation.OperationId)) return Results.BadRequest();
            await VerifySchemaAsync(factory, ct);
            await VerifyReviewAuthorityAsync(options, ct);
            return await runtime.ActivateAsync(activation.OperationId, activation.ExpectedPreviousOwner, ct)
                ? Results.Accepted(value: await runtime.StatusAsync(ct))
                : Results.Conflict(new { error = "ownership_conflict" });
        });
        app.MapPost("/api/controller/merge-fence", async (Activation activation, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(activation.OperationId)) return Results.BadRequest();
            await VerifySchemaAsync(factory, ct);
            await using var registryIssues = new IssueStore(factory);
            await using var projects = new ProjectStore(registryIssues);
            var registered = await projects.ListAsync(ct);
            if (registered.Count == 0) return Results.Conflict(new { error = "no_registered_projects" });
            foreach (var project in registered)
            {
                var projectDb = ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.SchemaForProject(project.Id), initializeSchema: false);
                await VerifySchemaAsync(projectDb, ct);
                await using var memory = new MemoryStore(projectDb);
                await new DeploymentMergeFence(memory).HoldAsync(activation.OperationId, ct);
            }
            return Results.Ok(new { held = true, operationId = activation.OperationId, projects = registered.Count });
        });
        app.MapPost("/api/controller/drain", async (Activation activation, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(activation.OperationId)) return Results.BadRequest();
            await runtime.DrainAsync(ct);
            return Results.Accepted(value: await runtime.StatusAsync(ct));
        });
        app.MapPost("/api/controller/workers/{id}/cancel", async (string id, CancellationToken ct) =>
        {
            await runtime.EnsureAdmissionAsync(ct);
            var attempts = new Core.Execution.WorkerAttemptStore(factory);
            var attempt = await attempts.GetAsync(id, ct);
            if (attempt is null || attempt.DatasetId != options.Controller.DatasetId) return Results.NotFound();
            await attempts.RequestCancellationAsync(id, ct);
            return Results.Accepted(value: new { attemptId = id, cancellationRequested = true });
        });
        try { await app.RunAsync(); }
        finally { await app.DisposeAsync(); }
        return 0;
    }

    public static async Task<int> VerifySchemaAsync(IDbConnectionFactory factory, CancellationToken ct)
    {
        await using var connection = await factory.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT MAX(version) FROM {factory.Dialect.Table("schema_version")}";
        var observed = await command.ExecuteScalarAsync(ct);
        var version = observed is null or DBNull ? 0 : Convert.ToInt32(observed);
        // Version42 is a persisted-format boundary. The strict target must not
        // inherit its maintenance ancestor's schema41 readiness allowance.
        if (!RuntimeSchemaCompatibility.CanRead(version))
            throw new InvalidOperationException("Database schema is outside this release's tested compatibility version.");
        return version;
    }

    internal static async Task VerifyReviewAuthorityAsync(AgentOptions options, CancellationToken ct)
    {
        var registry = ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.RegistrySchema, ForgeSchemaProfile.Core, initializeSchema: false);
        await using var issues = new IssueStore(registry);
        await using var projects = new ProjectStore(issues);
        var registered = await projects.ListAsync(ct);
        if (registered.Count == 0) throw new InvalidOperationException("No initialized project is registered.");
        foreach (var project in registered)
            await MemoryAuthoritySql.VerifyAsync(ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.SchemaForProject(project.Id), initializeSchema: false),
                options.Bootstrap.WorkerPrincipal, ct);
    }

    public static void Validate(AgentOptions options)
    {
        if (!options.Db.IsSqlServer) throw new InvalidOperationException("The distributed Forge runtime requires SQL Server.");
        if (!Guid.TryParse(options.Controller.DatasetId, out _))
            throw new InvalidOperationException("A stable UUID dataset identity is required.");
        if (options.Controller.Mode == "standby" && string.IsNullOrWhiteSpace(options.Controller.InstanceId))
            throw new InvalidOperationException("Controller instance identity is required.");
    }
}
