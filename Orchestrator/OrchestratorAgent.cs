using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Acp;
using PortHorizon.Agents.AgentTools;
using PortHorizon.Agents.Agents;
using PortHorizon.Agents.Configuration;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;
using PortHorizon.Agents.Reviewer;

namespace PortHorizon.Agents.Orchestrator;

public sealed class OrchestratorAgent : IAgent
{
    private readonly AcpProcessManager _acpManager;
    private readonly RoleAgentRegistry _roleRegistry;
    private readonly GitWorktreeService _worktrees;
    private readonly GitHubService _gitHub;
    private readonly PRWatcher _prWatcher;
    private readonly StateStore _state;
    private readonly SpawnerOptions _spawnerOptions;
    private readonly WorkspaceOptions _workspaceOptions;
    private readonly ILogger<OrchestratorAgent> _logger;
    private readonly IDashboardEventBus _events;
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly int _maxRetryCount;

    public string Id => "orchestrator";
    public string Name => "OrchestratorAgent";
    public AgentType Type => AgentType.Orchestrator;
    public AgentStatus Status { get; private set; } = AgentStatus.Idle;

    public OrchestratorAgent(
        AcpProcessManager acpManager,
        RoleAgentRegistry roleRegistry,
        GitWorktreeService worktrees,
        GitHubService gitHub,
        PRWatcher prWatcher,
        StateStore state,
        AgentOptions options,
        IDashboardEventBus events,
        ILogger<OrchestratorAgent> logger)
    {
        _acpManager = acpManager;
        _roleRegistry = roleRegistry;
        _worktrees = worktrees;
        _gitHub = gitHub;
        _prWatcher = prWatcher;
        _state = state;
        _spawnerOptions = options.Spawner;
        _workspaceOptions = options.Workspace;
        _events = events;
        _logger = logger;
        _concurrencyLimiter = new SemaphoreSlim(options.Spawner.MaxConcurrentSessions);
        _maxRetryCount = 1;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Status = AgentStatus.Running;
        try
        {
            await RunReaperAsync(cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                await DispatchCycleAsync(cancellationToken);
                await _state.SaveStateAsync(await _state.LoadStateAsync(cancellationToken), cancellationToken);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_spawnerOptions.PollIntervalSeconds), cancellationToken);
                }
                catch (OperationCanceledException) { break; }
            }
            Status = AgentStatus.Idle;
        }
        catch
        {
            Status = AgentStatus.Error;
            throw;
        }
    }

    public Task<Result> ProcessTaskAsync(AgentTask task, CancellationToken cancellationToken = default)
        => DispatchSingleTaskAsync(task, cancellationToken);

    private async Task DispatchCycleAsync(CancellationToken cancellationToken)
    {
        var state = await _state.LoadStateAsync(cancellationToken);
        var watchTasks = state.Tasks
            .Where(t => t.Type == AgentTaskTypes.PrWatch && t.Status == AgentTaskStatus.Pending)
            .ToList();
        foreach (var watch in watchTasks)
            _ = Task.Run(() => ProcessWatchTaskAsync(watch, cancellationToken), cancellationToken);

        var devTasks = state.Tasks
            .Where(t => t.Type != AgentTaskTypes.PrWatch && t.Status == AgentTaskStatus.Pending)
            .OrderBy(t => t.CreatedAt)
            .ToList();
        foreach (var dev in devTasks)
            _ = Task.Run(() => DispatchSingleTaskAsync(dev, cancellationToken), cancellationToken);
    }

    private async Task<Result> ProcessWatchTaskAsync(AgentTask watchTask, CancellationToken cancellationToken)
    {
        try
        {
            await _prWatcher.ProcessWatchTaskAsync(watchTask, cancellationToken);
            return new Result(true, $"Watch task {watchTask.Id} complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watch task {TaskId} crashed", watchTask.Id);
            return new Result(false, ex.Message);
        }
    }

    public async Task<Result> DispatchSingleTaskAsync(AgentTask task, CancellationToken cancellationToken)
    {
        await _concurrencyLimiter.WaitAsync(cancellationToken);
        var startedAt = DateTime.UtcNow;
        try
        {
            await TransitionAsync(task.Id, AgentTaskStatus.InProgress, error: null, cancellationToken);

            var roleAgent = _roleRegistry.ForType(RoleAgentRegistry.FromTaskType(task.Type));
            var branch = $"agent/{task.Id}";
            var worktreePath = await _worktrees.CreateAsync(task.Id, _workspaceOptions.DefaultBranch, cancellationToken);

            await UpdateParamsAsync(task.Id, p => MergeParams(p, new Dictionary<string, object>
            {
                ["worktreePath"] = worktreePath,
                ["branch"] = branch,
                ["roleAgent"] = roleAgent.KiloAgentName,
            }), cancellationToken);

            var client = _acpManager.GetClient();
            var newSession = await client.NewSessionAsync(
                new NewSessionParams(worktreePath, roleAgent.KiloAgentName), cancellationToken);
            await UpdateParamsAsync(task.Id, p => MergeParams(p, new Dictionary<string, object>
            {
                ["acpSessionId"] = newSession.SessionId,
            }), cancellationToken);

            var session = new AcpSession(client, newSession.SessionId, worktreePath, roleAgent.KiloAgentName);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.AcpSessionStarted,
                task.Id, $"session={newSession.SessionId} role={roleAgent.KiloAgentName}"));
            var prompt = BuildPrompt(task, roleAgent, worktreePath, branch);
            var result = await session.PromptAsync(prompt, cancellationToken);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.AcpSessionCompleted,
                task.Id, $"elapsed={session.Elapsed.TotalMilliseconds:F0}ms",
                new Dictionary<string, object?> { ["sessionId"] = newSession.SessionId, ["elapsedMs"] = session.Elapsed.TotalMilliseconds }));
            _logger.LogInformation("ACP session for {TaskId} completed in {Ms}ms",
                task.Id, session.Elapsed.TotalMilliseconds);

            await _worktrees.CommitAllAsync(worktreePath, $"Task({task.Id}): {task.Description}", cancellationToken);
            await _worktrees.PushAsync(worktreePath, branch, cancellationToken);
            var headSha = await _worktrees.GetHeadShaAsync(worktreePath, cancellationToken);

            var pr = await _gitHub.CreatePullRequestAsync(
                title: $"[{task.Type}] {task.Description}",
                body: BuildPrBody(task, roleAgent, headSha, result),
                headBranch: branch,
                baseBranch: _workspaceOptions.DefaultBranch,
                cancellationToken: cancellationToken);

            await UpdateParamsAsync(task.Id, p => MergeParams(p, new Dictionary<string, object>
            {
                ["prNumber"] = pr.Number,
                ["branchSha"] = headSha,
            }), cancellationToken);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.PrOpened,
                task.Id, $"PR #{pr.Number} -> {branch}",
                new Dictionary<string, object?> { ["prNumber"] = pr.Number, ["branch"] = branch, ["sha"] = headSha }));
            _logger.LogInformation("Opened PR #{PrNumber} for {TaskId}", pr.Number, task.Id);

            await EnqueueWatchTaskAsync(task, pr.Number, branch, worktreePath, cancellationToken);
            _logger.LogInformation("Task {TaskId} dispatched to PR #{PrNumber} (duration {Ms}ms)",
                task.Id, pr.Number, (DateTime.UtcNow - startedAt).TotalMilliseconds);
            return new Result(true, $"PR #{pr.Number} opened");
        }
        catch (OperationCanceledException)
        {
            await TransitionAsync(task.Id, AgentTaskStatus.Failed, "cancelled", cancellationToken);
            return new Result(false, "cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task {TaskId} failed", task.Id);
            await HandleFailureAsync(task, ex, cancellationToken);
            return new Result(false, ex.Message);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private async Task HandleFailureAsync(AgentTask task, Exception ex, CancellationToken cancellationToken)
    {
        var retryCount = task.Parameters.GetValueOrDefault("retryCount") as int? ?? 0;
        var worktreePath = task.Parameters.GetValueOrDefault("worktreePath") as string;
        if (retryCount < _maxRetryCount)
        {
            await UpdateParamsAsync(task.Id,
                p => MergeParams(p, new Dictionary<string, object> { ["retryCount"] = retryCount + 1 }),
                cancellationToken);
            await TransitionAsync(task.Id, AgentTaskStatus.Pending, ex.Message, cancellationToken);
            _logger.LogWarning("Task {TaskId} will be retried (attempt {N})", task.Id, retryCount + 1);
        }
        else
        {
            await TransitionAsync(task.Id, AgentTaskStatus.Failed, ex.Message, cancellationToken);
            if (!string.IsNullOrEmpty(worktreePath))
            {
                try { await _worktrees.RemoveAsync(task.Id, cancellationToken); }
                catch (Exception wx) { _logger.LogWarning(wx, "Worktree removal failed"); }
            }
        }
    }

    private async Task EnqueueWatchTaskAsync(
        AgentTask devTask, int prNumber, string branch, string worktreePath, CancellationToken ct)
    {
        var watch = new AgentTask(
            Id: $"{devTask.Id}-watch",
            Type: AgentTaskTypes.PrWatch,
            Description: $"Watch PR #{prNumber} for {devTask.Id}",
            Parameters: new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["prNumber"] = prNumber,
                ["branch"] = branch,
                ["worktreePath"] = worktreePath,
                ["taskId"] = devTask.Id,
            },
            Branch: branch,
            Status: AgentTaskStatus.Pending,
            Error: null,
            CreatedAt: DateTime.UtcNow);
        var state = await _state.LoadStateAsync(ct);
        state.Tasks.Add(watch);
        await _state.SaveStateAsync(state, ct);
    }

    private async Task RunReaperAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = await _state.LoadStateAsync(cancellationToken);
            var staleAfter = TimeSpan.FromMinutes(_spawnerOptions.StaleMinutes);
            var swept = StateReaper.ReapStaleTasks(state, staleAfter, _maxRetryCount,
                worktreeExists: path => _worktrees.WorktreeExistsAsync(path, cancellationToken).Result ? path : null);
            if (swept.Tasks.Any(t =>
                t.Status != AgentTaskStatus.InProgress &&
                state.Tasks.FirstOrDefault(s => s.Id == t.Id)?.Status == AgentTaskStatus.InProgress))
            {
                _logger.LogWarning("Reaper swept stale InProgress tasks");
                await _state.SaveStateAsync(swept, cancellationToken);
            }
        }
        catch (StateCorruptException ex)
        {
            _logger.LogCritical(ex, "State file corrupt; refusing to start");
            throw;
        }
        catch (StateSchemaException ex)
        {
            _logger.LogCritical(ex, "State schema mismatch; refusing to start");
            throw;
        }
    }

    private async Task TransitionAsync(string taskId, AgentTaskStatus to, string? error, CancellationToken ct)
    {
        var state = await _state.LoadStateAsync(ct);
        var idx = state.Tasks.FindIndex(t => t.Id == taskId);
        if (idx < 0) return;
        var task = state.Tasks[idx];
        var from = task.Status;
        var newTask = (to, task.CompletedAt) switch
        {
            (AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Blocked, _) =>
                task with { Status = to, Error = error, UpdatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow },
            _ => task with { Status = to, Error = error, UpdatedAt = DateTime.UtcNow },
        };
        state.Tasks[idx] = newTask;
        if (to == AgentTaskStatus.Completed) state.CompletedTasks++;
        if (to == AgentTaskStatus.Failed) state.FailedTasks++;
        await _state.SaveStateAsync(state, ct);
        _logger.LogInformation("Task {TaskId} transition {From} -> {To} (agent={Type})",
            taskId, from, to, task.Type);
        _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.TaskTransition,
            taskId, $"{from} -> {to}",
            new Dictionary<string, object?>
            {
                ["from"] = from.ToString(),
                ["to"] = to.ToString(),
                ["type"] = task.Type,
                ["error"] = error
            }));
    }

    private async Task UpdateParamsAsync(string taskId, Func<Dictionary<string, object>, Dictionary<string, object>> mutate, CancellationToken ct)
    {
        var state = await _state.LoadStateAsync(ct);
        var idx = state.Tasks.FindIndex(t => t.Id == taskId);
        if (idx < 0) return;
        var task = state.Tasks[idx];
        state.Tasks[idx] = task with
        {
            Parameters = mutate(task.Parameters),
            UpdatedAt = DateTime.UtcNow,
        };
        await _state.SaveStateAsync(state, ct);
    }

    private static Dictionary<string, object> MergeParams(
        Dictionary<string, object> existing,
        IReadOnlyDictionary<string, object> additions)
    {
        var merged = new Dictionary<string, object>(existing, StringComparer.Ordinal);
        foreach (var kv in additions)
            merged[kv.Key] = kv.Value;
        return merged;
    }

    private static string BuildPrompt(AgentTask task, RoleAgent role, string worktreePath, string branch)
        => $"""
            You are acting as the **{role.KiloAgentName}** agent for the PortHorizon project.
            Working directory: {worktreePath}
            Branch: {branch} (base: main)

            ## Task
            Type: {task.Type}
            Id: {task.Id}
            Description: {task.Description}

            ## Allowed tools
            {string.Join(", ", role.AllowedTools)}

            ## Rules
            - Make focused, minimal changes that fulfill the task description.
            - Run `dotnet build` and `dotnet test` on the projects you touch before committing.
            - Commit your work with message: `Task({task.Id}): <summary>`.
            - Push the branch when done.
            - Do NOT open a PR; the orchestrator handles that.
            - Do NOT touch files outside your project subdirectory ({role.ProjectSubdir}).
            """;

    private static string BuildPrBody(AgentTask task, RoleAgent role, string sha, PromptResult result)
        => $"""
            ## Summary
            Automated change for task `{task.Id}` (type: {task.Type}, role: {role.KiloAgentName}).

            ## Description
            {task.Description}

            ## Verification
            - HEAD SHA: `{sha}`
            - ACP session result (truncated): `{Truncate(result.Response, 400)}`

            Closes task `{task.Id}`.
            """;
    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}