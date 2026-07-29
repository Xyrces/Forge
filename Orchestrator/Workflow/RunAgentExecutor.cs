using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
// RoleAgentRegistry is in Forge.Agents, not .Core.

namespace Forge.Orchestrator.Workflow;

/// <summary>
/// Third executor in the engineering dispatch workflow. Builds
/// the prompt, drains the operator message bus, calls
/// <see cref="IAgentRunner.RunAsync"/> against the worktree, and
/// captures the model response in issue metadata. Returns an
/// <see cref="AgentCompleted"/> with the agent's text + session id.
/// </summary>
public sealed class RunAgentExecutor : FunctionExecutor<WorktreeReady, AgentCompleted>
{
    private readonly IIssueStore _issues;
    private readonly IAgentRunner _runner;
    private readonly RoleAgentRegistry _roleRegistry;
    private readonly Func<string, string?> _drainMessageBus;
    private readonly IDashboardEventBus _events;
    private readonly DesignArtifactStore _designArtifacts;
    private readonly ArtOutputStore _artOutputs;
    private readonly ILogger<RunAgentExecutor> _logger;
    private readonly string? _projectId;
    private readonly ISprintStore? _sprints;

    public RunAgentExecutor(
        IIssueStore issues,
        IAgentRunner runner,
        RoleAgentRegistry roleRegistry,
        Func<string, string?> drainMessageBus,
        IDashboardEventBus events,
        DesignArtifactStore designArtifacts,
        ArtOutputStore artOutputs,
        ILogger<RunAgentExecutor> logger,
        string? projectId = null,
        ISprintStore? sprints = null,
        double timeoutMinutes = 15.0,
        Core.TaskStateMachine? lifecycle = null)
        : base(
            "run-agent",
            (input, ctx, ct) => HandleAsync(input, issues, runner, roleRegistry, drainMessageBus, events, designArtifacts, artOutputs, logger, projectId, sprints, ct, timeoutMinutes, lifecycle),
            null,
            new[] { typeof(WorktreeReady) },
            new[] { typeof(AgentCompleted) })
    {
        _issues = issues;
        _runner = runner;
        _roleRegistry = roleRegistry;
        _drainMessageBus = drainMessageBus;
        _events = events;
        _designArtifacts = designArtifacts;
        _artOutputs = artOutputs;
        _logger = logger;
        _projectId = projectId;
        _sprints = sprints;
    }

    public static async ValueTask<AgentCompleted> HandleAsync(
        WorktreeReady input,
        IIssueStore issues,
        IAgentRunner runner,
        RoleAgentRegistry roleRegistry,
        Func<string, string?> drainMessageBus,
        IDashboardEventBus events,
        DesignArtifactStore designArtifacts,
        ArtOutputStore artOutputs,
        ILogger logger,
        string? projectId,
        ISprintStore? sprints,
        CancellationToken ct,
        double timeoutMinutes = 15.0,
        Core.TaskStateMachine? lifecycle = null)
    {
        if (input.Result == WorktreeResult.AlreadyClaimed)
        {
            return new AgentCompleted(input, AgentResult.Skipped, string.Empty, null);
        }
        var issue = input.Claim.Issue;
        var role = RoleAgentRegistry.FromTaskType(issue.Type);
        var branch = input.Claim.Branch ?? $"agent/{issue.Id}";
        var roleAgent = roleRegistry.ForType(role);
        var worktreePath = input.WorktreePath!;

        events.Publish(new DashboardEvent(
            DateTime.UtcNow, DashboardEventKind.AgentSessionStarted,
            issue.Id, $"role={roleAgent.AgentName} worktree={worktreePath}"));

        var designRefs = await LoadDesignArtifactRefsAsync(issues, designArtifacts, issue, ct);
        var artRefs = await LoadArtOutputRefsAsync(issues, artOutputs, issue, ct);
        var prompt = BuildPrompt(issue, role, worktreePath, branch, input.BaseBranch, designRefs, artRefs);
        // Rework loop: a task requeued by the PRWatcher carries the
        // failure context (CI failure or reviewer notes) — surface it
        // prominently so the agent fixes THAT, not re-explores.
        var reworkContext = issue.GetMetadata("reworkContext");
        if (!string.IsNullOrWhiteSpace(reworkContext))
        {
            var round = issue.GetMetadata("reworkAttempts") ?? "1";
            prompt += $"\n\n## Rework required (round {round})\n" +
                $"Your previous attempt produced a PR that did NOT pass review/CI. " +
                $"Fix the following on the SAME branch (do not restructure unrelated work):\n\n{reworkContext}";
        }
        // Plan-gate fast path: mechanical rework rounds (conflict
        // sync, infra retrigger) have their exact steps prescribed by
        // the watcher — evaluating a plan against an LLM critic
        // would waste tokens. The gate still records the plan.
        var planFastPath = (issue.GetMetadata("reworkReason") ?? "") is { } reason
            && (reason.Contains("conflicts with the base branch", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("base-branch CI recovered", StringComparison.OrdinalIgnoreCase));
        var queued = drainMessageBus(roleAgent.AgentName);
        var fullPrompt = string.IsNullOrEmpty(queued)
            ? prompt
            : prompt + "\n\n## Operator messages\n" + queued + "\n\nAddress these messages before working on the task.";

        AgentRunResult result;
        CancellationToken effectiveCt = ct;
        CancellationTokenSource? timeoutCts = null;
        if (timeoutMinutes > 0)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
            effectiveCt = timeoutCts.Token;
        }
        try
        {
            var context = new Dictionary<string, object>
            {
                ["worktreePath"] = worktreePath,
                ["branch"] = branch,
                ["issueId"] = issue.Id,
                // Drives the runner's secrets-by-reference env
                // injection (FORGE_SECRET_*). Null-safe: the
                // runner skips env when absent.
                ["projectId"] = projectId ?? string.Empty,
                ["planFastPath"] = planFastPath ? "true" : "false",
            };
            // Sprint flow: when the issue belongs to the ACTIVE
            // sprint, the runner gets the sprint id (drives the
            // `sprint/{id}/` memory recall), the sprint goal, and a
            // roster of sibling tasks — the shared context that makes
            // a sprint more than a label.
            if (sprints is not null)
            {
                try
                {
                    var active = await sprints.GetActiveAsync(ct);
                    if (active is not null)
                    {
                        var memberIds = await sprints.GetIssueIdsAsync(active.Id, ct);
                        if (memberIds.Contains(issue.Id))
                        {
                            context["sprintId"] = active.Id;
                            context["sprintName"] = active.Name;
                            context["sprintGoal"] = active.Goal;
                            var roster = new List<string>();
                            foreach (var memberId in memberIds)
                            {
                                if (memberId == issue.Id) continue;
                                var sibling = await issues.GetAsync(memberId, ct);
                                if (sibling is not null && !AgentTaskTypes.IsContainer(sibling.Type))
                                {
                                    roster.Add($"- {sibling.Id} [{sibling.Status}] {sibling.Title}");
                                }
                            }
                            context["sprintRoster"] = string.Join("\n", roster);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Sprint context is advisory; a lookup failure must
                    // never break a dispatch.
                    logger.LogWarning(ex, "RunAgent({Id}): sprint context lookup failed; continuing without it", issue.Id);
                }
            }
            // RunStarted: the model run is about to begin. Advances
            // the recorded lifecycle state (Dispatching ->
            // AgentRunning) and refreshes stateEnteredAt so the
            // stall guard's clock measures from run-start, not from
            // the rework fire (observed live 2026-07-27: retried
            // stalls looked frozen at Dispatching for the whole run).
            if (lifecycle is not null)
            {
                try
                {
                    var fresh = await issues.GetAsync(issue.Id, ct) ?? issue;
                    await lifecycle.ReportAsync(issues, fresh, Core.TaskEvent.RunStarted, watch: null, hasActiveDevRun: true, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "lifecycle RunStarted report failed for {Id}; continuing", issue.Id);
                }
            }
            result = await runner.RunAsync(
                role, fullPrompt,
                sessionId: null,
                context: context,
                effectiveCt);
            // DIAGNOSTIC: surface what the agent returned so we can
            // debug the "empty modelResponse" bug. Will be removed
            // once we find the root cause.
            logger.LogInformation(
                "RunAgent({Id}) role={Role} text_len={Len} session_id={Sid}",
                issue.Id, role, result.Text?.Length ?? 0, result.SessionId ?? "<null>");
            if (string.IsNullOrEmpty(result.Text))
            {
                logger.LogWarning(
                    "RunAgent({Id}) returned EMPTY text. Prompt length={Pl}. " +
                    "Inspect MafAgentRunner.RunAsync to see if response.Messages contains tool calls only.",
                    issue.Id, fullPrompt.Length);
            }
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
        {
            var timeoutMsg = $"Agent run timed out after {timeoutMinutes} minute(s)";
            logger.LogWarning("RunAgent({Id}): {Msg}", issue.Id, timeoutMsg);
            var cur = await issues.GetAsync(issue.Id, ct);
            if (cur is not null)
            {
                var meta = ParseMetadata(cur.MetadataJson);
                meta["lastError"] = timeoutMsg;
                meta["modelResponse"] = $"<timed out after {timeoutMinutes}m>";
                meta["agentTimeout"] = "true";
                await issues.TransitionAsync(issue.Id, IssueStatus.Pending,
                    error: timeoutMsg, metadata: meta, ct: ct);
            }
            throw new TimeoutException(timeoutMsg);
        }
        catch (Exception ex)
        {
            // Record the failure in the issue's lastError metadata
            // and transition the issue to Failed (preserves the
            // old sequential code's behavior — the operator can
            // see what went wrong from the dashboard's Tasks tab).
            // The orchestrator's HandleFailureAsync is the source
            // of truth for retry policy; here we just persist the
            // error so it survives even if the orchestrator doesn't
            // catch the workflow's exception.
            var cur = await issues.GetAsync(issue.Id, ct);
            if (cur is not null)
            {
                var meta = ParseMetadata(cur.MetadataJson);
                meta["lastError"] = $"{ex.GetType().Name}: {ex.Message}";
                meta["lastErrorAt"] = DateTime.UtcNow.ToString("O");
                meta["modelResponse"] = $"<threw: {ex.GetType().Name}: {ex.Message}>";
                await issues.TransitionAsync(issue.Id, cur.Status,
                    error: meta["lastError"]?.ToString(), metadata: meta, ct: ct);
            }
            throw;
        }
        finally
        {
            timeoutCts?.Dispose();
        }
        // Record the model response in issue metadata so the
        // dashboard can show what the agent said, even when
        // downstream steps fail.
        await RecordModelResponseAsync(issues, issue.Id, result.Text ?? string.Empty, ct);
        // P4 Stage A: advance to agent_completed. From here the
        // recoverer knows the LLM has finished; if we crash before
        // CommitPushPr runs, the recoverer resumes from commit.
        await issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.AgentCompleted, ct);
        await UpdateMetadataAsync(issues, issue.Id, m =>
        {
            m["agentSessionId"] = result.SessionId ?? string.Empty;
            return m;
        }, ct);
        events.Publish(new DashboardEvent(
            DateTime.UtcNow, DashboardEventKind.AgentSessionCompleted,
            issue.Id, $"elapsed={result.Elapsed.TotalMilliseconds:F0}ms",
            new Dictionary<string, object?>
            {
                ["sessionId"] = result.SessionId ?? "",
                ["elapsedMs"] = result.Elapsed.TotalMilliseconds,
            }));
        return new AgentCompleted(input, AgentResult.Ok, result.Text ?? string.Empty, result.SessionId);
    }

    private static async Task RecordModelResponseAsync(
        IIssueStore issues, string id, string response, CancellationToken ct)
    {
        var cur = await issues.GetAsync(id, ct);
        if (cur is null) return;
        var meta = ParseMetadata(cur.MetadataJson);
        meta["modelResponse"] = response ?? string.Empty;
        await issues.TransitionAsync(id, cur.Status, error: null, metadata: meta, ct: ct);
    }

    private static async Task UpdateMetadataAsync(
        IIssueStore issues, string id,
        Func<Dictionary<string, object>, Dictionary<string, object>> mutate,
        CancellationToken ct)
    {
        var cur = await issues.GetAsync(id, ct);
        if (cur is null) return;
        var current = ParseMetadata(cur.MetadataJson);
        var next = mutate(current);
        await issues.TransitionAsync(id, cur.Status, error: null, metadata: next, ct: ct);
    }

    private static Dictionary<string, object> ParseMetadata(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return new();
            var d = new Dictionary<string, object>();
            foreach (var p in doc.RootElement.EnumerateObject())
                d[p.Name] = System.Text.Json.JsonSerializer.Deserialize<object>(p.Value.GetRawText())!;
            return d;
        }
        catch { return new(); }
    }

    /// <summary>
    /// The full prompt mirrors the original OrchestratorAgent.BuildPrompt.
    /// Kept here so the executor's logic is self-contained for tests.
    /// </summary>
    public static string BuildPrompt(
        IssueRecord issue, AgentType role, string worktreePath,
        string branch, string baseBranch,
        IReadOnlyList<DesignArtifactRef>? designArtifacts = null,
        IReadOnlyList<ArtOutputRef>? artOutputs = null)
    {
        var designSection = "";
        if (designArtifacts is { Count: > 0 })
        {
            var refs = string.Join(", ", designArtifacts.Select(a => a.Id));
            designSection = $"""

                Design artifacts (P2.a): the parent spec has {designArtifacts.Count} design artifact(s):
                {refs}
                Each one is at GET /api/specs/{issue.ParentIssueId ?? issue.Id}/design-artifacts. Use `curl` to fetch them BEFORE writing code. The artifact body IS the visual source of truth.
                """;
        }

        var artSection = "";
        if (artOutputs is { Count: > 0 })
        {
            var refs = string.Join(", ", artOutputs.Select(a => $"{a.Id} [{a.Kind}:{a.BodyKind}]"));
            var artFileApi = $"/api/specs/{issue.ParentIssueId ?? issue.Id}/art-output";
            artSection = $$"""

                Art outputs (P2.b): the parent spec has {{artOutputs.Count}} art output(s):
                {{refs}}
                Each one is at GET {{artFileApi}}. The body is a relative path under .portHorizon/art-output/. The file is served at GET /api/art-output/{id}/file (use `curl` to download). Use these for the actual asset payloads in your implementation.
                """;
        }

        return $"""
            You are the {role} agent.

            Issue: {issue.Id}
            Title: {issue.Title}
            Type: {issue.Type}
            Priority: {issue.Priority}

            {issue.Description ?? ""}

            Working directory: {worktreePath}
            Branch: {branch} (base: {baseBranch}){designSection}{artSection}

            ## Completion contract
            Do the work: explore, then EDIT, then build/test. If — and
            only if — you conclude the task genuinely requires no code
            changes (already implemented, verification-only), end your
            final message with the exact marker NO_CHANGES_NEEDED plus
            a one-sentence justification. A run that produces no diff
            without that marker is treated as a failed attempt and
            re-queued (circuit breaker applies).
            """;
    }

    /// <summary>
    /// Looks up the spec associated with the issue (via parent_issue_id)
    /// and returns its design_artifact rows as refs. P2.a: the
    /// engineering agent prompt references these so the model can
    /// fetch the visual artifacts before writing code.
    /// </summary>
    private static async Task<IReadOnlyList<DesignArtifactRef>?> LoadDesignArtifactRefsAsync(
        IIssueStore issues, DesignArtifactStore designArtifacts,
        IssueRecord issue, CancellationToken ct)
    {
        // Issue's parent_issue_id is the epic; the spec that was created
        // from that epic has parent_issue_id = epic. For stories/tasks,
        // the spec parent_issue_id may be the story's parent epic.
        // Try the issue itself first, then walk up the parent chain.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = issue.Id;
        while (!string.IsNullOrEmpty(current) && seen.Add(current))
        {
            var all = await issues.ListAsync(new IssueFilter(), ct);
            var spec = all.FirstOrDefault(s => s.ParentIssueId == current);
            if (spec is null) break;
            var arts = await designArtifacts.ListBySpecAsync(spec.Id, status: null, ct);
            if (arts.Count > 0)
            {
                return arts.Select(a => new DesignArtifactRef(a.Id, a.Kind.ToString().ToLowerInvariant(), a.Title)).ToList();
            }
            current = spec.ParentIssueId;
        }
        return null;
    }

    /// <summary>
    /// P2.b: walks the same parent chain as
    /// <see cref="LoadDesignArtifactRefsAsync"/> and returns
    /// the spec's <c>art_output</c> rows. Mirrors the design
    /// ref loader; the prompt builder renders an
    /// "Art outputs" section the engineering agent can
    /// <c>curl</c> before writing code.
    /// </summary>
    private static async Task<IReadOnlyList<ArtOutputRef>?> LoadArtOutputRefsAsync(
        IIssueStore issues, ArtOutputStore artOutputs,
        IssueRecord issue, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = issue.Id;
        while (!string.IsNullOrEmpty(current) && seen.Add(current))
        {
            var all = await issues.ListAsync(new IssueFilter(), ct);
            var spec = all.FirstOrDefault(s => s.ParentIssueId == current);
            if (spec is null) break;
            var arts = await artOutputs.ListBySpecAsync(spec.Id, status: null, ct);
            if (arts.Count > 0)
            {
                return arts.Select(a => new ArtOutputRef(
                    a.Id, a.Kind.ToString().ToLowerInvariant(), a.BodyKind, a.Title)).ToList();
            }
            current = spec.ParentIssueId;
        }
        return null;
    }
}

/// <summary>Lightweight reference to a design artifact (id + kind + title).</summary>
public sealed record DesignArtifactRef(string Id, string Kind, string Title);

/// <summary>P2.b: lightweight reference to an art output (id + kind + body_kind + title).</summary>
public sealed record ArtOutputRef(string Id, string Kind, string BodyKind, string Title);

public enum AgentResult
{
    Ok,
    Skipped,
}

/// <summary>
/// Output of <see cref="RunAgentExecutor"/>. Carries the
/// worktree-ready input + the agent's text + session id.
/// </summary>
public sealed record AgentCompleted(
    WorktreeReady Worktree,
    AgentResult Result,
    string Text,
    string? SessionId);