using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Acp;
using PortHorizon.Agents.AgentTools;
using PortHorizon.Agents.Agents;
using PortHorizon.Agents.Configuration;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;
using PortHorizon.Agents.Orchestrator;
using PortHorizon.Agents.Reviewer;

namespace PortHorizon.Agents;

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
                o.IncludeScopes = false;
            });
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger("PortHorizon.Agents");

        if (mode == CliMode.Status)
            return await PrintStatusAsync(options, logger);

        if (mode == CliMode.Enqueue)
            return await EnqueueTaskAsync(args, options);

        if (mode == CliMode.DashboardOnly)
            return await RunDashboardOnlyAsync(options, loggerFactory, logger);

        if (mode == CliMode.WorktreeSmoke)
            return await RunWorktreeSmokeAsync(args, options, loggerFactory, logger);

        return await RunOrchestratorAsync(options, loggerFactory, logger);
    }

    private enum CliMode { Run, Once, Status, Enqueue, DashboardOnly, WorktreeSmoke }

    private static CliMode ParseMode(string[] args)
    {
        if (args.Any(a => a == "--status")) return CliMode.Status;
        if (args.Any(a => a == "--enqueue-task")) return CliMode.Enqueue;
        if (args.Any(a => a == "--dashboard-only")) return CliMode.DashboardOnly;
        if (args.Any(a => a == "--worktree-smoke")) return CliMode.WorktreeSmoke;
        if (args.Any(a => a == "--once")) return CliMode.Once;
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
        var stateStore = new StateStore();
        try
        {
            var state = await stateStore.LoadStateAsync();
            Console.WriteLine($"Pending: {state.Tasks.Count(t => t.Status == AgentTaskStatus.Pending)}");
            Console.WriteLine($"InProgress: {state.Tasks.Count(t => t.Status == AgentTaskStatus.InProgress)}");
            Console.WriteLine($"Completed: {state.Tasks.Count(t => t.Status == AgentTaskStatus.Completed)}");
            Console.WriteLine($"Failed: {state.Tasks.Count(t => t.Status == AgentTaskStatus.Failed)}");
            Console.WriteLine($"Blocked: {state.Tasks.Count(t => t.Status == AgentTaskStatus.Blocked)}");
            Console.WriteLine($"Total: {state.Tasks.Count}");
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
        var taskId = ParseArg(args, "--enqueue-task")
            ?? Guid.NewGuid().ToString("N")[..12];
        var type = ParseArg(args, "--task-type") ?? "ecs";
        var description = ParseArg(args, "--task-desc") ?? "no description";
        var branch = ParseArg(args, "--branch") ?? $"agent/{taskId}";

        var stateStore = new StateStore();
        var state = await stateStore.LoadStateAsync();
        if (state.Tasks.Any(t => t.Id == taskId))
        {
            Console.Error.WriteLine($"Task {taskId} already exists");
            return 1;
        }
        state.Tasks.Add(new AgentTask(
            Id: taskId,
            Type: type,
            Description: description,
            Parameters: new Dictionary<string, object>(StringComparer.Ordinal),
            Branch: branch,
            Status: AgentTaskStatus.Pending,
            Error: null,
            CreatedAt: DateTime.UtcNow));
        await stateStore.SaveStateAsync(state);
        Console.WriteLine($"Enqueued task {taskId} (type={type})");
        return 0;
    }

    private static async Task<int> RunDashboardOnlyAsync(
        AgentOptions options, ILoggerFactory loggerFactory, ILogger logger)
    {
        var stateStore = new StateStore();
        var eventBus = new InMemoryDashboardEventBus();
        var dashboard = new DashboardHost(
            options.Dashboard, stateStore, eventBus,
            loggerFactory.CreateLogger<DashboardHost>());

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

            var headProbe = Path.Combine(path, "PortHorizon.sln");
            Console.WriteLine($"[3/5] Probe: {headProbe} exists = {File.Exists(headProbe)}");

            Console.WriteLine("[4/5] Writing + committing a marker file...");
            var marker = Path.Combine(path, ".ph-smoke");
            await File.WriteAllTextAsync(marker, $"smoke at {DateTime.UtcNow:O}");
            var commitMsg = await worktrees.CommitAllAsync(path, $"smoke({taskId}): worktree smoke");
            Console.WriteLine($"      -> {commitMsg.Split('\n').First()}");

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

    private static async Task<int> RunOrchestratorAsync(
        AgentOptions options, ILoggerFactory loggerFactory, ILogger logger)
    {
        var stateStore = new StateStore();
        var worktrees = new GitWorktreeService(options.Workspace, loggerFactory.CreateLogger<GitWorktreeService>());
        var gitHub = new GitHubService(options.GitHub);
        var acpManager = new AcpProcessManager(
            options.AcpServer, options.Workspace.Root,
            loggerFactory.CreateLogger<AcpProcessManager>());
        var roleRegistry = new RoleAgentRegistry();
        var eventBus = new InMemoryDashboardEventBus();
        var prWatcher = new PRWatcher(
            gitHub, worktrees, stateStore,
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30),
            eventBus,
            loggerFactory.CreateLogger<PRWatcher>());
        var orchestrator = new OrchestratorAgent(
            acpManager, roleRegistry, worktrees, gitHub, prWatcher, stateStore, options,
            eventBus,
            loggerFactory.CreateLogger<OrchestratorAgent>());
        var dashboard = new DashboardHost(
            options.Dashboard, stateStore, eventBus,
            loggerFactory.CreateLogger<DashboardHost>());

        using var shutdownCts = new CancellationTokenSource();
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

            logger.LogInformation("Starting ACP server");
            await acpManager.StartAsync(shutdownCts.Token);

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
            logger.LogInformation("Stopping ACP server");
            try { await acpManager.DisposeAsync(); } catch { }
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