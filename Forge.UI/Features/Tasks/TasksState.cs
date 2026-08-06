using System.Net.Http.Json;
using Fluxor;

namespace Forge.Dashboard.Features.Tasks;

[FeatureState]
public sealed record TasksState
{
    public bool Loading { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<TaskRow> Rows { get; init; } = Array.Empty<TaskRow>();
    public string? ActionMessage { get; init; }
    public string? ActionError { get; init; }
}

public sealed record TaskEventRow(string Kind, DateTime At, string? Detail);

public sealed record TaskRow(
    string Id,
    string Type,
    string Title,
    string Status,
    int Priority,
    string? Assignee,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ClosedAt,
    string? DispatchCheckpoint,
    int RecoveryAttempts,
    string? PrUrl,
    string? Branch,
    string? WorktreePath,
    IReadOnlyList<TaskEventRow> Events);

public static class TasksActions
{
    public sealed record LoadTasksAction(string? ProjectId = null);
    public sealed record TasksLoadedAction(IReadOnlyList<TaskRow> Rows);
    public sealed record TasksLoadFailedAction(string Error);

    public sealed record RetryMessageAction(string TaskId, string Text);
    public sealed record RetryMessageSucceededAction(string TaskId);
    public sealed record RetryMessageFailedAction(string Error);

    public sealed record RecoverTaskAction(string TaskId);
    public sealed record RecoverTaskSucceededAction(string TaskId, int? ReportId);
    public sealed record RecoverTaskFailedAction(string Error);
}

public sealed class TasksClient
{
    private readonly HttpClient _http;

    public TasksClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<TaskRow>> ListInProgressAsync(string? projectId, CancellationToken ct)
    {
        var url = projectId is null
            ? "/api/tasks/in-progress"
            : $"/api/tasks/in-progress?projectId={Uri.EscapeDataString(projectId)}";
        return await _http.GetFromJsonAsync<List<TaskRow>>(url, ct) ?? new List<TaskRow>();
    }

    public async Task RetryMessageAsync(string taskId, string text, string? projectId, CancellationToken ct)
    {
        var url = $"/api/tasks/{Uri.EscapeDataString(taskId)}/retry-message";
        if (projectId is not null) url += $"?projectId={Uri.EscapeDataString(projectId)}";
        var resp = await _http.PostAsJsonAsync(url, new { text }, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<RecoverResultDto?> RecoverAsync(string taskId, string? projectId, CancellationToken ct)
    {
        var url = $"/api/tasks/{Uri.EscapeDataString(taskId)}/recover";
        if (projectId is not null) url += $"?projectId={Uri.EscapeDataString(projectId)}";
        var resp = await _http.PostAsync(url, null, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<RecoverResultDto>(cancellationToken: ct);
    }
}

public sealed record RecoverResultDto(string TaskId, int? ReportId);
