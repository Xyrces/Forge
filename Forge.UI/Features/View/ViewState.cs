using System.Net.Http.Json;
using Fluxor;

namespace Forge.Dashboard.Features.View;

[FeatureState]
public sealed record ViewState
{
    public bool Loading { get; init; }
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
    string? WorktreePath);

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
    string Status);

public static class ViewActions
{
    public sealed record LoadViewAction();
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

    public async Task<ViewSnapshot> FetchAsync(CancellationToken ct)
    {
        var resp = await _http.GetFromJsonAsync<RawState>("api/state", ct)
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
                t.worktreePath)).ToArray(),
            Agents: resp.agents.Select(a => new ViewAgent(
                a.id, a.agentName ?? "", a.displayName ?? "", a.scope ?? "",
                a.description, a.enabled, a.configJson ?? "{}", a.createdAt, a.updatedAt)).ToArray(),
            Skills: resp.skills.Select(s => new ViewSkill(
                s.id, s.name ?? "", s.description, s.body, s.agentId,
                s.enabled, s.createdAt, s.updatedAt)).ToArray(),
            Sprints: resp.sprints.Select(sp => new ViewSprint(
                sp.id, sp.name, sp.goal, sp.startDate, sp.endDate,
                sp.status ?? "Unknown")).ToArray(),
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
        string? worktreePath);

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
        string status);
}

public sealed record ViewSnapshot(
    IReadOnlyList<ViewTask> Tasks,
    IReadOnlyList<ViewAgent> Agents,
    IReadOnlyList<ViewSkill> Skills,
    IReadOnlyList<ViewSprint> Sprints,
    int CompletedTasks,
    int FailedTasks);
