using System.Net.Http.Json;
using Forge.Dashboard.Features.View;

namespace Forge.Dashboard.Features.Sprints;

public sealed record SprintHistoryResponse(string ProjectId, DateTime GeneratedAt, ViewSprint? Active,
    long Total, IReadOnlyList<ViewSprint> History, string? NextCursor);
public sealed record SprintCardRow(string Id, string Title, string Type, string Status, string? State,
    int Priority, string? Assignee, string? PrNumber, string? ReworkAttempts, bool Boosted,
    long BlockerCount, IReadOnlyList<string> BlockedBy, string? Situation, string? SituationTone);
public sealed record SprintBoardResponse(string ProjectId, DateTime GeneratedAt, ViewSprint? Sprint,
    long Total, long Done, IReadOnlyList<SprintCardRow> Members, string? NextCursor);

public sealed class SprintReadClient(HttpClient http)
{
    public Task<SprintHistoryResponse> HistoryAsync(string projectId, string? cursor, CancellationToken ct) =>
        GetAsync<SprintHistoryResponse>($"/api/sprints/summary?projectId={Uri.EscapeDataString(projectId)}" + Cursor(cursor), ct);
    public Task<SprintBoardResponse> BoardAsync(string projectId, string? sprintId, string? cursor, CancellationToken ct) =>
        GetAsync<SprintBoardResponse>($"/api/sprints/board?projectId={Uri.EscapeDataString(projectId)}"
            + (sprintId is null ? "" : "&sprintId=" + Uri.EscapeDataString(sprintId)) + Cursor(cursor), ct);
    private static string Cursor(string? value) => value is null ? "" : "&cursor=" + Uri.EscapeDataString(value);
    private async Task<T> GetAsync<T>(string path, CancellationToken ct) =>
        await http.GetFromJsonAsync<T>(path, ct) ?? throw new InvalidDataException("Sprint endpoint returned no data.");
}

/// <summary>Project/selection generations fence late successes and failures, including cancelled HTTP calls.</summary>
public sealed class SprintSelection : IDisposable
{
    private CancellationTokenSource _cancellation = new();
    public long Generation { get; private set; }
    public string? ProjectId { get; private set; }
    public string? SprintId { get; private set; }
    public CancellationToken Token => _cancellation.Token;
    public long Select(string? projectId, string? sprintId)
    {
        _cancellation.Cancel(); _cancellation.Dispose(); _cancellation = new();
        ProjectId = projectId; SprintId = sprintId; return ++Generation;
    }
    public bool IsCurrent(long generation) => generation == Generation && !_cancellation.IsCancellationRequested;
    public void Dispose() { _cancellation.Cancel(); _cancellation.Dispose(); }
}
