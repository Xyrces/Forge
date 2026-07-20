using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator.Slots;
using Forge.Projects;
using Forge.Orchestrator;
using Forge.Reviewer;

namespace Forge;

public static class Program
{
    // Held references for fire-and-forget background services so
    // the GC doesn't reap them mid-run. The orchestrator uses
    // top-level statements so we can't keep these as local async
    // fields; these statics are the simplest pattern.
    private static Agents.ProductRefinementQueue? _productRefinementQueue;
    private static Orchestrator.ScheduledGroomer? _scheduledGroomer;
    private static Orchestrator.DesignerScheduler? _scheduledDesigner;
    private static Orchestrator.ArtistScheduler? _scheduledArtist;
    private static Orchestrator.StartupRecovery? _startupRecovery;
    private static IssuesJsonlMirror? _issuesJsonlMirror;

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
                o.IncludeScopes = false;
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
            return await PrintStatusAsync(options, logger);

        if (mode == CliMode.Check)
            return await RunPreflightCheckAsync(options, logger);

        if (mode == CliMode.Enqueue)
            return await EnqueueTaskAsync(args, options);

if (mode == CliMode.DashboardOnly)
            return await RunDashboardOnlyAsync(options, loggerFactory, logger);

        if (mode == CliMode.WorktreeSmoke)
            return await RunWorktreeSmokeAsync(args, options, loggerFactory, logger);

        if (mode == CliMode.RecoverDryRun)
            return await RunRecoverAsync(options, loggerFactory, logger, dryRun: true);

        if (mode == CliMode.RecoverAndStart)
            return await RunRecoverAsync(options, loggerFactory, logger, dryRun: false);

        // Windows Service hosting. When launched by the Service
        // Control Manager (sc.exe / New-Service / Windows Services
        // MMC), stdin/stdout are not a console and Console.CancelKeyPress
        // never fires on stop -- the SCM instead expects the process
        // to honor a stop signal delivered through the generic host's
        // IHostApplicationLifetime. WindowsServiceHelpers.IsWindowsService()
        // returns false for `dotnet run`/interactive launches, so this
        // is a no-op for every other invocation in this switch.
        if (WindowsServiceHelpers.IsWindowsService())
            return await RunAsWindowsServiceAsync(options, loggerFactory, logger);

        return await RunOrchestratorAsync(options, loggerFactory, logger);
    }

    // Wraps RunOrchestratorAsync in a minimal generic Host so the
    // Service Control Manager sees a well-behaved service: SCM start
    // is acknowledged once the host starts the hosted service, and an
    // SCM stop request cancels stoppingToken, which we forward into
    // RunOrchestratorAsync's own shutdown token. Everything else
    // (dashboard, dispatch loop, schedulers) is unchanged -- this is
    // purely a lifecycle adapter, not a parallel code path.
    private static async Task<int> RunAsWindowsServiceAsync(
        AgentOptions options, ILoggerFactory loggerFactory, ILogger logger)
    {
        var exitCode = 0;
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(o => o.ServiceName = "Forge");
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

    private enum CliMode { Run, Once, Status, Enqueue, DashboardOnly, WorktreeSmoke, Check, RecoverDryRun, RecoverAndStart }

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

    private static async Task<int> PrintStatusAsync(AgentOptions options, ILogger logger)
    {
        try
        {
            var (projects, dbByProject, _) = BuildProjectBootstrap(options, logger);
            var primary = projects[0];
            await using var issues = new IssueStore(dbByProject[primary.Id]);
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

    private static async Task<int> EnqueueTaskAsync(string[] args, AgentOptions options)
    {
        var title = ParseArg(args, "--enqueue-task")
            ?? $"task-{Guid.NewGuid().ToString("N")[..8]}";
        var type = ParseArg(args, "--task-type") ?? "ecs";
        var description = ParseArg(args, "--task-desc") ?? "no description";
        var branch = ParseArg(args, "--branch") ?? $"agent/{title}";

        var (projects, dbByProject, _) = BuildProjectBootstrap(options, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var primary = projects[0];
        var issues = new IssueStore(dbByProject[primary.Id]);


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
    /// Resolves Forgesystem options + runs the per-project bootstrap
    /// (create root dir, init git repo, allocate state DB). Returns the
    /// finalised project list (with Root rewritten to the bootstrap
    /// directory) + a per-project DB path map. Idempotent.
    /// </summary>
    private static (IReadOnlyList<ProjectOptions> Projects,
                    Dictionary<string, string> IssuesDbByProject,
                    string DataRoot)
        BuildProjectBootstrap(AgentOptions options, ILogger logger)
    {
        var dataRoot = ForgesystemPaths.ResolveDataRoot(options.Forgesystem.DataRoot);
        Directory.CreateDirectory(dataRoot);
        logger.LogInformation("Forgesystem data root: {Root}", dataRoot);

        var bootstrap = new ProjectBootstrap(dataRoot, null);
        var registry = ProjectRegistryLoader.Load(options, logger);

        var finalised = new List<ProjectOptions>(registry.Count);
        var dbByProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in registry)
        {
            var result = bootstrap.EnsureProject(p);
            finalised.Add(result.Project);
            dbByProject[result.Project.Id] = result.IssuesDbPath;
            logger.LogInformation(
                "Project '{Id}' root={Root} state={State} created={Created} gitInit={GitInit}",
                result.Project.Id, result.Project.Root, result.StateDirectory,
                result.Created, result.InitializedAsGitRepo);
        }

        return (finalised, dbByProject, dataRoot);
    }

    private static async Task<int> RunDashboardOnlyAsync(
        AgentOptions options, ILoggerFactory loggerFactory, ILogger logger)
    {
        var (dashboardOnlyProjects, dbByProject, _) = BuildProjectBootstrap(options, logger);
        var defaultDb = dashboardOnlyProjects.Count > 0
            ? dbByProject[dashboardOnlyProjects[0].Id]
            : throw new InvalidOperationException("At least one project is required to run the dashboard.");
        var issues = new IssueStore(defaultDb);
        var agents = new AgentStore(issues);
        var skills = new SkillStore(issues);
        var sprints = new SprintStore(issues);
        var messageBus = new AgentMessageBus();
        var eventBus = new InMemoryDashboardEventBus();
        var dashboardOnlyFactory = new ProjectContextFactory(dashboardOnlyProjects, dbByProject);
        var dashboardOnlySlots = new SlotTable();
        var _roleFiller = new[] { "coredev", "clientdev", "reviewer", "intake", "designer", "artist", "groomer", "orchestrator" };
        foreach (var pp in dashboardOnlyProjects)
            foreach (var rr in _roleFiller)
                dashboardOnlySlots.Configure(pp.Id, rr, DefaultProjectRoles.MaxFor(pp.Roles, rr));
var dashboard = new DashboardHost(
            options.Dashboard, options.Headroom, issues, agents, skills, sprints, messageBus, eventBus,
            loggerFactory.CreateLogger<DashboardHost>(),
            projectFactory: dashboardOnlyFactory,
            slots: dashboardOnlySlots);

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
    private static GitHubService BuildGitHubService(Configuration.GitHubOptions options, ILogger<GitHubService> logger)
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
            var (projects, dbByProject, _) = BuildProjectBootstrap(options, logger);
            var primary = projects[0];
            var primaryDb = dbByProject[primary.Id];
            var stateDir = Path.GetDirectoryName(primaryDb)!;
            await using var issues = new IssueStore(primaryDb);
            var recoveryReports = new RecoveryReportStore(primaryDb);
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

    private static async Task<int> RunPreflightCheckAsync(AgentOptions options, ILogger logger)
    {
        var failures = new List<string>();
        var (projects, dbByProject, dataRoot) = BuildProjectBootstrap(options, logger);
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

        // 2. IssueStore opens + schema version is current
        try
        {
            await using var issues = new IssueStore(Path.Combine(stateDir, "issues.db"));
            // Trigger InitializeSchema by listing (cheap read).
            var probe = await issues.ListAsync(new IssueFilter(), CancellationToken.None);
            var expectedSchema = IssueStore.CurrentSchemaVersion;
            // Read the actual schema_version from the DB.
            int actualSchema = -1;
            await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(issues.ConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
                var result = await cmd.ExecuteScalarAsync();
                actualSchema = Convert.ToInt32(result);
            }
            if (actualSchema == expectedSchema)
            {
                Console.WriteLine($"  [ok] issues.db schema v{actualSchema} (current)");
            }
            else
            {
                failures.Add($"issues.db schema v{actualSchema} but current is v{expectedSchema} (run orchestrator once to migrate)");
            }
            _ = probe;
        }
        catch (Exception ex)
        {
            failures.Add($"issues.db: {ex.GetType().Name}: {ex.Message}");
        }

        // 3. MemoryStore opens + schema version is current
        try
        {
            var memPath = Path.Combine(stateDir, "memory.db");
            if (!File.Exists(memPath))
            {
                Console.WriteLine("  [skip] memory.db does not exist yet (will be created on first start)");
            }
            else
            {
                // Reuse IssueStore to bootstrap the schema, then check.
                _ = new IssueStore(memPath);
                await using var mem = new MemoryStore(memPath);
                var memProbe = await mem.RecallAsync();
                int actualSchema = -1;
                await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(mem.ConnectionString))
                {
                    await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
                    var result = await cmd.ExecuteScalarAsync();
                    actualSchema = Convert.ToInt32(result);
                }
                var expectedSchema = IssueStore.CurrentSchemaVersion;
                if (actualSchema == expectedSchema)
                {
                    Console.WriteLine($"  [ok] memory.db schema v{actualSchema} (current)");
                }
                else
                {
                    failures.Add($"memory.db schema v{actualSchema} but current is v{expectedSchema} (run orchestrator once to migrate)");
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add($"memory.db: {ex.GetType().Name}: {ex.Message}");
        }

        // 4. LLM provider + key configured
        var llmConfig = LlmConfigAdapter.FromOptions(options.Llm);
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

        // 5. GitHub token + repo
        if (string.IsNullOrEmpty(options.GitHub.Token) || options.GitHub.Token.StartsWith("GITHUB_TOKEN"))
        {
            failures.Add("github.token looks unset (still a placeholder)");
        }
        else
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", options.GitHub.Token);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Forge-Check");
                var resp = await http.GetAsync($"https://api.github.com/repos/{options.GitHub.Owner}/{options.GitHub.Repo}");
                if (resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"  [ok] GitHub repo {options.GitHub.Owner}/{options.GitHub.Repo} reachable");
                }
                else if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    failures.Add($"GitHub repo {options.GitHub.Owner}/{options.GitHub.Repo} not found (or token lacks access)");
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
    /// Pick the <see cref="IChatClientFactory"/> based on the configured
    /// providers. Stub config (no providers with a non-Empty ApiKey) yields
    /// the in-process <see cref="StubbedChatClientFactory"/>; everything else
    /// uses the OpenAI-compatible factory.
    /// </summary>
    private static (IChatClientFactory factory, CostTracker? costTracker) SelectChatClientFactory(
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


    private static async Task<int> RunOrchestratorAsync(
        AgentOptions options, ILoggerFactory loggerFactory, ILogger logger,
        CancellationToken externalStop = default)
    {
        var (knownProjects, orchDbByProject, orchDataRoot) = BuildProjectBootstrap(options, logger);
        var primary = knownProjects[0];
        var primaryDb = orchDbByProject[primary.Id];
        var primaryStateDir = Path.GetDirectoryName(primaryDb)!;
        var stateStore = new StateStore(primaryStateDir);
        var issues = new IssueStore(primaryDb);
        var agents = new AgentStore(issues);
        var skills = new SkillStore(issues);
        var sprints = new SprintStore(issues);
        var messageBus = new AgentMessageBus();
        var worktrees = new GitWorktreeService(
            new WorkspaceOptions
            {
                Root = primary.Root,
                WorktreeRoot = options.Workspace.WorktreeRoot,
                DefaultBranch = options.Workspace.DefaultBranch,
            },
            loggerFactory.CreateLogger<GitWorktreeService>());
        var gitHub = BuildGitHubService(options.GitHub, loggerFactory.CreateLogger<GitHubService>());
        var roleRegistry = new RoleAgentRegistry();
        var agentsStore = new Core.AgentStore(issues);
        var skillsStore = new Core.SkillStore(issues);
        var skillSource = new SqliteSkillSource(agentsStore, skillsStore, roleRegistry);
        // The memory table lives in IssueStore's schema (v7). Construct an
        // IssueStore against the memory DB once at startup so the schema
        // (and any future migrations) run before MemoryStore touches it.
        // MemoryStore itself does not own migrations.
        var memoryDbPath = Path.Combine(primaryStateDir, "memory.db");
        var memoryBootstrap = new Core.IssueStore(memoryDbPath);
        var memoryStore = new MemoryStore(memoryDbPath);

        // 2026-07-18 (Phase 2.11.f + bug-3 deploy self-deadlock fix):
        // before any startup work, scan sibling-of-releases for
        // .pending-{sha} markers and consume them. The new release
        // was published by SelfHostedWindowsServiceDeploymentExecutor;
        // we swap the junction (and delete the marker) before any
        // startup work so a half-failed deploy from the previous
        // process bootstraps into the new binary cleanly.
        var primaryDeployCfg = primary.Deployment;
        if (primaryDeployCfg?.Kind == DeploymentKind.SelfHostedWindowsService
            && !string.IsNullOrWhiteSpace(primaryDeployCfg.ReleasesRoot)
            && !string.IsNullOrWhiteSpace(primaryDeployCfg.CurrentLinkPath))
        {
            var siblingOfReleases = Path.GetDirectoryName(primaryDeployCfg.ReleasesRoot.TrimEnd('\\','/'))!;
            if (Directory.Exists(siblingOfReleases))
            {
                foreach (var markerPath in Directory.GetFiles(siblingOfReleases, ".pending-*"))
                {
                    try
                    {
                        var sha = Path.GetFileName(markerPath).Substring(".pending-".Length);
                        var releaseDir = Path.Combine(primaryDeployCfg.ReleasesRoot, sha);
                        if (!Directory.Exists(releaseDir))
                        {
                            logger.LogWarning("Skipping pending marker {Marker}: releaseDir {ReleaseDir} missing", markerPath, releaseDir);
                            File.Delete(markerPath);
                            continue;
                        }
                        var current = primaryDeployCfg.CurrentLinkPath;
                        if (Directory.Exists(current))
                        {
                            var attrs = File.GetAttributes(current);
                            if (attrs.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(current);
                            else
                            {
                                var backup = current + ".pre-junction-" + DateTime.UtcNow.Ticks;
                                Directory.Move(current, backup);
                            }
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(current))!);
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c mklink /J \"{current}\" \"{releaseDir}\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                        };
                        using var proc = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("mklink start failed");
                        proc.WaitForExit();
                        if (proc.ExitCode != 0)
                            throw new InvalidOperationException($"mklink exit={proc.ExitCode}; {proc.StandardError.ReadToEnd()}");
                        File.Delete(markerPath);
                        logger.LogInformation("Staged swap applied: current -> {ReleaseDir}", releaseDir);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Pending marker {Marker} failed; leaving for retry", markerPath);
                    }
                }
            }
        }

        // Phase 4: JSONL mirror of the issue store. Background service
        // rewrites the file every 5s so it's safe to tail -f.
        var issuesJsonlPath = Path.Combine(primaryStateDir, "issues.jsonl");
        var jsonlMirror = new IssuesJsonlMirror(issues, issuesJsonlPath,
            loggerFactory.CreateLogger<IssuesJsonlMirror>());

            // P3.5: issue_groomer_run store. Shares the issues DB
            // (the v8 migration is applied at IssueStore's ctor).
            // The groomer_runs table has a foreign key on issue.id,
            // so the runs must live in the same DB as the issue rows.
            var groomerRunsDb = primaryDb;
            var groomerRuns = new Core.IssueGroomerRunStore(groomerRunsDb);
            // P2.a: design_artifact + designer_run share the issues.db
            // (the v9 migration created both tables). The IssueStore
            // ctor already ran the migration.
            var designArtifacts = new Core.DesignArtifactStore(groomerRunsDb);
            var designerRuns = new Core.DesignerRunStore(groomerRunsDb);
            // P2.b: art_output + artist_run share the issues.db (the
            // v10 migration created both tables).
            var artOutputs = new Core.ArtOutputStore(groomerRunsDb);
            var artistRuns = new Core.ArtistRunStore(groomerRunsDb);
            // P4 Stage A: recovery_report table (lives in issues.db
            // alongside the other v10/v11 tables; created in the
            // IssueStore schema migration).
            var recoveryReports = new Core.RecoveryReportStore(groomerRunsDb);

        // P0.5: vision.md import. Build the VisionStore (loads the
        // configured file on startup), inject it into memory as the
        // 'vision/master' key, and pass it to the dashboard so the
        // Vision tab can surface it.
        var vision = new VisionStore(primary.Root, options.Vision.Path);
        var visionSnapshot = vision.Reload();
        if (visionSnapshot.Exists)
        {
            logger.LogInformation("Vision loaded from {Path} ({Len} chars)",
                visionSnapshot.Path, visionSnapshot.Content.Length);
            // Inject into memory so every agent prompt includes the
            // vision. The memory block goes through the normal
            // MemoryStore path; no special casing in the agent.
            await memoryStore.RememberAsync("vision/master", visionSnapshot.Content, ttlDays: null, CancellationToken.None);
        }
        else
        {
            logger.LogWarning("Vision file not found at {Path}; dashboard Vision tab will be empty", visionSnapshot.Path);
        }

        // Bootstrap the operator-maintained playbook reference
        // (Xyrces/godot-ecs-gamedev-playbook) into the memory layer
        // so every agent prompt can see the repo URL + a per-role
        // skill list. Idempotent: SeedIfMissingAsync skips writes
        // when the key already exists, so operator edits survive
        // orchestrator restarts.
        var skillBootstrap = new Agents.SkillBootstrap(
            memoryStore, loggerFactory.CreateLogger<Agents.SkillBootstrap>());
        await skillBootstrap.SeedAsync();
        var llmConfig = LlmConfigAdapter.FromOptions(options.Llm);
        var (chatClientFactory, costTracker) = SelectChatClientFactory(llmConfig, options.Llm, options.Headroom);

        // P5.5: auto-extract project memory from the model
        // response after each PR is opened. Audit log lives in
        // the same memory.db; the v13 migration in IssueStore
        // covers the table.
        var extractionStore = new Orchestrator.MemoryExtractionStore(groomerRunsDb);
        var sprintProposalAudit = new Orchestrator.SprintProposalAuditStore(groomerRunsDb);
        var scorer = new Agents.DeterministicScorer();
        var sprintPropose = new Orchestrator.SprintProposeService(issues, sprints, scorer, sprintProposalAudit);
        var memoryExtractor = new Orchestrator.MemoryExtractor(
            chatClientFactory, llmConfig, memoryStore,
            loggerFactory.CreateLogger<Orchestrator.MemoryExtractor>());
        // Late-binding holder for specStore. Created before
        // MafAgentRunner ctor, populated after specStore is
        // constructed (the runner builds its tool list per call,
        // so a forward-reference is enough).
        var specStoreRef = new Core.SpecStoreHolder();
        var agentRunner = new MafAgentRunner(
            chatClientFactory, llmConfig, roleRegistry,
            loggerFactory.CreateLogger<MafAgentRunner>(),
            skills: skillSource,
            kiloAgentsRoot: Path.Combine(primary.Root, ".kilo", "agents"),
            memory: memoryStore,
            handoffs: recoveryReports is null ? null : new Core.ContextHandoffStore(groomerRunsDb),
            designArtifacts: () => designArtifacts,
            specs: () => specStoreRef.Value,
            artOutputs: () => artOutputs);
        var eventBus = new InMemoryDashboardEventBus();
        var prWatcher = new PRWatcher(
            gitHub, worktrees, issues,
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30),
            eventBus,
            loggerFactory.CreateLogger<PRWatcher>());
        // P4 Stage B — pick the workflow runtime based on
// appsettings.json. The InProcess dispatcher (default) is a
// thin lambda over the existing EngineeringDispatchWorkflow +
// InProcessExecution; the Durable dispatcher registers the
// same workflow with Microsoft.Agents.AI.DurableTask so the
// DTS sidecar persists workflow state across orchestrator
// crashes. The switch is controlled by Orchestrator:Execution
// in appsettings.json. See deploy/docker-compose.yml for the
// DTS emulator sidecar that powers Durable mode in dev.
        IWorkflowDispatcher dispatcher;
        if (string.Equals(options.Orchestrator.Execution, "Durable", StringComparison.OrdinalIgnoreCase))
        {
            // Build the workflow ONCE (the executors are stateless;
            // they read singletons from DI at construction). The
            // Durable runtime expects a Workflow instance, not a
            // factory, so we share it across all orchestrations.
            var workflow = new Orchestrator.Workflow.EngineeringDispatchWorkflow(
                issues, agentRunner, worktrees, gitHub, roleRegistry, options.Workspace,
                eventBus, agent => messageBus.Drain(agent),
                designArtifacts, artOutputs,
                memoryExtractor, extractionStore,
                loggerFactory.CreateLogger<Orchestrator.Workflow.EngineeringDispatchWorkflow>())
                .Build();
            var services = new ServiceCollection()
                .AddSingleton(workflow)
                .BuildServiceProvider();
            dispatcher = new Orchestrator.DurableDispatcher(
                options.Orchestrator,
                workflow,
                loggerFactory.CreateLogger<Orchestrator.DurableDispatcher>(),
                buildHost: () => Orchestrator.DurableDispatcher.BuildHost(
                    services, workflow, options.Orchestrator));
        }
        else
        {
            // InProcessDispatcher (default): runs the same
            // workflow via InProcessExecution. P4 Stage A's
            // StartupRecovery handles crash safety.
            dispatcher = new Orchestrator.InProcessDispatcher(
                async (issue, ct) =>
                {
                    var workflow = new Orchestrator.Workflow.EngineeringDispatchWorkflow(
                        issues, agentRunner, worktrees, gitHub, roleRegistry, options.Workspace,
                        eventBus, agent => messageBus.Drain(agent),
                        designArtifacts, artOutputs,
                        memoryExtractor, extractionStore,
                        loggerFactory.CreateLogger<Orchestrator.Workflow.EngineeringDispatchWorkflow>());
                    await workflow.RunAsync(issue, ct);
                },
                loggerFactory.CreateLogger<Orchestrator.InProcessDispatcher>());
        }

        var orchestrator = new OrchestratorAgent(
            agentRunner, roleRegistry, worktrees, gitHub, prWatcher, issues,
            agents, sprints, messageBus,
            eventBus,
            designArtifacts,
            artOutputs,
            dispatcher,
            loggerFactory.CreateLogger<OrchestratorAgent>());
        orchestrator.BindOptions(options);
        var intakeStore = new Core.IntakeStore(issues);
        var intakeRegistry = new IntakeAgentRegistry(projectId =>
            new IntakeAgent(
                projectId,
                intakeStore,
                issues,
                sprints,
                chatClientFactory,
                llmConfig,
                roleRegistry,
                eventBus,
                loggerFactory.CreateLogger<IntakeAgent>(),
                skills: skillSource,
                kiloAgentsRoot: Path.Combine(primary.Root, ".kilo", "agents")));
        var specStore = new Core.SpecStore(issues, designArtifacts: designArtifacts);
        specStoreRef.Set(specStore);  // P5 — wire the spec store to the late-binding holder
        var specExtractionReader = new Core.SpecExtractionReader(issues);
        var codebaseGraphCache = new Codebase.CodebaseGraphCacheStore(issues);
        var codebaseGraphBuilder = new Codebase.DotnetCodebaseGraphBuilder();
        var projectContextSource = new Core.FilesystemProjectContextSource(
            issues, agents, specStore, skills, primary.Root);
        var productAgentFactory = new Agents.ProductAgentFactory(
            specStore, issues, projectContextSource, chatClientFactory, llmConfig,
            roleRegistry, eventBus, skillSource, loggerFactory,
            Path.Combine(primary.Root, ".kilo", "agents"));
        var productRefinementQueue = new Agents.ProductRefinementQueue(
            productAgentFactory, specStore, eventBus,
            loggerFactory.CreateLogger<Agents.ProductRefinementQueue>());
        // Hold a reference: the queue self-starts in its ctor; if it
        // goes out of scope the GC reaps the worker Task and the
        // event subscription dies.
        _productRefinementQueue = productRefinementQueue;
        var groomerFactory = new Agents.GroomerAgentFactory(
            issues, specStore, eventBus, chatClientFactory, llmConfig, loggerFactory);
        // P2.a: Designer pipeline. The hygiene checker is shared
        // between the manual endpoint, the scheduled run, and the
        // agent's first step. The factory builds fresh DesignerAgent
        // instances per run.
        var designHygiene = new Orchestrator.DesignHygieneChecker(
            specStore, codebaseGraphCache, codebaseGraphBuilder, primary.Root);
        var designerAgentFactory = new Orchestrator.DesignerAgentFactory(
            specStore, designArtifacts, designerRuns, memoryStore, designHygiene,
            chatClientFactory, llmConfig, roleRegistry, eventBus, loggerFactory);
        // P2.b: Meshy client + Artist pipeline. The Meshy client
        // uses a plain SocketsHttpHandler in production; the
        // injection seam (HttpMessageHandler) lets tests stub the
        // upstream API.
        var meshyOptions = Microsoft.Extensions.Options.Options.Create(new Meshy.MeshyOptions
        {
            ApiKey = options.Llm.MeshyApiKey,
            BaseUrl = options.Llm.MeshyBaseUrl,
            PollIntervalSeconds = options.Llm.MeshyPollIntervalSeconds,
            MaxWaitSeconds = options.Llm.MeshyMaxWaitSeconds,
            MaxConcurrentJobs = options.Llm.MeshyMaxConcurrentJobs,
        });
        var meshy = new Meshy.MeshyClient(
            new SocketsHttpHandler(),
            meshyOptions,
            loggerFactory.CreateLogger<Meshy.MeshyClient>(),
            artOutputRoot: primary.Id == "default"
                ? Path.Combine(primary.Root, ".portHorizon", "art-output")
                : ForgesystemPaths.ArtOutputDir(orchDataRoot, primary.Id));
        var artistAgentFactory = new Orchestrator.ArtistAgentFactory(
            specStore, designArtifacts, artOutputs, artistRuns, memoryStore, meshy,
            chatClientFactory, llmConfig, roleRegistry, eventBus, loggerFactory);
        // P4 Stage A — StartupRecovery service. Constructed BEFORE
        // the dashboard so the dashboard can expose recovery
        // endpoints (POST /api/recovery/run + dry-run + reports).
        // RunAsync is called later, after the dashboard starts.
        var startupRecovery = new Orchestrator.StartupRecovery(
            issues, recoveryReports!, worktrees,
            new Orchestrator.GitHubRecoveryAdapter(gitHub),
            eventBus,
            loggerFactory.CreateLogger<Orchestrator.StartupRecovery>());
        _startupRecovery = startupRecovery;  // held against GC reaping

        // v1 multi-project: build the registry from configuration
        // (back-compat shim to a single "default" project when only
        // workspace.root is set), lazily construct per-project
        // IssueStore bundles, and pre-size in-process concurrency
        // slots per (projectId, role). The orchestrator dispatch
        // loop still uses the legacy single-workspace path; this
        // exposes multi-project info to the dashboard.
        var projectFactory = new ProjectContextFactory(knownProjects, orchDbByProject);
        var slots = new SlotTable();
        var roleFiller = new[] { "coredev", "clientdev", "reviewer", "intake", "designer", "artist", "groomer", "orchestrator" };
        foreach (var p in knownProjects)
        {
            foreach (var role in roleFiller)
            {
                var max = DefaultProjectRoles.MaxFor(p.Roles, role);
                slots.Configure(p.Id, role, max);
            }
        }
        if (knownProjects.Count > 0)
        {
            logger.LogInformation(
                "Multi-project registry: {Count} project(s) [{Ids}]; slot caps configured per role.",
                knownProjects.Count,
                string.Join(",", knownProjects.Select(p => $"{p.Id}={p.Name}")));
        }

        // P8: reconcile any SelfHostedWindowsService deployment that
        // completed (or failed) while THIS process wasn't running --
        // the executor that kicked it off was killed by the very
        // service stop it triggered, so the verdict could only ever
        // be written by whichever Forge.Core process starts next.
        var deployReconciler = new Deploy.DeploymentResultReconciler(
            loggerFactory.CreateLogger<Deploy.DeploymentResultReconciler>());
        await deployReconciler.ReconcileAsync(
            knownProjects,
            projectId => new Deploy.DeploymentStore(orchDbByProject[projectId]),
            CancellationToken.None);

        var dashboard = new DashboardHost(
            options.Dashboard, options.Headroom, issues, agents, skills, sprints, messageBus, eventBus,
            loggerFactory.CreateLogger<DashboardHost>(),
            intakeStore: intakeStore,
            intakeRegistry: intakeRegistry,
            specs: specStore,
            groomerFactory: groomerFactory,
            memory: memoryStore,
            extractions: extractionStore,
            sprintProposalAudit: sprintProposalAudit,
            sprintPropose: sprintPropose,
            issuesJsonlPath: issuesJsonlPath,
            vision: vision,
            groomerRuns: groomerRuns,
            designerFactory: designerAgentFactory,
            designerRuns: designerRuns,
            designArtifacts: designArtifacts,
            artistFactory: artistAgentFactory,
            artistRuns: artistRuns,
            artOutputs: artOutputs,
            meshy: meshy,
            recoveryReports: recoveryReports,
            startupRecovery: startupRecovery,
            costTracker: costTracker,
            extractor: specExtractionReader,
            codebaseBuilder: codebaseGraphBuilder,
            codebaseCache: codebaseGraphCache,
            projectFactory: projectFactory,
            slots: slots,
            gitHub: gitHub,
            reviewerRunner: agentRunner,
            loggerFactory: loggerFactory);

        // externalStop is the Windows Service host's stoppingToken when
        // running under the SCM (default(CancellationToken) -- never
        // cancels on its own -- for every other invocation). Linking it
        // means an SCM stop request tears the orchestrator down through
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

try
        {
            logger.LogInformation("Starting dashboard");
            await dashboard.StartAsync(shutdownCts.Token);

            // P4 Stage A — StartupRecovery. Runs ONCE before the
            // dispatch loop starts. Inspects every InProgress +
            // assignee=kilo issue, replays the cheap side-effects
            // (commit, push, PR open) when the LLM has already
            // finished but the previous run crashed, and writes
            // one recovery_report row. By default we run recovery
            // every startup; --check (existing pre-flight) skips
            // the dispatch loop entirely and --recover runs
            // recovery with no side effects and exits 0.
            //
            // The startupRecovery service was constructed BEFORE
            // the dashboard so the dashboard's endpoints can
            // expose it. Run the recovery pass now (before the
            // dispatch loop starts) so any in-flight issues from
            // a previous crash are replayed before the new
            // dispatch cycle begins.
            await startupRecovery.RunAsync(ct: shutdownCts.Token);

            // P4 Stage B — bring the workflow dispatcher host up.
            // For InProcess this is a no-op. For Durable this
            // starts the DTS worker (which connects to the DTS
            // sidecar). Failures here are visible in the log;
            // dispatch falls back to errors per-call.
            try
            {
                await dispatcher.EnsureReadyAsync(shutdownCts.Token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Workflow dispatcher EnsureReadyAsync failed; dispatch may fail at call time.");
            }

            // JSONL mirror is a fire-and-forget background task; it
            // cancels itself when shutdownCts fires.
            _ = jsonlMirror.StartAsync(shutdownCts.Token);
            _issuesJsonlMirror = jsonlMirror;

            // P3.5: scheduled Groomer wakes up every 5 minutes and
            // grooms any Approved specs that haven't been groomed
            // recently (or whose last groom failed). Fire-and-forget.
            var scheduledGroomer = new Orchestrator.ScheduledGroomer(
                specStore, groomerFactory, groomerRuns, eventBus,
                loggerFactory.CreateLogger<Orchestrator.ScheduledGroomer>(),
                interval: TimeSpan.FromMinutes(5));
            _ = scheduledGroomer.RunAsync(shutdownCts.Token);
            _scheduledGroomer = scheduledGroomer;

            // P2.a: scheduled Designer wakes up every 5 minutes and
            // designs any ReadyForDesign specs that haven't been
            // designed recently (or whose last design failed).
            // Fire-and-forget.
            var scheduledDesigner = new Orchestrator.DesignerScheduler(
                specStore, designerAgentFactory, designerRuns, eventBus,
                loggerFactory.CreateLogger<Orchestrator.DesignerScheduler>(),
                interval: TimeSpan.FromMinutes(5));
            _ = scheduledDesigner.RunAsync(shutdownCts.Token);
            _scheduledDesigner = scheduledDesigner;

            // P2.b: scheduled Artist wakes up every 5 minutes and
            // produces art for any Designed specs that haven't been
            // arted recently (or whose last art run failed).
            // Fire-and-forget.
            var scheduledArtist = new Orchestrator.ArtistScheduler(
                specStore, artistAgentFactory, artistRuns, eventBus,
                loggerFactory.CreateLogger<Orchestrator.ArtistScheduler>(),
                interval: TimeSpan.FromMinutes(5));
            _ = scheduledArtist.RunAsync(shutdownCts.Token);
            _scheduledArtist = scheduledArtist;

            logger.LogInformation("Orchestrator starting");
            await orchestrator.ExecuteAsync(shutdownCts.Token);
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
            return 1;
        }
        finally
        {
            try { await dashboard.StopAsync(); } catch { }
            try
            {
                var s = await stateStore.LoadStateAsync();
                await stateStore.SaveStateAsync(s);
            }
            catch { }
        }
    }
}
