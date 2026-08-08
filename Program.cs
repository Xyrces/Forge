using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Db;
using Forge.Dashboard;
using Forge.Orchestrator.Slots;
using Forge.Projects;
using Forge.Orchestrator;
using Forge.Reviewer;

namespace Forge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var mode = ParseMode(args);
        var configPath = ParseArg(args, "--config");

        AgentOptions options;
        try
        {
            options = OptionsLoader.Load(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Configuration error: {ex.Message}");
            return 1;
        }

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss.fff ";
                // Scopes ON: the dispatch correlation id (v30) rides
                // every log line a dispatch writes — the postmortem
                // join key (operator 2026-08-01).
                o.IncludeScopes = true;
            });
            // Always log to a file so the SCM-hosted service has
            // something visible in C:\ProgramData\Forge\forge-scm.log
            // when stdout is swallowed by the SCM. The diagnostic
            // MafAgentRunner logs use this to debug the silent-agent
            // bug. Cheap (rolling size), and the file only exists when
            // the service is running.
            var logFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Forge", "forge-scm.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
                builder.AddProvider(new Forge.Core.ForgeFileLoggerProvider(logFile));
            }
            catch
            {
                // best-effort: if we can't write the log, console still works
            }
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger("Forge");

        if (mode == CliMode.Status)
            return await PrintStatusAsync(options, loggerFactory);

        if (mode == CliMode.Check)
            return await RunPreflightCheckAsync(options, loggerFactory);

        if (mode == CliMode.Enqueue)
            return await EnqueueTaskAsync(args, options, loggerFactory);

if (mode == CliMode.DashboardOnly)
            return await RunDashboardOnlyAsync(options, loggerFactory, logger);

        if (mode == CliMode.WorktreeSmoke)
            return await RunWorktreeSmokeAsync(args, options, loggerFactory, logger);

        if (mode == CliMode.RecoverDryRun)
            return await RunRecoverAsync(options, loggerFactory, logger, dryRun: true);

        if (mode == CliMode.RecoverAndStart)
            return await RunRecoverAsync(options, loggerFactory, logger, dryRun: false);

        if (mode == CliMode.MigrateDb)
            return await RunMigrateDbAsync(args, options, loggerFactory);

        if (mode == CliMode.InitAzureSql)
            return await RunInitAzureSqlAsync(args, options, loggerFactory);

        // systemd hosting. When launched by systemd (Type=notify),
        // stdin/stdout are not a console and Console.CancelKeyPress
        // never fires on stop -- systemd instead expects the process
        // to honor a stop signal delivered through the generic host's
        // IHostApplicationLifetime. INVOCATION_ID is set by systemd
        // for every service unit process; its presence is the
        // canonical "we were launched by systemd" signal. This is
        // a no-op for every other invocation in this switch.
        if (IsSystemdService())
            return await RunAsSystemdServiceAsync(options, loggerFactory, logger);

        return await RunOrchestratorAsync(options, loggerFactory, logger);
    }

    private static bool IsSystemdService()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID"));
    }

    // Wraps RunOrchestratorAsync in a minimal generic Host so systemd
    // sees a well-behaved Type=notify service: READY=1 is published
    // once the hosted service starts, and a systemd stop request
    // cancels stoppingToken, which we forward into
    // RunOrchestratorAsync's own shutdown token. Everything else
    // (dashboard, dispatch loop, schedulers) is unchanged -- this is
    // purely a lifecycle adapter, not a parallel code path.
    private static async Task<int> RunAsSystemdServiceAsync(
        AgentOptions options, ILoggerFactory loggerFactory, ILogger logger)
    {
        var exitCode = 0;
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSystemd();
        builder.Services.AddHostedService(_ =>
            new OrchestratorHostedService(options, loggerFactory, logger, code => exitCode = code));
        using var host = builder.Build();
        await host.RunAsync();
        return exitCode;
    }

    // Adapts RunOrchestratorAsync (a plain cancellable async method,
    // not an IHostedService) to BackgroundService so it can live
    // inside the generic host built above. ExecuteAsync's stoppingToken
    // is cancelled by the host when the SCM delivers a stop request.
    private sealed class OrchestratorHostedService(
        AgentOptions options, ILoggerFactory loggerFactory, ILogger logger, Action<int> onExit)
        : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var code = await RunOrchestratorAsync(options, loggerFactory, logger, stoppingToken);
            onExit(code);
        }
    }

    private enum CliMode { Run, Once, Status, Enqueue, DashboardOnly, WorktreeSmoke, Check, RecoverDryRun, RecoverAndStart, MigrateDb, InitAzureSql }

    private static CliMode ParseMode(string[] args)
    {
        if (args.Any(a => a == "--status")) return CliMode.Status;
        if (args.Any(a => a == "--enqueue-task")) return CliMode.Enqueue;
        if (args.Any(a => a == "--dashboard-only")) return CliMode.DashboardOnly;
        if (args.Any(a => a == "--worktree-smoke")) return CliMode.WorktreeSmoke;
        if (args.Any(a => a == "--once")) return CliMode.Once;
        if (args.Any(a => a == "--check")) return CliMode.Check;
        if (args.Any(a => a == "--recover")) return CliMode.RecoverDryRun;
        if (args.Any(a => a == "--recover-and-start")) return CliMode.RecoverAndStart;
        if (args.Any(a => a == "--migrate-db")) return CliMode.MigrateDb;
        if (args.Any(a => a == "--init-azure-sql")) return CliMode.InitAzureSql;
        return CliMode.Run;
    }

    private static string? ParseArg(string[] args, string key)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == key && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith($"{key}=", StringComparison.Ordinal))
                return args[i][(key.Length + 1)..];
        }
        return null;
    }

    private static async Task<int> PrintStatusAsync(AgentOptions options, ILoggerFactory loggerFactory)
    {
        try
        {
            var (projects, dbByProject, _, projectStore, cloner, secretStore) = BuildProjectBootstrap(options, loggerFactory);
            if (projects.Count == 0)
            {
                Console.Error.WriteLine("No projects registered. Add one via POST /api/projects or the dashboard Projects page.");
                return 1;
            }
            var primary = projects[0];
            await using var issues = new IssueStore(FactoryFor(options.Db, primary.Id, dbByProject[primary.Id]));
            var all = await issues.ListAsync(new IssueFilter(), CancellationToken.None);
            Console.WriteLine($"Project   : {primary.Id} ({primary.Root})");
            Console.WriteLine($"Pending:    {all.Count(i => i.Status == IssueStatus.Pending)}");
            Console.WriteLine($"InProgress: {all.Count(i => i.Status == IssueStatus.InProgress)}");
            Console.WriteLine($"Completed:  {all.Count(i => i.Status == IssueStatus.Completed)}");
            Console.WriteLine($"Failed:     {all.Count(i => i.Status == IssueStatus.Failed)}");
            Console.WriteLine($"Blocked:    {all.Count(i => i.Status == IssueStatus.Blocked)}");
            Console.WriteLine($"Total:      {all.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Status error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> EnqueueTaskAsync(string[] args, AgentOptions options, ILoggerFactory loggerFactory)
    {
        var title = ParseArg(args, "--enqueue-task")
            ?? $"task-{Guid.NewGuid().ToString("N")[..8]}";
        var type = ParseArg(args, "--task-type") ?? "ecs";
        var description = ParseArg(args, "--task-desc") ?? "no description";
        var branch = ParseArg(args, "--branch") ?? $"agent/{title}";

        var (projects, dbByProject, _, projectStore, cloner, secretStore) = BuildProjectBootstrap(options, loggerFactory);
        if (projects.Count == 0)
        {
            Console.Error.WriteLine("No projects registered. Add one via POST /api/projects or the dashboard Projects page.");
            return 1;
        }
        var primary = projects[0];
        var issues = new IssueStore(FactoryFor(options.Db, primary.Id, dbByProject[primary.Id]));


        // Stable, caller-supplied id: prefer explicit --task-id, else slugify the title.
        var explicitId = ParseArg(args, "--task-id");
        var shortId = explicitId ?? Slugify(title);
        if (string.IsNullOrEmpty(shortId)) shortId = Guid.NewGuid().ToString("N")[..8];

        var metadata = new Dictionary<string, object> { ["branch"] = branch };

        try
        {
            var issue = await issues.CreateAsync(new NewIssue(
                Type: type,
                Title: title,
                Description: description,
                Metadata: metadata), CancellationToken.None);

            // Override the auto id with the requested short id so callers can
            // reference their own chosen ids. Easiest is to rename the row.
            if (issue.ShortId != shortId)
            {
                // IssueStore assigned task-N; we just record the requested id
                // as an alias under task-<shortId>. For P0 we use the
                // auto-assigned id and warn if it differs.
                Console.Error.WriteLine($"Warning: --task-id ignored (IssueStore assigned {issue.Id}). Use the assigned id.");
            }

            Console.WriteLine($"Enqueued {issue.Id} ({type}): {title}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Enqueue failed: {ex.Message}");
            return 1;
        }
    }

    private static string Slugify(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        var cleaned = sb.ToString().Trim('-');
        return cleaned.Length == 0 || cleaned.Length > 40 ? cleaned[..Math.Min(40, cleaned.Length)] : cleaned;
    }

    /// <summary>
    /// Build the shared per-(project, role) concurrency slot table.
    /// Caps come from each project's persisted roles (roles_json) with
    /// <see cref="DefaultProjectRoles"/> as the fallback. The same
    /// instance is handed to the orchestrator (dispatch enforcement)
    /// and the dashboard (live meters + role-cap edits).
    /// </summary>
    internal static Orchestrator.Slots.SlotTable BuildSlotTable(IReadOnlyList<ProjectOptions> projects)
    {
        var slots = new Orchestrator.Slots.SlotTable();
        foreach (var p in projects)
        {
            foreach (var role in Agents.RoleAgentRegistry.AllSlotRoles)
            {
                var max = DefaultProjectRoles.MaxFor(p.Roles, role);
                slots.Configure(p.Id, role, max);
            }
        }
        return slots;
    }

    /// <summary>
    /// Resolves Forgesystem options + runs the per-project bootstrap
    /// (create root dir, init git repo, allocate state DB). Returns the
    /// finalised project list (with Root rewritten to the bootstrap
    /// directory) + a per-project DB path map. Idempotent.
    /// </summary>
    internal static (IReadOnlyList<ProjectOptions> Projects,
                    Dictionary<string, string> IssuesDbByProject,
                    string DataRoot,
                     Core.ProjectStore ProjectStore,
                     Projects.ProjectCloner Cloner,
                     Core.SecretStore SecretStore)
        BuildProjectBootstrap(AgentOptions options, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Forge.Bootstrap");
        var dataRoot = ForgesystemPaths.ResolveDataRoot(options.Forgesystem.DataRoot);
        Directory.CreateDirectory(dataRoot);
        logger.LogInformation("Forgesystem data root: {Root}", dataRoot);

        // ProjectStore + ProjectCloner + SecretStore all need an
        // IssueStore to anchor their SQLite connection. The "primary"
        // project (first in the registry) gets its IssueStore
        // created here, then we pass it to the registry loader +
        // cloner. Other projects get their IssueStore allocated
        // inside the loop below. The SecretStore piggy-backs on
        // the same SQLite file (the `secret` table lives in the
        // default project's DB; cross-project secret rows are
        // scoped by project_id).
        var primaryDbPath = ForgesystemPaths.IssuesDb(dataRoot, "default");
        Directory.CreateDirectory(Path.GetDirectoryName(primaryDbPath)!);
        var primaryStore = new Core.IssueStore(
            Core.Db.ForgeDb.ForRegistry(options.Db.IsSqlServer, options.Db.ConnectionString, primaryDbPath));
        var projectStore = new Core.ProjectStore(primaryStore);
        var secretStore = new Core.SecretStore(
            primaryStore,
            Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create("forge.secrets"),
            loggerFactory.CreateLogger<Core.SecretStore>());

        var cloner = new Projects.ProjectCloner(dataRoot, null);
        var registry = ProjectRegistryLoader.LoadAsync(projectStore, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (registry.Count == 0)
        {
            logger.LogWarning(
                "No projects registered. Add one via the dashboard Projects page (POST /api/projects) before dispatch will run work.");
        }
        else
        {
            logger.LogInformation("Loaded {Count} project(s) from SQLite.", registry.Count);
        }

        var finalised = new List<ProjectOptions>(registry.Count);
        var dbByProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in registry)
        {
            // Per-project github_token secret overrides the global PAT
            // for the boot-time clone (same rule as the endpoints).
            var effectiveGitHub = Projects.GitHubTokenResolver
                .ResolveAsync(p.Id, options.GitHub, secretStore, CancellationToken.None)
                .GetAwaiter().GetResult();
            var bootstrap = new Projects.ProjectBootstrap(dataRoot, cloner, effectiveGitHub, null);
            var result = bootstrap.EnsureProject(p);
            finalised.Add(result.Project);
            dbByProject[result.Project.Id] = result.IssuesDbPath;
            logger.LogInformation(
                "Project '{Id}' root={Root} state={State} created={Created} gitInit={GitInit} cloned={Cloned}",
                result.Project.Id, result.Project.Root, result.StateDirectory,
                result.Created, result.InitializedAsGitRepo, result.ClonedFromRemote);
        }

        return (finalised, dbByProject, dataRoot, projectStore, cloner, secretStore);
    }

    /// <summary>
    /// <c>--migrate-db --target sqlserver [--connection-string "..."]
    /// [--include-open-work] [--reset]</c>: one-shot SQLite -> Azure SQL
    /// state migration (registry + secrets ciphertext + memory keys;
    /// open work only with the flag). Idempotent; prints a per-table
    /// verification report. The service must be stopped first — the
    /// migration reads the SQLite files read-only but the operator
    /// model is "cut over while nothing writes".
    /// </summary>
    private static async Task<int> RunMigrateDbAsync(string[] args, AgentOptions options, ILoggerFactory loggerFactory)
    {
        var target = ParseArg(args, "--target") ?? "sqlserver";
        if (!string.Equals(target, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"--target '{target}' is not supported (only 'sqlserver').");
            return 1;
        }
        var connectionString = ParseArg(args, "--connection-string") ?? options.Db.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("No target connection string. Pass --connection-string or set db.connectionString in config.");
            return 1;
        }
        var includeOpenWork = args.Any(a => a == "--include-open-work");
        var reset = args.Any(a => a == "--reset");

        var (projects, dbByProject, dataRoot, _, _, _) = BuildProjectBootstrap(options, loggerFactory);
        var sources = projects
            .Select(p => new Core.Db.StateMigrator.ProjectSource(
                p.Id,
                dbByProject[p.Id],
                Path.Combine(Path.GetDirectoryName(dbByProject[p.Id])!, "memory.db")))
            .ToList();
        // The registry anchor ('default') may not be a registered
        // project — always include it so project/secret rows migrate.
        if (sources.All(sx => !string.Equals(sx.ProjectId, "default", StringComparison.OrdinalIgnoreCase)))
        {
            var defaultPath = ForgesystemPaths.IssuesDb(dataRoot, "default");
            if (File.Exists(defaultPath))
            {
                sources.Insert(0, new Core.Db.StateMigrator.ProjectSource(
                    "default", defaultPath,
                    Path.Combine(Path.GetDirectoryName(defaultPath)!, "memory.db")));
            }
        }

        if (reset)
        {
            Console.WriteLine($"Resetting {sources.Count} project schema(s) on the target...");
            await Core.Db.StateMigrator.ResetAsync(
                connectionString, sources.Select(sx => sx.ProjectId).ToList());
            Console.WriteLine("Reset complete.");
        }

        Console.WriteLine($"Migrating {sources.Count} project source(s) -> Azure SQL (includeOpenWork={includeOpenWork})");
        var report = await Core.Db.StateMigrator.MigrateAsync(
            sources, connectionString,
            new Core.Db.StateMigrator.MigrateOptions(IncludeOpenWork: includeOpenWork));
        foreach (var line in report) Console.WriteLine($"  {line}");
        Console.WriteLine("Migration complete. Verify with --check after flipping db.provider=sqlserver.");
        return 0;
    }

    /// <summary>
    /// <c>--init-azure-sql [--connection-string "..."] [--mi-name forge-mi]</c>:
    /// one-shot Azure SQL provisioning, run as the Entra admin. Creates the
    /// contained database user for the user-assigned managed identity
    /// (db_owner — the app does DDL at startup) so the future ACA/AKS
    /// cutover is a pure config change. Idempotent.
    /// </summary>
    private static async Task<int> RunInitAzureSqlAsync(string[] args, AgentOptions options, ILoggerFactory loggerFactory)
    {
        var connectionString = ParseArg(args, "--connection-string") ?? options.Db.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("No target connection string. Pass --connection-string or set db.connectionString in config.");
            return 1;
        }
        var miName = ParseArg(args, "--mi-name") ?? "forge-mi";
        var factory = Core.Db.ForgeDb.SqlServer(connectionString, "dbo");
        await using var conn = await factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @mi)
                CREATE USER [{miName}] FROM EXTERNAL PROVIDER;
            IF NOT EXISTS (SELECT 1 FROM sys.database_role_members rm
                           JOIN sys.database_principals r ON rm.role_principal_id = r.principal_id
                           JOIN sys.database_principals m ON rm.member_principal_id = m.principal_id
                           WHERE r.name = 'db_owner' AND m.name = @mi)
                ALTER ROLE db_owner ADD MEMBER [{miName}];
            SELECT name, type_desc FROM sys.database_principals WHERE name = @mi;
            """;
        cmd.AddParam("@mi", miName);
        await using var rd = await cmd.ExecuteReaderAsync();
        var found = false;
        while (await rd.ReadAsync())
        {
            found = true;
            Console.WriteLine($"  [ok] contained user: {rd.GetString(0)} ({rd.GetString(1)})");
        }
        if (!found)
        {
            Console.Error.WriteLine($"  fail: contained user '{miName}' not present after init.");
            return 1;
        }
        Console.WriteLine($"  [ok] '{miName}' is db_owner (idempotent)");
        return 0;
    }

    /// <summary>
    /// Appended to --check DB failures on the SQL Server provider:
    /// the dominant failure mode is an expired az CLI session
    /// (Active Directory Default resolves via AzureCliCredential on
    /// this machine), so name the remediation explicitly.
    /// </summary>
    private static string DbAuthHint(Exception ex, AgentOptions options)
    {
        if (!options.Db.IsSqlServer) return "";
        var msg = ex.ToString();
        if (msg.Contains("az login", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("AzureCliCredential", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("DefaultAzureCredential", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("AADSTS", StringComparison.OrdinalIgnoreCase))
        {
            return " [hint: Entra token acquisition failed — run `az login` (this machine) or check the managed-identity assignment (Azure)]";
        }
        return "";
    }

    /// <summary>
    /// Resolve the connection factory for one project's logical state
    /// database. SQLite: per-project .db file at <paramref name="sqlitePath"/>
    /// (settings match IssueStore's canonical builder). SQL Server: the
    /// shared database, schema-per-project (proj_&lt;id&gt;) — the first
    /// IssueStore construction against a schema creates it and all tables.
    /// </summary>
    internal static Core.Db.IDbConnectionFactory FactoryFor(DbOptions db, string projectId, string sqlitePath)
        => Core.Db.ForgeDb.ForProject(db.IsSqlServer, db.ConnectionString, projectId, sqlitePath);

    private static async Task<int> RunDashboardOnlyAsync(
        AgentOptions options, ILoggerFactory loggerFactory, ILogger logger)
    {
        var (dashboardOnlyProjects, dbByProject, dataRoot, projectStore, cloner, secretStore) = BuildProjectBootstrap(options, loggerFactory);
        var defaultDb = dashboardOnlyProjects.Count > 0
            ? dbByProject[dashboardOnlyProjects[0].Id]
            : throw new InvalidOperationException("At least one project is required to run the dashboard.");
        var issues = new IssueStore(FactoryFor(options.Db, dashboardOnlyProjects[0].Id, defaultDb));
        var registryIssues = new IssueStore(
            Core.Db.ForgeDb.ForRegistry(options.Db.IsSqlServer, options.Db.ConnectionString, defaultDb));
        var agents = new AgentStore(registryIssues);
        var skills = new SkillStore(registryIssues);
        var sprints = new SprintStore(issues);
        var messageBus = new AgentMessageBus();
        var eventBus = new InMemoryDashboardEventBus();
        var dashboardOnlyFactory = new ProjectContextFactory(projectStore, dataRoot, dbByProject,
            (pid, path) => FactoryFor(options.Db, pid, path));
        var dashboardOnlySlots = new SlotTable();
        var _roleFiller = new[] { "coredev", "clientdev", "reviewer", "intake", "designer", "artist", "groomer", "orchestrator" };
        foreach (var pp in dashboardOnlyProjects)
            foreach (var rr in _roleFiller)
                dashboardOnlySlots.Configure(pp.Id, rr, DefaultProjectRoles.MaxFor(pp.Roles, rr));
var dashboard = new DashboardHost(
            options.Dashboard, options.Headroom, issues, agents, skills, sprints, messageBus, eventBus,
            loggerFactory.CreateLogger<DashboardHost>(),
            projectFactory: dashboardOnlyFactory,
            slots: dashboardOnlySlots,
            projectStore: projectStore,
            projectCloner: cloner,
            githubOptions: options.GitHub,
            gateOptions: options.Gates,
            secretStore: secretStore);

        using var shutdownCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            logger.LogWarning("SIGINT received; cancelling...");
            e.Cancel = true;
            shutdownCts.Cancel();
        };

        try
        {
            await dashboard.StartAsync(shutdownCts.Token);
            logger.LogInformation("Dashboard running. Ctrl+C to stop.");
            await Task.Delay(Timeout.Infinite, shutdownCts.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            try { await dashboard.StopAsync(); } catch { }
        }
    }

    private static async Task<int> RunWorktreeSmokeAsync(
        string[] args, AgentOptions options, ILoggerFactory loggerFactory, ILogger logger)
    {
        var taskId = ParseArg(args, "--worktree-smoke")
            ?? $"smoke-{Guid.NewGuid().ToString("N")[..8]}";
        var worktrees = new GitWorktreeService(options.Workspace, loggerFactory.CreateLogger<GitWorktreeService>());
        try
        {
            Console.WriteLine($"Workspace root: {worktrees.WorkspaceRoot}");
            Console.WriteLine($"Worktree root : {worktrees.WorktreeRoot}");
            Console.WriteLine($"Default branch: {worktrees.DefaultBranch}");
            Console.WriteLine();
            Console.WriteLine($"[1/5] Creating worktree for task '{taskId}'...");
            var path = await worktrees.CreateAsync(taskId, worktrees.DefaultBranch);
            Console.WriteLine($"      -> {path}");

            Console.WriteLine("[2/5] Verifying worktree exists in git...");
            var listed = await worktrees.WorktreeExistsAsync(path);
            Console.WriteLine($"      -> listed={listed}");

            // Probe for any *.sln in the worktree (we don't assume
            // a particular target project name; the orchestrator
            // is project-agnostic). First match wins.
            var sln = Directory.EnumerateFiles(path, "*.sln", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            var headProbe = sln ?? Path.Combine(path, "no-sln");
            Console.WriteLine($"[3/5] Probe: {headProbe} exists = {File.Exists(headProbe)}");

            Console.WriteLine("[4/5] Writing + committing a marker file...");
            var marker = Path.Combine(path, ".ph-smoke");
            await File.WriteAllTextAsync(marker, $"smoke at {DateTime.UtcNow:O}");
            var commit = await worktrees.CommitAllAsync(path, $"smoke({taskId}): worktree smoke");
            Console.WriteLine($"      -> outcome={commit.Outcome} msg={commit.Message.Split('\n').First()}");

            Console.WriteLine("[5/5] Push dry-run (no actual push; verifies config + remote)...");
            var headSha = await worktrees.GetHeadShaAsync(path);
            Console.WriteLine($"      -> head sha: {headSha}");
            var stats = await worktrees.GetDiffStatsAsync(path, worktrees.DefaultBranch);
            Console.WriteLine($"      -> diff: {stats.Summary}");

            Console.WriteLine();
            Console.WriteLine("Smoke OK. Worktree and marker branch left in place.");
            Console.WriteLine($"Clean up with: git -C \"{worktrees.WorkspaceRoot}\" worktree remove --force \"{path}\" && git -C \"{worktrees.WorkspaceRoot}\" branch -D agent/{taskId}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Smoke FAILED: {ex.GetType().Name}: {ex.Message}");
Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    /// <summary>
    /// P4 Stage A — StartupRecovery CLI.
    ///
    /// <c>--recover</c>: dry-run. Reports what the recoverer
    /// WOULD do (issues scanned / replayed / failed) and
    /// exits 0. No side-effects on the issue store, no PR
    /// opens, no pushes. Use this to verify the recoverer
    /// before running a real restart.
    ///
    /// <c>--recover-and-start</c>: same as <c>--recover</c> but
    /// the side-effects run for real (push, PR open, worktree
    /// cleanup). After the recoverer finishes, the orchestrator
    /// continues with the normal dispatch loop.
    ///
    /// The full orchestrator boots in the second branch;
    /// --recover exits as soon as the sweep is done.
    /// </summary>
    internal static GitHubService BuildGitHubService(Configuration.GitHubOptions options, ILogger<GitHubService> logger)
    {
        if (string.Equals(options.Mode, "Local", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.LocalRemotePath))
                throw new InvalidOperationException(
                    "github.mode=Local requires github.localRemotePath to point at a bare git repository.");
            logger.LogInformation(
                "GitHubService: using LocalGitHubService against bare remote at {Path}",
                options.LocalRemotePath);
            return new LocalGitHubService(options.LocalRemotePath, options.Owner, options.Repo);
        }
        return new GitHubService(options);
    }

    private static async Task<int> RunRecoverAsync(
        AgentOptions options, ILoggerFactory loggerFactory, ILogger logger, bool dryRun)
    {
        try
        {
            var failures = new List<string>();
            var (projects, dbByProject, _, projectStore, cloner, secretStore) = BuildProjectBootstrap(options, loggerFactory);
            if (projects.Count == 0)
            {
                Console.Error.WriteLine("No projects registered. Add one via POST /api/projects or the dashboard Projects page.");
                return 1;
            }
            var primary = projects[0];
            var primaryDb = dbByProject[primary.Id];
            var stateDir = Path.GetDirectoryName(primaryDb)!;
            var primaryFactory = FactoryFor(options.Db, primary.Id, primaryDb);
            await using var issues = new IssueStore(primaryFactory);
            var recoveryReports = new RecoveryReportStore(primaryFactory);
            var worktrees = new GitWorktreeService(
                new WorkspaceOptions
                {
                    Root = primary.Root,
                    WorktreeRoot = options.Workspace.WorktreeRoot,
                    DefaultBranch = options.Workspace.DefaultBranch,
                },
                loggerFactory.CreateLogger<GitWorktreeService>());
            var gitHub = BuildGitHubService(options.GitHub, loggerFactory.CreateLogger<GitHubService>());
            var recovery = new Orchestrator.StartupRecovery(
                issues, recoveryReports, worktrees,
                new Orchestrator.GitHubRecoveryAdapter(gitHub),
                new InMemoryDashboardEventBus(),
                loggerFactory.CreateLogger<Orchestrator.StartupRecovery>());

            Console.WriteLine($"P4 StartupRecovery: {(dryRun ? "DRY-RUN" : "side-effects enabled")}");
            Console.WriteLine($"  project   : {primary.Id} root={primary.Root}");
            Console.WriteLine($"  state dir : {stateDir}");
            Console.WriteLine();

            if (dryRun)
            {
                // Classify each candidate without writing any
                // side-effects. We re-implement the read path
                // here (no audit row written) so the operator
                // can see "what would happen" without leaving
                // a paper trail.
                var candidates = await issues.ListInProgressForRecoveryAsync();
                Console.WriteLine($"  scanned: {candidates.Count}");
                int replay = 0, failed = 0, leftAlone = 0;
                foreach (var issue in candidates)
                {
                    var d = recovery.Classify(issue);
                    var line = $"    {issue.Id}: {issue.DispatchCheckpoint?.ToDbValue() ?? "(none)"} -> {d.Action} ({d.Reason})";
                    Console.WriteLine(line);
                    if (d.Action == RecoveryAction.Replay) replay++;
                    else if (d.Action == RecoveryAction.Failed) failed++;
                    else leftAlone++;
                }
                Console.WriteLine();
                Console.WriteLine($"  dry-run totals: replay={replay} failed={failed} left_alone={leftAlone}");
                return 0;
            }

            var reportId = await recovery.RunAsync();
            var report = await recoveryReports.GetAsync(reportId);
            Console.WriteLine($"  recovery_report id: {reportId}");
            if (report is not null)
            {
                Console.WriteLine($"  scanned={report.IssuesScanned} replayed={report.IssuesReplayed} failed={report.IssuesFailed} duration={report.DurationMs}ms");
                if (!string.IsNullOrWhiteSpace(report.ActionsJson))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(report.ActionsJson);
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        var id = el.GetProperty("IssueId").GetString();
                        var action = el.GetProperty("Action").GetString();
                        var before = el.TryGetProperty("BeforeCheckpoint", out var b) && b.ValueKind != System.Text.Json.JsonValueKind.Null ? b.GetString() : "(none)";
                        var after = el.TryGetProperty("AfterCheckpoint", out var a) && a.ValueKind != System.Text.Json.JsonValueKind.Null ? a.GetString() : "(none)";
                        var err = el.TryGetProperty("Error", out var e) && e.ValueKind != System.Text.Json.JsonValueKind.Null ? e.GetString() : "";
                        Console.WriteLine($"    {id}: {before} -> {after} ({action}{(string.IsNullOrEmpty(err) ? "" : $", err={err}")})");
                    }
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static async Task<int> RunPreflightCheckAsync(AgentOptions options, ILoggerFactory loggerFactory)
    {
        var failures = new List<string>();
        var (projects, dbByProject, dataRoot, projectStore, cloner, secretStore) = BuildProjectBootstrap(options, loggerFactory);
        if (projects.Count == 0)
        {
            failures.Add("No projects registered. Add one via POST /api/projects or the dashboard Projects page.");
            foreach (var f in failures) Console.Error.WriteLine($"  fail: {f}");
            return 1;
        }
        var primary = projects[0];
        var primaryDb = dbByProject[primary.Id];
        var stateDir = Path.GetDirectoryName(primaryDb)!;

        Console.WriteLine("Pre-flight check for Forge");
        Console.WriteLine($"  data root : {dataRoot}");
        Console.WriteLine($"  project   : {primary.Id} root={primary.Root} state={stateDir}");
        Console.WriteLine();

        // 1. Workspace is now auto-scaffolded by the bootstrap; this
        //    only surfaces it. Operators who hand-supplied an empty
        //    workspace.root get a brand-new repo under the Forgesystem
        //    data root; operators who supplied an existing path get
        //    that path with a fresh git init if needed.
        if (string.IsNullOrWhiteSpace(primary.Root) || !Directory.Exists(primary.Root))
        {
            failures.Add($"project '{primary.Id}' root is missing after bootstrap: {primary.Root}");
        }
        else if (!Directory.Exists(Path.Combine(primary.Root, ".git")))
        {
            failures.Add($"project '{primary.Id}' root is not a git repo after bootstrap: {primary.Root}");
        }
        else
        {
            Console.WriteLine($"  [ok] project '{primary.Id}' is a git repo at {primary.Root}");
        }

        // 2. State DB opens + schema version is current (both
        //    providers; SQL Server also validates the Entra token
        //    path and reports round-trip latency).
        try
        {
            await using var issues = new IssueStore(FactoryFor(options.Db, primary.Id, Path.Combine(stateDir, "issues.db")));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var probe = await issues.ListAsync(new IssueFilter(), CancellationToken.None);
            sw.Stop();
            var expectedSchema = issues.Db.Provider == ForgeDbProvider.SqlServer
                ? Core.Db.SqlServerMigrations.ExpectedVersion
                : IssueStore.CurrentSchemaVersion;
            int actualSchema = -1;
            await using (var conn = await issues.Db.OpenAsync())
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT COALESCE(MAX(version), 0) FROM {issues.Db.Dialect.Table("schema_version")};";
                var result = await cmd.ExecuteScalarAsync();
                actualSchema = Convert.ToInt32(result);
            }
            var dbLabel = issues.Db.Provider == ForgeDbProvider.SqlServer
                ? $"sqlserver ({issues.Db.Qualifier}, {sw.ElapsedMilliseconds}ms)"
                : "sqlite";
            if (actualSchema == expectedSchema)
            {
                Console.WriteLine($"  [ok] db provider={dbLabel} schema v{actualSchema} (current)");
            }
            else
            {
                failures.Add($"db schema v{actualSchema} but current is v{expectedSchema} (run orchestrator once to migrate)");
            }
            _ = probe;
        }
        catch (Exception ex)
        {
            failures.Add($"db: {ex.GetType().Name}: {ex.Message}{DbAuthHint(ex, options)}");
        }

        // 3. Memory table reachable (same schema on SQL Server;
        //    separate memory.db file on SQLite).
        try
        {
            var memPath = Path.Combine(stateDir, "memory.db");
            if (!options.Db.IsSqlServer && !File.Exists(memPath))
            {
                Console.WriteLine("  [skip] memory.db does not exist yet (will be created on first start)");
            }
            else
            {
                MemoryStore mem;
                if (options.Db.IsSqlServer)
                {
                    mem = new MemoryStore(FactoryFor(options.Db, primary.Id, memPath));
                }
                else
                {
                    // Reuse IssueStore to bootstrap the schema, then check.
                    _ = new IssueStore(memPath);
                    mem = new MemoryStore(memPath);
                }
                await using (mem)
                {
                    var memProbe = await mem.RecallAsync();
                    Console.WriteLine($"  [ok] memory store reachable ({memProbe.Count} keys)");
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add($"memory store: {ex.GetType().Name}: {ex.Message}{DbAuthHint(ex, options)}");
        }

        // 4. LLM provider + key configured. The kilo gateway key is
        //    operator-managed via the dashboard Secrets page (encrypted
        //    per project); appsettings.json carries only the
        //    KILO_GATEWAY_API_KEY placeholder. Resolve the DB secret
        //    first so the auth probe below exercises the real key.
        var llmConfig = await ResolveProviderApiKeysAsync(
            LlmConfigAdapter.FromOptions(options.Llm), projects, secretStore,
            loggerFactory.CreateLogger("Forge.Bootstrap"));
        if (string.IsNullOrEmpty(llmConfig.DefaultProvider))
        {
            failures.Add("llm.defaultProvider is empty");
        }
        else
        {
            var provider = llmConfig.Providers.FirstOrDefault(p => p.Name == llmConfig.DefaultProvider);
            if (provider is null)
            {
                failures.Add($"llm.defaultProvider '{llmConfig.DefaultProvider}' not found in llm.providers[]");
            }
            else if (string.IsNullOrEmpty(provider.ApiKey) || provider.ApiKey.StartsWith("KILO_GATEWAY") || provider.ApiKey.StartsWith("GITHUB_TOKEN"))
            {
                failures.Add($"llm.providers['{provider.Name}'].apiKey looks unset (still a placeholder)");
            }
            else if (!provider.BaseUrl.StartsWith("http"))
            {
                failures.Add($"llm.providers['{provider.Name}'].baseUrl invalid: {provider.BaseUrl}");
            }
            else
            {
                // Ping the gateway with a minimal chat completion.
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
                    var resp = await http.PostAsJsonAsync(
                        provider.BaseUrl.TrimEnd('/') + "/v1/chat/completions",
                        new
                        {
                            model = provider.DefaultModel,
                            messages = new[] { new { role = "user", content = "ping" } },
                            max_tokens = 4,
                        });
                    if (resp.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"  [ok] kilo gateway reachable at {provider.BaseUrl} (model={provider.DefaultModel})");
                    }
                    else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        failures.Add($"kilo gateway auth failed (HTTP 401) — apiKey expired or invalid");
                    }
                    else
                    {
                        failures.Add($"kilo gateway returned HTTP {(int)resp.StatusCode} — {resp.ReasonPhrase}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"kilo gateway unreachable: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // 5. GitHub token + repo. The token is operator-managed per
        //    project via the dashboard Secrets page ('github_token');
        //    appsettings.json github.token carries only a placeholder.
        //    Resolve the DB secret first (same pattern as the kilo
        //    gateway key above) and probe the owning project's repo
        //    (owner/repo parsed from its registered RepoUrl).
        var ghToken = options.GitHub.Token;
        var ghOwner = options.GitHub.Owner;
        var ghRepo = options.GitHub.Repo;
        if (string.IsNullOrEmpty(ghToken) || ghToken.StartsWith("GITHUB_TOKEN"))
        {
            foreach (var project in projects)
            {
                string? secret;
                try
                {
                    secret = await secretStore.GetPlaintextAsync(
                        project.Id, SecretKinds.GitHubToken, CancellationToken.None);
                }
                catch
                {
                    continue;
                }
                if (string.IsNullOrEmpty(secret)) continue;

                ghToken = secret;
                if (ProjectDispatchBundleFactory.ParseGitHubOwnerRepo(project.RepoUrl) is { } parsed)
                {
                    ghOwner = parsed.Owner;
                    ghRepo = parsed.Repo;
                }
                break;
            }
        }
        if (string.IsNullOrEmpty(ghToken) || ghToken.StartsWith("GITHUB_TOKEN"))
        {
            failures.Add("github.token looks unset (still a placeholder) and no per-project 'github_token' secret is stored");
        }
        else
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", ghToken);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Forge-Check");
                var resp = await http.GetAsync($"https://api.github.com/repos/{ghOwner}/{ghRepo}");
                if (resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"  [ok] GitHub repo {ghOwner}/{ghRepo} reachable");
                }
                else if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    failures.Add($"GitHub repo {ghOwner}/{ghRepo} not found (or token lacks access)");
                }
                else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    failures.Add("GitHub token rejected (HTTP 401) — token expired or wrong scope");
                }
                else
                {
                    failures.Add($"GitHub returned HTTP {(int)resp.StatusCode} — {resp.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"GitHub unreachable: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("All pre-flight checks passed.");
            return 0;
        }
        Console.Error.WriteLine($"{failures.Count} pre-flight check(s) failed:");
        foreach (var f in failures)
        {
            Console.Error.WriteLine($"  - {f}");
        }
        return 1;
    }

    /// <summary>
    /// Provider API keys are operator-managed via the dashboard
    /// Secrets page (stored encrypted, per project) — appsettings.json
    /// carries only placeholders. For every provider whose ApiKey is
    /// empty or a UPPER_SNAKE placeholder, resolve the first stored
    /// secret named <c>&lt;provider&gt;_api_key</c> (provider name
    /// lowercased, dashes to underscores — kilo-gateway →
    /// kilo_gateway_api_key, kimi → kimi_api_key) across registered
    /// projects and substitute it. Key rotation via the UI takes
    /// effect on restart. Values are never logged.
    /// </summary>
    internal static async Task<LlmConfig> ResolveProviderApiKeysAsync(
        LlmConfig config,
        IReadOnlyList<ProjectOptions> projects,
        SecretStore secretStore,
        ILogger logger)
    {
        var providers = config.Providers.ToList();
        var changed = false;
        for (var i = 0; i < providers.Count; i++)
        {
            var p = providers[i];
            var needsKey = string.IsNullOrEmpty(p.ApiKey)
                || (p.ApiKey.ToUpperInvariant() == p.ApiKey && p.ApiKey.Contains('_') && !p.ApiKey.StartsWith("sk-"));
            if (!needsKey) continue;

            var kind = p.Name.ToLowerInvariant().Replace('-', '_') + "_api_key";
            foreach (var project in projects)
            {
                string? key;
                try
                {
                    key = await secretStore.GetPlaintextAsync(project.Id, kind, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to read {Kind} secret for project {ProjectId}; trying next project", kind, project.Id);
                    continue;
                }
                if (string.IsNullOrEmpty(key)) continue;

                logger.LogInformation("provider '{Provider}' api key resolved from project '{ProjectId}' secret store ({Kind})",
                    p.Name, project.Id, kind);
                providers[i] = p with { ApiKey = key };
                changed = true;
                break;
            }
        }
        return changed ? config with { Providers = providers } : config;
    }

    /// <summary>
    /// Pick the <see cref="IChatClientFactory"/> based on the configured
    /// providers. Stub config (no providers with a non-Empty ApiKey) yields
    /// the in-process <see cref="StubbedChatClientFactory"/>; everything else
    /// uses the OpenAI-compatible factory.
    /// </summary>
    internal static (IChatClientFactory factory, CostTracker? costTracker) SelectChatClientFactory(
        LlmConfig llmConfig, LlmOptions options, HeadroomOptions headroom)
    {
        var hasRealKey = llmConfig.Providers.Any(p => !string.IsNullOrEmpty(p.ApiKey));
        if (!hasRealKey)
        {
            Console.Error.WriteLine("No LLM provider with an API key configured; using StubbedChatClientFactory.");
            return (new StubbedChatClientFactory(), null);
        }
        var factory = new OpenAICompatibleChatClientFactory();
        CostTracker? tracker = null;
        if (headroom.Enabled && !string.IsNullOrEmpty(headroom.ProxyBaseUrl))
        {
            // Rewrite the LLM baseUrl so the OpenAI client talks to
            // Headroom. The proxy is started with the upstream URL
            // as a CLI flag, so it knows where to forward.
            factory.HeadroomProxyBaseUrl = headroom.ProxyBaseUrl;
            factory.HeadroomProviderName = headroom.ProviderName;
            Console.Error.WriteLine(
                $"Headroom: enabled (proxy={headroom.ProxyBaseUrl}, mode={headroom.Mode}, ccr={headroom.CcrEnabled}); chat client talks to the proxy.");
        }
        if (headroom.TrackUsage)
        {
            // We can't construct CostTracker without an inner
            // IChatClient; it's wired into the per-call factory
            // chain in OpenAICompatibleChatClientFactory.Create.
            // For the dashboard endpoint we expose the same
            // singleton below.
            // Note: CostTracker is a plain aggregator class
            // now (no longer extends DelegatingChatClient). The
            // per-session wrappers in OpenAICompatibleChatClientFactory
            // construct their own DelegatingChatClient around
            // the inner client.
            tracker = new CostTracker();
            factory.CostTracker = tracker;
        }
        return (factory, tracker);
    }


    /// <summary>
    /// Orchestrator runtime entrypoint. The entire object graph
    /// (stores, factories, schedulers, OrchestratorAgent, messaging)
    /// is composed in <see cref="Forge.Orchestrator.Composition.ForgeComposition"/>;
    /// this method only drives lifecycle: start the dashboard, replay
    /// recovery, start the background loops, then block on the dispatch
    /// loop until shutdown.
    /// </summary>
    private static async Task<int> RunOrchestratorAsync(
        AgentOptions options, ILoggerFactory loggerFactory, ILogger logger,
        CancellationToken externalStop = default)
    {
        ServiceProvider provider;
        try
        {
            provider = await Forge.Orchestrator.Composition.ForgeComposition.BuildAsync(
                options, loggerFactory, externalStop);
        }
        catch (Forge.Orchestrator.Composition.NoProjectsRegisteredException)
        {
            logger.LogWarning(
                "No projects registered. To register one, run with `--dashboard-only` and use the Projects page (or POST /api/projects), then restart the orchestrator. The v1 dispatch loop is single-project; runtime hot-add of dispatch targets is a follow-up — see AGENTS.md.");
            return 1;
        }

        await using (provider)
        {
            // externalStop is the host's stoppingToken when running
            // under systemd (default(CancellationToken) — never cancels
            // on its own — for every other invocation). Linking it
            // means a stop request tears the orchestrator down through
            // the exact same path as Ctrl+C.
            using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(externalStop);
            Console.CancelKeyPress += (_, e) =>
            {
                logger.LogWarning("SIGINT received; cancelling...");
                e.Cancel = true;
                shutdownCts.Cancel();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                logger.LogWarning("Process exit; cancelling");
                try { shutdownCts.Cancel(); } catch { }
            };

            var dashboard = provider.GetRequiredService<DashboardHost>();
            try
            {
                logger.LogInformation("Starting dashboard");
                await dashboard.StartAsync(shutdownCts.Token);

                // P4 Stage A — StartupRecovery. Runs ONCE before the
                // dispatch loop starts. Multi-project: one recovery
                // context per NON-primary project (the primary is the
                // recovery service's own construction context).
                var boot = provider.GetRequiredService<Forge.Orchestrator.Composition.ForgeProjectBootstrap>();
                var primary = boot.Projects[0];
                var startupRecovery = provider.GetRequiredService<Orchestrator.StartupRecovery>();
                var dispatchBundleFactory = provider.GetRequiredService<Orchestrator.ProjectDispatchBundleFactory>();
                var recoveryContexts = boot.Projects
                    .Where(p => !string.Equals(p.Id, primary.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(p =>
                    {
                        var b = dispatchBundleFactory.Build(p);
                        return new Orchestrator.StartupRecovery.ProjectRecoveryContext(
                            p.Id, b.IssueStore, b.Worktrees,
                            new Orchestrator.GitHubRecoveryAdapter(b.GitHub),
                            p.DefaultBranch);
                    })
                    .ToList();
                await startupRecovery.RunAsync(extraProjects: recoveryContexts, ct: shutdownCts.Token);

                // P4 Stage B — bring the workflow dispatcher host up.
                // For InProcess this is a no-op. For Durable this
                // starts the DTS worker.
                try
                {
                    await provider.GetRequiredService<Orchestrator.IWorkflowDispatcher>()
                        .EnsureReadyAsync(shutdownCts.Token);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Workflow dispatcher EnsureReadyAsync failed; dispatch may fail at call time.");
                }

                // Live provider-key refresh (Secrets-page rotation
                // without restart). Initial refresh happened during
                // composition; this is the 30s loop.
                var keyResolver = provider.GetService<Agents.ProviderApiKeyResolver>();
                if (keyResolver is not null)
                {
                    var providerNames = provider.GetRequiredService<Agents.LlmConfig>()
                        .Providers.Select(p => p.Name).ToArray();
                    _ = Task.Run(async () =>
                    {
                        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
                        try
                        {
                            while (await timer.WaitForNextTickAsync(shutdownCts.Token))
                                await keyResolver.RefreshAsync(providerNames, shutdownCts.Token);
                        }
                        catch (OperationCanceledException) { }
                    });
                }

                // JSONL mirror is a fire-and-forget background task; it
                // cancels itself when shutdownCts fires.
                _ = provider.GetRequiredService<IssuesJsonlMirror>().StartAsync(shutdownCts.Token);

                // Messaging: 15m backstop tick publisher. Event
                // consumers registered by the messaging wiring start
                // alongside it.
                await provider.GetRequiredService<Messaging.SweepTickPublisher>().StartAsync(shutdownCts.Token);

                // Schedulers (fire-and-forget; they cancel on shutdown).
                _ = provider.GetRequiredService<Orchestrator.ScheduledGroomer>().RunAsync(shutdownCts.Token);
                _ = provider.GetRequiredService<Orchestrator.ScheduledWatchdog>().RunAsync(shutdownCts.Token);
                _ = provider.GetRequiredService<Orchestrator.DesignerScheduler>().RunAsync(shutdownCts.Token);
                _ = provider.GetRequiredService<Orchestrator.ArtistScheduler>().RunAsync(shutdownCts.Token);
                _ = provider.GetRequiredService<Orchestrator.Sprint.SprintAssembler>().RunAsync(shutdownCts.Token);

                // Self-starting queue: resolving it guarantees the
                // singleton was constructed (its ctor subscribes the
                // worker).
                _ = provider.GetRequiredService<Agents.ProductRefinementQueue>();

                logger.LogInformation("Orchestrator starting");
                await provider.GetRequiredService<OrchestratorAgent>().ExecuteAsync(shutdownCts.Token);
                return 0;
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Orchestrator stopped");
                return 0;
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Orchestrator crashed");
                try { await dashboard.StopAsync(); } catch { }
                // Die for real: a logged crash that leaves the process
                // alive is a zombie systemd never restarts (observed
                // live 2026-07-30).
                Environment.Exit(1);
                return 1;
            }
            finally
            {
                try { await dashboard.StopAsync(); } catch { }
                try
                {
                    var stateStore = provider.GetRequiredService<StateStore>();
                    var s = await stateStore.LoadStateAsync();
                    await stateStore.SaveStateAsync(s);
                }
                catch { }
            }
        }
    }
}
