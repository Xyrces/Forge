using System.Net.Http.Json;
using Fluxor;

namespace Forge.Dashboard.Features.View;

[FeatureState]
public sealed record ViewState
{
    public bool Loading { get; init; }
    /// <summary>
    /// A background re-fetch is in flight. Pages keep rendering the
    /// CURRENT data while this is set — only the initial load
    /// (<see cref="Loading"/>) may replace content with a placeholder,
    /// otherwise every poll/event-driven reload would tear down the
    /// DOM and the whole panel blinks (operator 2026-07-31).
    /// </summary>
    public bool Refreshing { get; init; }
    public string? Error { get; init; }
    public DateTime? LastFetchedAt { get; init; }
    public IReadOnlyList<ViewTask> Tasks { get; init; } = Array.Empty<ViewTask>();
    public IReadOnlyList<ViewAgent> Agents { get; init; } = Array.Empty<ViewAgent>();
    public IReadOnlyList<ViewSkill> Skills { get; init; } = Array.Empty<ViewSkill>();
    public IReadOnlyList<ViewSprint> Sprints { get; init; } = Array.Empty<ViewSprint>();
    public int CompletedTasks { get; init; }
    public int FailedTasks { get; init; }
}

public sealed record ViewTask(
    string Id,
    string Type,
    string Title,
    string? Description,
    string Status,
    int Priority,
    string? Assignee,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ClosedAt,
    string? DispatchCheckpoint,
    string? PrUrl,
    string? Branch,
    string? WorktreePath,
    string? ParentIssueId = null,
    IReadOnlyDictionary<string, object?>? Parameters = null)
{
    /// <summary>prNumber from issue metadata, when the task has an open PR.</summary>
    public string? PrNumber =>
        Parameters is not null
        && Parameters.TryGetValue("prNumber", out var raw)
        && raw is not null
            ? raw.ToString()
            : null;

    /// <summary>
    /// Rework round from issue metadata ("1".."3"), when the task is
    /// in the review/rework loop. A task showing Pending with a PR
    /// number is queued for a rework round — this is the badge that
    /// makes that visible.
    /// </summary>
    public string? ReworkAttempts =>
        Parameters is not null
        && Parameters.TryGetValue("reworkAttempts", out var raw)
        && raw is not null
            ? raw.ToString()
            : null;

    /// <summary>
    /// The lifecycle state recorded by the state machine
    /// (Core/TaskStateMachine), when present — Pending, Dispatching,
    /// AgentRunning, ReworkQueued/Running, StalledRework, PROpen,
    /// ParkedInfra, MergeReady, Merged, Completed, BlockedOperator,
    /// Failed, Closed. Null for entities that predate the machine.
    /// </summary>
    public string? LifecycleState =>
        Parameters is not null
        && Parameters.TryGetValue("state", out var raw)
        && raw is not null
            ? raw.ToString()
            : null;
}

public sealed record ViewAgent(
    string Id,
    string AgentName,
    string DisplayName,
    string Scope,
    string? Description,
    bool Enabled,
    string ConfigJson,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ViewSkill(
    string Id,
    string Name,
    string? Description,
    string? Body,
    string? AgentId,
    bool Enabled,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record ViewSprint(
    string? Id,
    string? Name,
    string? Goal,
    DateTime? StartDate,
    DateTime? EndDate,
    string Status,
    int IssueCount = 0,
    int DoneCount = 0,
    IReadOnlyList<ViewSprintMember>? Members = null);

public sealed record ViewSprintMember(
    string Id,
    string Title,
    string Status,
    IReadOnlyList<string>? BlockedBy = null,
    string? Situation = null,
    string? SituationTone = null);

public static class ViewActions
{
    /// <param name="Background">True for polls and event-driven
    /// reloads: the store keeps showing current data (Refreshing
    /// flag) instead of entering the full Loading state.</param>
    public sealed record LoadViewAction(string? ProjectId = null, bool Background = false);
    public sealed record ViewLoadedAction(
        ViewState State);
    public sealed record ViewLoadFailedAction(string Error);
}

public sealed class ViewClient
{
    private readonly HttpClient _http;

    public ViewClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<ViewSnapshot> FetchAsync(string? projectId, CancellationToken ct)
    {
        var url = projectId is null
            ? "api/state"
            : $"api/state?projectId={Uri.EscapeDataString(projectId)}";
        var resp = await _http.GetFromJsonAsync<RawState>(url, ct)
                   ?? throw new InvalidOperationException("empty /api/state");
        var snapshot = new ViewSnapshot(
            Tasks: resp.tasks.Select(t => new ViewTask(
                t.id,
                t.type ?? "",
                t.title ?? "",
                t.description,
                t.status ?? "",
                t.priority,
                t.assignee,
                t.createdAt,
                t.updatedAt,
                t.closedAt,
                t.dispatchCheckpoint,
                t.prUrl,
                t.branch,
                t.worktreePath,
                t.parentIssueId,
                t.parameters)).ToArray(),
            Agents: resp.agents.Select(a => new ViewAgent(
                a.id, a.agentName ?? "", a.displayName ?? "", a.scope ?? "",
                a.description, a.enabled, a.configJson ?? "{}", a.createdAt, a.updatedAt)).ToArray(),
            Skills: resp.skills.Select(s => new ViewSkill(
                s.id, s.name ?? "", s.description, s.body, s.agentId,
                s.enabled, s.createdAt, s.updatedAt)).ToArray(),
            Sprints: resp.sprints.Select(sp => new ViewSprint(
                sp.id, sp.name, sp.goal, sp.startDate, sp.endDate,
                sp.status ?? "Unknown",
                sp.issueCount, sp.doneCount,
                (sp.members ?? Array.Empty<SprintMemberDto>())
                    .Select(m => new ViewSprintMember(m.id, m.title, m.status, m.blockedBy, m.situation, m.situationTone)).ToArray())).ToArray(),
            CompletedTasks: resp.completedTasks,
            FailedTasks: resp.failedTasks);
        return snapshot;
    }

    private sealed record RawState(
        TaskDto[] tasks,
        AgentDto[] agents,
        SkillDto[] skills,
        SprintDto[] sprints,
        int completedTasks,
        int failedTasks,
        DateTime lastHeartbeat);

    private sealed record TaskDto(
        string id,
        string type,
        string title,
        string? description,
        string status,
        int priority,
        string? assignee,
        DateTime createdAt,
        DateTime updatedAt,
        DateTime? closedAt,
        string? dispatchCheckpoint,
        string? prUrl,
        string? branch,
        string? worktreePath,
        string? parentIssueId = null,
        Dictionary<string, object?>? parameters = null);

    private sealed record AgentDto(
        string id,
        string agentName,
        string displayName,
        string scope,
        string? description,
        bool enabled,
        string configJson,
        DateTime createdAt,
        DateTime updatedAt);

    private sealed record SkillDto(
        string id,
        string name,
        string? description,
        string? body,
        string? agentId,
        bool enabled,
        DateTime createdAt,
        DateTime updatedAt);

    private sealed record SprintDto(
        string? id,
        string? name,
        string? goal,
        DateTime? startDate,
        DateTime? endDate,
        string status,
        int issueCount = 0,
        int doneCount = 0,
        SprintMemberDto[]? members = null);

    private sealed record SprintMemberDto(
        string id,
        string title,
        string status,
        string[]? blockedBy = null,
        string? situation = null,
        string? situationTone = null);
}

public sealed record ViewSnapshot(
    IReadOnlyList<ViewTask> Tasks,
    IReadOnlyList<ViewAgent> Agents,
    IReadOnlyList<ViewSkill> Skills,
    IReadOnlyList<ViewSprint> Sprints,
    int CompletedTasks,
    int FailedTasks);
