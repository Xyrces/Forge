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
    private readonly IIssueStore _issues;
    private SpawnerOptions _spawnerOptions = new();
    private WorkspaceOptions _workspaceOptions = new();
    private readonly ILogger<OrchestratorAgent> _logger;
    private readonly IDashboardEventBus _events;
    private SemaphoreSlim _concurrencyLimiter = new(4);
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
        IIssueStore issues,
        IDashboardEventBus events,
        ILogger<OrchestratorAgent> logger)
    {
        _acpManager = acpManager;
        _roleRegistry = roleRegistry;
        _worktrees = worktrees;
        _gitHub = gitHub;
        _prWatcher = prWatcher;
        _issues = issues;
        _events = events;
        _logger = logger;
        _concurrencyLimiter = new SemaphoreSlim(4);
        _maxRetryCount = 1;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Status = AgentStatus.Running;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await DispatchCycleAsync(cancellationToken);
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
        => Task.FromResult(new Result(false, "Direct AgentTask dispatch is deprecated; enqueue via the CLI instead"));

    private async Task DispatchCycleAsync(CancellationToken cancellationToken)
    {
        var ready = await _issues.ReadyAsync(_spawnerOptions.MaxConcurrentSessions, cancellationToken);

        var watchTasks = ready.Where(i => i.Type == AgentTaskTypes.PrWatch).ToList();
        foreach (var watch in watchTasks)
            _ = Task.Run(() => ProcessWatchIssueAsync(watch, cancellationToken), cancellationToken);

        var devTasks = ready.Where(i => i.Type != AgentTaskTypes.PrWatch).ToList();
        foreach (var dev in devTasks)
            _ = Task.Run(() => DispatchSingleTaskAsync(dev, cancellationToken), cancellationToken);
    }

    private async Task<Result> ProcessWatchIssueAsync(IssueRecord watchIssue, CancellationToken cancellationToken)
    {
        try
        {
            await _prWatcher.ProcessWatchTaskAsync(watchIssue, cancellationToken);
            return new Result(true, $"Watch {watchIssue.Id} complete");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watch issue {Id} crashed", watchIssue.Id);
            return new Result(false, ex.Message);
        }
    }

    public async Task<Result> DispatchSingleTaskAsync(IssueRecord issue, CancellationToken cancellationToken)
    {
        await _concurrencyLimiter.WaitAsync(cancellationToken);
        var startedAt = DateTime.UtcNow;
        try
        {
            // Atomic claim so two orchestrators can't grab the same issue.
            var claimed = await _issues.ClaimAsync(issue.Id, "kilo", cancellationToken);
            if (claimed is null)
            {
                _logger.LogDebug("Issue {Id} already claimed elsewhere", issue.Id);
                return new Result(false, "already-claimed");
            }

            await PublishTransition(claimed, IssueStatus.Pending, IssueStatus.InProgress, null, cancellationToken);

            var roleAgent = _roleRegistry.ForType(RoleAgentRegistry.FromTaskType(claimed.Type));
            var branch = claimed.GetMetadata("branch") ?? $"agent/{claimed.Id}";
            var worktreePath = await _worktrees.CreateAsync(claimed.Id, _workspaceOptions.DefaultBranch, cancellationToken);

            await UpdateMetadataAsync(claimed.Id, m => MergeDict(m, new Dictionary<string, object>
            {
                ["worktreePath"] = worktreePath,
                ["branch"] = branch,
                ["roleAgent"] = roleAgent.KiloAgentName,
            }), cancellationToken);

            var client = _acpManager.GetClient();
            var newSession = await client.NewSessionAsync(
                new NewSessionParams(worktreePath, roleAgent.KiloAgentName), cancellationToken);
            await UpdateMetadataAsync(claimed.Id, m => MergeDict(m, new Dictionary<string, object>
            {
                ["acpSessionId"] = newSession.SessionId,
            }), cancellationToken);

            var claimedRefresh = (await _issues.GetAsync(claimed.Id, cancellationToken))!;
            var session = new AcpSession(client, newSession.SessionId, worktreePath, roleAgent.KiloAgentName);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.AcpSessionStarted,
                claimed.Id, $"session={newSession.SessionId} role={roleAgent.KiloAgentName}"));
            var prompt = BuildPrompt(claimedRefresh, roleAgent, worktreePath, branch, _workspaceOptions.DefaultBranch);

            // Capture the prompt result in metadata so the dashboard can show
            // what the agent said even when downstream steps fail. Capture
            // BEFORE we use `result` so the failure path doesn't lose it.
            PromptResult result;
            try
            {
                result = await session.PromptAsync(prompt, cancellationToken);
            }
            catch (Exception promptEx)
            {
                await RecordModelResponseMetadataAsync(claimed.Id, error: $"prompt-threw: {promptEx.GetType().Name}: {promptEx.Message}", ct: cancellationToken);
                throw;
            }
            await RecordModelResponseMetadataAsync(claimed.Id, response: result.Response, ct: cancellationToken);

            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.AcpSessionCompleted,
                claimed.Id, $"elapsed={session.Elapsed.TotalMilliseconds:F0}ms",
                new Dictionary<string, object?> { ["sessionId"] = newSession.SessionId, ["elapsedMs"] = session.Elapsed.TotalMilliseconds }));
            _logger.LogInformation("ACP session for {Id} completed in {Ms}ms",
                claimed.Id, session.Elapsed.TotalMilliseconds);

            var commit = await _worktrees.CommitAllAsync(worktreePath, $"Task({claimed.Id}): {claimed.Title}", cancellationToken);
            if (!commit.HasChanges)
            {
                _logger.LogWarning("Issue {Id}: model produced no diff (no files committed). Marking Completed with lastResponse captured.", claimed.Id);
                await _issues.TransitionAsync(claimed.Id, IssueStatus.Completed, "no changes (agent made 0 edits)", ct: cancellationToken);
                _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.TaskTransition,
                    claimed.Id, "Completed (no-op)", new Dictionary<string, object?> { ["response"] = Truncate(result.Response ?? "", 400) }));
                return new Result(true, "completed with no diff");
            }

            await _worktrees.PushAsync(worktreePath, branch, cancellationToken);
            var headSha = await _worktrees.GetHeadShaAsync(worktreePath, cancellationToken);

            var pr = await _gitHub.CreatePullRequestAsync(
                title: $"[{claimed.Type}] {claimed.Title}",
                body: BuildPrBody(claimed, roleAgent, headSha, result),
                headBranch: branch,
                baseBranch: _workspaceOptions.DefaultBranch,
                cancellationToken: cancellationToken);

            await UpdateMetadataAsync(claimed.Id, m => MergeDict(m, new Dictionary<string, object>
            {
                ["prNumber"] = pr.Number,
                ["branchSha"] = headSha,
            }), cancellationToken);
            _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.PrOpened,
                claimed.Id, $"PR #{pr.Number} -> {branch}",
                new Dictionary<string, object?> { ["prNumber"] = pr.Number, ["branch"] = branch, ["sha"] = headSha }));
            _logger.LogInformation("Opened PR #{PrNumber} for {Id}", pr.Number, claimed.Id);

            await EnqueueWatchIssueAsync(claimed.Id, pr.Number, branch, worktreePath, cancellationToken);
            _logger.LogInformation("Task {Id} dispatched to PR #{PrNumber} (duration {Ms}ms)",
                claimed.Id, pr.Number, (DateTime.UtcNow - startedAt).TotalMilliseconds);
            return new Result(true, $"PR #{pr.Number} opened");
        }
        catch (OperationCanceledException)
        {
            await SafeTransitionAsync(issue.Id, IssueStatus.Failed, "cancelled", cancellationToken);
            return new Result(false, "cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task {Id} failed", issue.Id);
            await HandleFailureAsync(issue, ex, cancellationToken);
            return new Result(false, ex.Message);
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    internal void BindOptions(AgentOptions options)
    {
        _spawnerOptions = options.Spawner;
        _workspaceOptions = options.Workspace;
        _concurrencyLimiter.Dispose();
        _concurrencyLimiter = new SemaphoreSlim(Math.Max(1, options.Spawner.MaxConcurrentSessions));
    }

    private async Task HandleFailureAsync(IssueRecord issue, Exception ex, CancellationToken cancellationToken)
    {
        var retryCount = 0;
        var prev = issue.GetMetadata("retryCount");
        if (prev is not null && int.TryParse(prev, out var r)) retryCount = r;
        var worktreePath = issue.GetMetadata("worktreePath");

        if (retryCount < _maxRetryCount)
        {
            await UpdateMetadataAsync(issue.Id, m => MergeDict(m, new Dictionary<string, object>
            {
                ["retryCount"] = retryCount + 1
            }), cancellationToken);
            await SafeTransitionAsync(issue.Id, IssueStatus.Pending, ex.Message, cancellationToken);
            _logger.LogWarning("Issue {Id} will be retried (attempt {N})", issue.Id, retryCount + 1);
        }
        else
        {
            await SafeTransitionAsync(issue.Id, IssueStatus.Failed, ex.Message, cancellationToken);
            if (!string.IsNullOrEmpty(worktreePath))
            {
                try { await _worktrees.RemoveAsync(issue.Id, cancellationToken); }
                catch (Exception wx) { _logger.LogWarning(wx, "Worktree removal failed"); }
            }
        }
    }

    private async Task EnqueueWatchIssueAsync(
        string devIssueId, int prNumber, string branch, string worktreePath, CancellationToken ct)
    {
        var watch = await _issues.CreateAsync(new NewIssue(
            Type: AgentTaskTypes.PrWatch,
            Title: $"Watch PR #{prNumber} for {devIssueId}",
            Description: $"Wait for PR #{prNumber} to be reviewed.",
            Metadata: new Dictionary<string, object>
            {
                ["prNumber"] = prNumber,
                ["branch"] = branch,
                ["worktreePath"] = worktreePath,
                ["taskId"] = devIssueId,
            }), ct);
        _logger.LogInformation("Enqueued watch issue {Id} for PR #{PrNumber}", watch.Id, prNumber);
    }

    private async Task RecordModelResponseMetadataAsync(string id, string? response = null, string? error = null, CancellationToken ct = default)
    {
        try
        {
            var current = await _issues.GetAsync(id, ct);
            if (current is null) return;
            await _issues.TransitionAsync(id, current.Status,
                error: error ?? current.GetMetadata("lastError"),
                metadata: new Dictionary<string, object>
                {
                    ["modelResponse"] = Truncate(response ?? "", 2000),
                    ["lastError"] = error ?? current.GetMetadata("lastError") ?? "",
                },
                ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record modelResponse metadata for {Id}", id);
        }
    }

    private async Task UpdateMetadataAsync(string id, Func<Dictionary<string, object>, Dictionary<string, object>> mutate, CancellationToken ct)
    {
        var current = await _issues.GetAsync(id, ct);
        if (current is null) return;
        using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrEmpty(current.MetadataJson) ? "{}" : current.MetadataJson);
        var dict = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
            dict[prop.Name] = System.Text.Json.JsonSerializer.Deserialize<object>(prop.Value.GetRawText())!;
        var merged = mutate(dict);
        await _issues.TransitionAsync(id, current.Status, current.GetMetadata("lastError"),
            metadata: merged, ct: ct);
    }

    private async Task SafeTransitionAsync(string id, IssueStatus to, string? error, CancellationToken ct)
    {
        try { await _issues.TransitionAsync(id, to, error, ct: ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Transition failed for {Id}", id); }
    }

    private async Task PublishTransition(IssueRecord issue, IssueStatus from, IssueStatus to, string? error, CancellationToken ct)
    {
        _logger.LogInformation("Issue {Id} transition {From} -> {To} (type={Type})", issue.Id, from, to, issue.Type);
        _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.TaskTransition,
            issue.Id, $"{from} -> {to}",
            new Dictionary<string, object?>
            {
                ["from"] = from.ToString(),
                ["to"] = to.ToString(),
                ["type"] = issue.Type,
                ["error"] = error
            }));
    }

    private static Dictionary<string, object> MergeDict(
        Dictionary<string, object> existing,
        IReadOnlyDictionary<string, object> additions)
    {
        var merged = new Dictionary<string, object>(existing, StringComparer.Ordinal);
        foreach (var kv in additions)
            merged[kv.Key] = kv.Value;
        return merged;
    }

    private static string BuildPrompt(IssueRecord issue, RoleAgent role, string worktreePath, string branch, string? defaultBranch)
        => $"""
            You are acting as the **{role.KiloAgentName}** agent for the PortHorizon project.
            Working directory: {worktreePath}
            Branch: {branch} (base: {defaultBranch ?? "main"})

            ## Task
            Type: {issue.Type}
            Id: {issue.Id}
            Title: {issue.Title}

            ## Allowed tools
            {string.Join(", ", role.AllowedTools)}

            ## Rules
            - Make focused, minimal changes that fulfill the task description.
            - Run `dotnet build` and `dotnet test` on the projects you touch before committing.
            - Commit your work with message: `Task({issue.Id}): <summary>`.
            - Push the branch when done.
            - Do NOT open a PR; the orchestrator handles that.
            - Do NOT touch files outside your project subdirectory ({role.ProjectSubdir}).
            """;

    private static string BuildPrBody(IssueRecord issue, RoleAgent role, string sha, PromptResult result)
        => $"""
            ## Summary
            Automated change for issue `{issue.Id}` (type: {issue.Type}, role: {role.KiloAgentName}).

            ## Description
            {issue.Description}

            ## Verification
            - HEAD SHA: `{sha}`
            - ACP session result (truncated): `{Truncate(result.Response, 400)}`

            Closes `{issue.Id}`.
            """;

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

}
