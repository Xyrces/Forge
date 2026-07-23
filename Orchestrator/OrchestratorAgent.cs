using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Reviewer;

namespace Forge.Orchestrator;

public sealed class OrchestratorAgent : IAgent
{
    private readonly IProjectStore _projectStore;
    private readonly IProjectDispatchBundleFactory _bundleFactory;
    private readonly RoleAgentRegistry _roleRegistry;
    private readonly IAgentRunner _runner;
    private readonly AgentMessageBus _messageBus;
    private readonly IWorkflowDispatcher _dispatcher;
    private readonly IDashboardEventBus _events;
    private readonly ILogger<OrchestratorAgent> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ConcurrentDictionary<string, ProjectDispatchBundle> _bundles = new();
    private SpawnerOptions _spawnerOptions = new();
    private SemaphoreSlim _concurrencyLimiter = new(4);
    private readonly int _maxRetryCount;
    // GitHub rate-limit cooldown for the PR-watch path. When Octokit
    // reports RateLimitExceeded, watch issues are skipped until this
    // time so the loop doesn't hammer the API every dispatch cycle.
    private DateTime _githubRateLimitedUntil = DateTime.MinValue;
    private static readonly TimeSpan GitHubRateLimitCooldown = TimeSpan.FromMinutes(10);
    // Watch sweep cadence. Previously every dispatch cycle spawned a
    // parallel Task.Run per Pending watch, and ProcessWatchTaskAsync
    // looped internally every 30s with 3 API calls per iteration —
    // loops multiplied unboundedly (watches are never claimed, so they
    // stay Pending) and vaporized the 5000-req/hr GitHub quota. Now
    // watches are polled in ONE sequential sweep every WatchSweepInterval:
    // 3 calls per watch per sweep (2 watches -> ~24 calls/hr).
    private DateTime _nextWatchSweepUtc = DateTime.MinValue;
    private static readonly TimeSpan WatchSweepInterval = TimeSpan.FromMinutes(15);
    // LLM 429 cooldown for the engineering dispatch path. Free-tier
    // providers rate-limit parallel agent runs; a 429 re-queues the
    // task (not a code failure) and pauses new dev dispatches.
    private DateTime _llmRateLimitedUntil = DateTime.MinValue;
    private static readonly TimeSpan LlmRateLimitCooldown = TimeSpan.FromMinutes(3);

    public string Id => "orchestrator";
    public string Name => "OrchestratorAgent";
    public AgentType Type => AgentType.Orchestrator;
    public AgentStatus Status { get; private set; } = AgentStatus.Idle;

    public OrchestratorAgent(
        IProjectStore projectStore,
        IProjectDispatchBundleFactory bundleFactory,
        IAgentRunner runner,
        RoleAgentRegistry roleRegistry,
        AgentMessageBus messageBus,
        IWorkflowDispatcher dispatcher,
        IDashboardEventBus events,
        ILogger<OrchestratorAgent> logger,
        ILoggerFactory? loggerFactory = null)
    {
        _projectStore = projectStore;
        _bundleFactory = bundleFactory;
        _runner = runner;
        _roleRegistry = roleRegistry;
        _messageBus = messageBus;
        _dispatcher = dispatcher;
        _events = events;
        _logger = logger;
        _loggerFactory = loggerFactory;
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

    /// <summary>
    /// One iteration of the dispatch loop. Walks every registered
    /// project, asks each project's bundle for its ready queue, and
    /// dispatches. Runtime-added projects (via POST /api/projects)
    /// are picked up on the next cycle without a service restart.
    /// </summary>
    private async Task DispatchCycleAsync(CancellationToken cancellationToken)
    {
        var projectRecords = await _projectStore.ListAsync(cancellationToken);
        if (projectRecords.Count == 0) return;

        foreach (var record in projectRecords)
        {
            if (cancellationToken.IsCancellationRequested) return;
            var bundle = GetOrCreateBundle(ProjectRecordToOptions(record));
            if (bundle is null) continue;

            try
            {
                var activeSprint = await bundle.Sprints.GetActiveAsync(cancellationToken);

                // Fetch the full ready queue (limit 0) and filter in
                // memory: containers (epic/story) clog the queue head
                // when the LIMIT is applied before filtering, so real
                // tasks behind them never dispatch (found live: 7
                // stories + a watch starved 4 feature tasks).
                var allReady = await bundle.IssueStore.ReadyAsync(0, sprintId: null, cancellationToken);

                // Watches are lifecycle subscriptions, not sprint
                // work: they sweep from the FULL queue regardless of
                // sprint state (a sprint-scoped fetch would starve
                // them — watches are never linked to sprints).
                var watchTasks = allReady.Where(i => i.Type == AgentTaskTypes.PrWatch).ToList();
                if (watchTasks.Count > 0 && DateTime.UtcNow < _githubRateLimitedUntil)
                {
                    _logger.LogDebug("Dispatch cycle: skipping {N} watch issues — GitHub rate-limit cooldown until {Until:HH:mm:ss}",
                        watchTasks.Count, _githubRateLimitedUntil);
                    watchTasks = new List<IssueRecord>();
                }
                if (watchTasks.Count > 0 && DateTime.UtcNow >= _nextWatchSweepUtc)
                {
                    _nextWatchSweepUtc = DateTime.UtcNow + WatchSweepInterval;
                    await RunWatchSweepAsync(watchTasks, bundle, cancellationToken);
                }

                // Sprint flow gate: ALL engineering work happens inside
                // a sprint. No active sprint => the SprintAssembler
                // hasn't ingested work yet (or nothing is eligible);
                // until then only design/planning stages run.
                if (activeSprint is null)
                {
                    _logger.LogDebug("Dispatch cycle: no active sprint (project={Project}) — engineering dispatch gated off", bundle.Project.Id);
                    continue;
                }
                var sprintMemberIds = new HashSet<string>(
                    await bundle.Sprints.GetIssueIdsAsync(activeSprint.Id, cancellationToken),
                    StringComparer.Ordinal);

                // Engineering dispatch skips pipeline containers.
                // Epics and stories feed the spec -> groom chain;
                // they are not units of engineering work. (Found by
                // the first UI e2e: an intake-accepted epic was
                // claimed directly and implemented, bypassing the
                // entire pipeline.) All other types dispatch,
                // preserving operator-enqueued type names (dev, ecs,
                // ui, bug, ...).
                var devTasks = allReady.Where(i => i.Type != AgentTaskTypes.PrWatch
                    && !AgentTaskTypes.IsContainer(i.Type)
                    && sprintMemberIds.Contains(i.Id))
                    .Take(_spawnerOptions.MaxConcurrentSessions)
                    .ToList();
                if (devTasks.Count > 0 && DateTime.UtcNow < _llmRateLimitedUntil)
                {
                    _logger.LogDebug("Dispatch cycle: skipping {N} dev tasks — LLM rate-limit cooldown until {Until:HH:mm:ss}",
                        devTasks.Count, _llmRateLimitedUntil);
                    continue;
                }
                foreach (var dev in devTasks)
                    _ = Task.Run(() => DispatchSingleTaskAsync(dev, bundle, cancellationToken), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dispatch cycle for project '{Id}' crashed; skipping this cycle for that project", bundle.Project.Id);
            }
        }
    }

    private ProjectDispatchBundle? GetOrCreateBundle(ProjectOptions project)
    {
        return _bundles.GetOrAdd(project.Id, _ =>
        {
            try
            {
                _logger.LogInformation("Constructing dispatch bundle for project '{Id}' (root={Root}, repo={Repo})",
                    project.Id, project.Root, project.RepoUrl);
                return _bundleFactory.Build(project);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build dispatch bundle for project '{Id}'", project.Id);
                return null!;
            }
        });
    }

    private static ProjectOptions ProjectRecordToOptions(ProjectRecord r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        RepoUrl = r.RepoUrl,
        DefaultBranch = r.DefaultBranch,
        Root = string.Empty,
    };

    private static bool IsLlmRateLimited(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.ClientModel.ClientResultException cre && cre.Status == 429) return true;
            var msg = e.Message;
            if (!msg.Contains("429")) continue;
            if (msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Status: 429"))
                return true;
        }
        return false;
    }

    /// <summary>
    /// One sequential poll over every Pending watch issue — a single
    /// GitHub burst per <see cref="WatchSweepInterval"/> instead of
    /// unbounded parallel poll loops. Each watch first gets its
    /// reviewer pass (ReviewerDispatcher records the verdict in the
    /// watch metadata), then the merge/rework decision
    /// (PRWatcher.PollWatchOnceAsync). A 429 aborts the sweep early
    /// and arms the cooldown. Watch issues stay Pending between
    /// sweeps (by design: the watch IS a long-lived subscription).
    /// </summary>
    private async Task RunWatchSweepAsync(IReadOnlyList<IssueRecord> watchTasks, ProjectDispatchBundle bundle, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Watch sweep: polling {N} watch issue(s) (project={Project})",
            watchTasks.Count, bundle.Project.Id);
        foreach (var watch in watchTasks)
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                // Review first (verdict metadata), then decide. The
                // reviewer is constructed per sweep — it shares the
                // bundle's stores + the shared agent runner.
                var reviewer = new Forge.Reviewer.ReviewerDispatcher(
                    bundle.IssueStore, bundle.GitHub, _runner,
                    _loggerFactory?.CreateLogger<Forge.Reviewer.ReviewerDispatcher>()
                        ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Forge.Reviewer.ReviewerDispatcher>.Instance);
                var fresh = watch;
                var outcome = await reviewer.ReviewOnceAsync(watch, cancellationToken);
                if (outcome is not null)
                {
                    // The review updated the watch's metadata; re-read
                    // so the poll sees the fresh verdict.
                    fresh = await bundle.IssueStore.GetAsync(watch.Id, cancellationToken) ?? watch;
                }
                var poll = await bundle.PrWatcher.PollWatchOnceAsync(fresh, cancellationToken);
                _logger.LogDebug("Watch {Id}: {Outcome}", watch.Id, poll);
            }
            catch (Octokit.RateLimitExceededException)
            {
                _githubRateLimitedUntil = DateTime.UtcNow + GitHubRateLimitCooldown;
                _logger.LogWarning("Watch sweep: GitHub rate limit exceeded; backing off for {Cooldown} (project={Project})",
                    GitHubRateLimitCooldown, bundle.Project.Id);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watch issue {Id} crashed (project={Project})", watch.Id, bundle.Project.Id);
            }
            // Courtesy delay: GitHub's secondary rate limit dislikes
            // rapid-fire request bursts even well under the quota.
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task<Result> DispatchSingleTaskAsync(IssueRecord issue, ProjectDispatchBundle bundle, CancellationToken cancellationToken)
    {
        await _concurrencyLimiter.WaitAsync(cancellationToken);
        var startedAt = DateTime.UtcNow;
        try
        {
            // P3 (final wiring): dispatch is now driven by the MAF
            // Workflows pipeline. ClaimExecutor detects the
            // pre-claim (InProgress + assignee=forge) and passes
            // through; otherwise it claims itself.
            var claimed = await bundle.IssueStore.ClaimAsync(issue.Id, "forge", cancellationToken);
            if (claimed is null)
            {
                _logger.LogDebug("Issue {Id} already claimed elsewhere", issue.Id);
                return new Result(false, "already-claimed");
            }
            await PublishTransition(claimed, IssueStatus.Pending, IssueStatus.InProgress, null, cancellationToken);

            // Re-fetch after the claim/transition so the workflow's
            // input has InProgress + assignee=forge (ClaimExecutor
            // short-circuits on that combination).
            var preClaimed = (await bundle.IssueStore.GetAsync(claimed.Id, cancellationToken))!;

            // P4 Stage B: the dispatcher abstracts over InProcess
            // (current behavior) vs Durable (DTS-backed). Both
            // block until the workflow run reaches a terminal
            // state so the caller can keep its synchronous
            // dispatch-then-check shape.
            try
            {
                await _dispatcher.DispatchAsync(preClaimed, bundle, cancellationToken);
            }
            catch (Exception ex)
            {
                if (IsLlmRateLimited(ex))
                {
                    _llmRateLimitedUntil = DateTime.UtcNow + LlmRateLimitCooldown;
                    _logger.LogWarning("Issue {Id}: LLM rate limit (429); re-queued, dispatch cooling down for {Cooldown}",
                        preClaimed.Id, LlmRateLimitCooldown);
                    await SafeTransitionAsync(preClaimed.Id, IssueStatus.Pending, "llm-429", bundle, cancellationToken);
                    return new Result(false, "llm-rate-limited");
                }
                _logger.LogError(ex, "Workflow dispatch for {Id} threw", preClaimed.Id);
                await HandleFailureAsync(preClaimed, ex, bundle, cancellationToken);
                return new Result(false, ex.Message);
            }

            // Inspect the issue post-workflow to construct the
            // Result message (preserves the old sequential contract).
            var after = await bundle.IssueStore.GetAsync(preClaimed.Id, cancellationToken);
            var lastError = after?.GetMetadata("lastError");
            if (!string.IsNullOrEmpty(lastError))
            {
                // A recorded 429 with a completed PR is noise: the
                // agent's LLM call rate-limited mid-conversation but
                // the workflow still committed + pushed + opened the
                // PR. Never requeue those — that would redispatch
                // finished work (observed live: two tasks requeued
                // with PRs #6/#7 already open).
                var reachedPr = after?.DispatchCheckpoint >= DispatchCheckpoint.PrOpened;
                if (IsLlmRateLimited(new InvalidOperationException(lastError)) && !reachedPr)
                {
                    _llmRateLimitedUntil = DateTime.UtcNow + LlmRateLimitCooldown;
                    _logger.LogWarning("Issue {Id}: LLM rate limit (429); re-queued, dispatch cooling down for {Cooldown}",
                        preClaimed.Id, LlmRateLimitCooldown);
                    await SafeTransitionAsync(preClaimed.Id, IssueStatus.Pending, "llm-429", bundle, cancellationToken);
                    return new Result(false, "llm-rate-limited");
                }
                _logger.LogWarning("Workflow dispatch for {Id} reported failure: {Err}",
                    preClaimed.Id, lastError);
                var ex = new InvalidOperationException(lastError);
                await HandleFailureAsync(preClaimed, ex, bundle, cancellationToken);
                return new Result(false, lastError);
            }
            var prNumber = after?.GetMetadata("prNumber");
            _logger.LogInformation("Workflow dispatch for {Id} completed in {Ms}ms (status={Status} prNumber={Pr})",
                preClaimed.Id, (DateTime.UtcNow - startedAt).TotalMilliseconds, after?.Status, prNumber);
            if (!string.IsNullOrEmpty(prNumber))
            {
                return new Result(true, $"PR #{prNumber} opened");
            }
            if (after?.Status == IssueStatus.Completed)
            {
                return new Result(true, "completed with no diff");
            }
            // Mid-pipeline halt detection: the workflow run returned
            // but the issue never reached PrOpened and has no
            // lastError. MAF InProcessExecution swallows executor
            // faults (the run just halts), so without this check the
            // issue would sit InProgress forever with no retry and
            // no error anywhere. Treat as a dispatch failure and let
            // the retry/hard-fail policy handle it.
            if (after?.Status == IssueStatus.InProgress
                && after.DispatchCheckpoint is not null
                && after.DispatchCheckpoint < DispatchCheckpoint.PrOpened)
            {
                var msg = $"workflow halted mid-pipeline at checkpoint {after.DispatchCheckpoint} without surfacing an error";
                _logger.LogWarning("Workflow dispatch for {Id}: {Msg}", preClaimed.Id, msg);
                await HandleFailureAsync(preClaimed, new InvalidOperationException(msg), bundle, cancellationToken);
                return new Result(false, msg);
            }
            return new Result(true, "workflow completed");
        }
        catch (OperationCanceledException)
        {
            await SafeTransitionAsync(issue.Id, IssueStatus.Failed, "cancelled", bundle, cancellationToken);
            return new Result(false, "cancelled");
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    internal void BindOptions(AgentOptions options)
    {
        _spawnerOptions = options.Spawner;
        _concurrencyLimiter.Dispose();
        _concurrencyLimiter = new SemaphoreSlim(Math.Max(1, options.Spawner.MaxConcurrentSessions));
    }

    private async Task HandleFailureAsync(IssueRecord issue, Exception ex, ProjectDispatchBundle bundle, CancellationToken cancellationToken)
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
            }), bundle, cancellationToken);
            await SafeTransitionAsync(issue.Id, IssueStatus.Pending, ex.Message, bundle, cancellationToken);
            _logger.LogWarning("Issue {Id} will be retried (attempt {N})", issue.Id, retryCount + 1);
        }
        else
        {
            await SafeTransitionAsync(issue.Id, IssueStatus.Failed, ex.Message, bundle, cancellationToken);
            if (!string.IsNullOrEmpty(worktreePath))
            {
                try { await bundle.Worktrees.RemoveAsync(issue.Id, cancellationToken); }
                catch (Exception wx) { _logger.LogWarning(wx, "Worktree removal failed"); }
            }
        }
    }

    private async Task EnqueueWatchIssueAsync(string devIssueId, int prNumber, string branch, string worktreePath, ProjectDispatchBundle bundle, CancellationToken ct)
    {
        var watch = await bundle.IssueStore.CreateAsync(new NewIssue(
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

    private async Task RecordModelResponseMetadataAsync(string id, string? response, string? error, ProjectDispatchBundle bundle, CancellationToken ct = default)
    {
        try
        {
            var current = await bundle.IssueStore.GetAsync(id, ct);
            if (current is null) return;
            await bundle.IssueStore.TransitionAsync(id, current.Status,
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

    private async Task UpdateMetadataAsync(string id, Func<Dictionary<string, object>, Dictionary<string, object>> mutate, ProjectDispatchBundle bundle, CancellationToken ct)
    {
        var current = await bundle.IssueStore.GetAsync(id, ct);
        if (current is null) return;
        using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrEmpty(current.MetadataJson) ? "{}" : current.MetadataJson);
        var dict = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
            dict[prop.Name] = System.Text.Json.JsonSerializer.Deserialize<object>(prop.Value.GetRawText())!;
        var merged = mutate(dict);
        await bundle.IssueStore.TransitionAsync(id, current.Status, current.GetMetadata("lastError"),
            metadata: merged, ct: ct);
    }

    private async Task SafeTransitionAsync(string id, IssueStatus to, string? error, ProjectDispatchBundle bundle, CancellationToken ct)
    {
        try { await bundle.IssueStore.TransitionAsync(id, to, error, ct: ct); }
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

    internal static string BuildPrompt(IssueRecord issue, RoleAgent role, string worktreePath, string branch, string? defaultBranch)
        => $"""
            You are acting as the **{role.AgentName}** agent for the PortHorizon project.
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

    internal static string BuildPrBody(IssueRecord issue, RoleAgent role, string sha, string response)
        => $"""
            ## Summary
            Automated change for issue `{issue.Id}` (type: {issue.Type}, role: {role.AgentName}).

            ## Description
            {issue.Description}

            ## Verification
            - HEAD SHA: `{sha}`
            - ACP session result (truncated): `{Truncate(response, 400)}`

            Closes `{issue.Id}`.
            """;

    internal static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

}






