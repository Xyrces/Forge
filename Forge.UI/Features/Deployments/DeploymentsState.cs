using System.Net.Http.Json;
using Fluxor;

namespace Forge.Dashboard.Features.Deployments;

[FeatureState]
public sealed record DeploymentsState
{
    public bool Loading { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<DeploymentRow> Candidates { get; init; } = Array.Empty<DeploymentRow>();
    public DateTime? LastFetchedAt { get; init; }

    // Populated on demand when the operator opens the "request a
    // deployment" panel for a given project; kept separate from
    // Candidates so switching projects doesn't require re-fetching
    // the whole candidate list.
    public string? CommitsForProjectId { get; init; }
    public IReadOnlyList<CommitRow> Commits { get; init; } = Array.Empty<CommitRow>();
    public bool CommitsLoading { get; init; }

    public string? ActionError { get; init; }
    public InFlightWarning? PendingInFlightWarning { get; init; }
}

public sealed record DeploymentRow(
    string Id,
    string ProjectId,
    string CommitSha,
    string? CommitSummary,
    string Status,
    DateTime RequestedAt,
    string? RequestedBy,
    string? BuildLog,
    DateTime? ApprovedAt,
    string? ApprovedBy,
    DateTime? DeployedAt,
    string? DeployLog);

public sealed record CommitRow(string Sha, string ShortSha, string Subject, string Author, string DateIso);

public sealed record InFlightWarning(string DeploymentId, string Message);

public static class DeploymentsActions
{
    public sealed record LoadDeploymentsAction(string? ProjectId = null);
    public sealed record DeploymentsLoadedAction(IReadOnlyList<DeploymentRow> Candidates);
    public sealed record DeploymentsLoadFailedAction(string Error);

    public sealed record LoadCommitsAction(string ProjectId);
    public sealed record CommitsLoadedAction(string ProjectId, IReadOnlyList<CommitRow> Commits);

    public sealed record RequestDeploymentAction(string ProjectId, string CommitSha, string? RequestedBy);
    public sealed record RequestDeploymentFailedAction(string Error);

    public sealed record ApproveDeploymentAction(string Id, bool Force = false);
    public sealed record ApproveDeploymentBlockedAction(string Id, string Message);
    public sealed record ApproveDeploymentFailedAction(string Error);

    public sealed record RejectDeploymentAction(string Id);

    public sealed record ClearActionErrorAction();
}

public sealed class DeploymentsClient
{
    private readonly HttpClient _http;
    public DeploymentsClient(HttpClient http) { _http = http; }

    public async Task<IReadOnlyList<DeploymentRow>> ListAsync(string? projectId, CancellationToken ct)
    {
        var url = projectId is null ? "/api/deployments/" : $"/api/deployments/?projectId={Uri.EscapeDataString(projectId)}";
        var rows = await _http.GetFromJsonAsync<List<DeploymentRow>>(url, ct);
        return rows ?? new List<DeploymentRow>();
    }

    public async Task<IReadOnlyList<CommitRow>> ListCommitsAsync(string projectId, CancellationToken ct)
    {
        var rows = await _http.GetFromJsonAsync<List<CommitRow>>(
            $"/api/deployments/commits?projectId={Uri.EscapeDataString(projectId)}&limit=25", ct);
        return rows ?? new List<CommitRow>();
    }

    public async Task RequestAsync(string projectId, string commitSha, string? requestedBy, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("/api/deployments/", new { projectId, commitSha, requestedBy }, ct);
        resp.EnsureSuccessStatusCode();
    }

    // Returns (Success, BlockedMessage). BlockedMessage is set when the
    // API returns 409 in_flight_tasks -- the caller can offer "force"
    // as a follow-up action instead of treating it as a hard failure.
    public async Task<(bool Success, string? BlockedMessage)> ApproveAsync(string id, bool force, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync($"/api/deployments/{id}/approve", new { force }, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var body = await resp.Content.ReadFromJsonAsync<ConflictBody>(cancellationToken: ct);
            return (false, body?.Message ?? "Deploy blocked: tasks in flight.");
        }
        resp.EnsureSuccessStatusCode();
        return (true, null);
    }

    public async Task RejectAsync(string id, CancellationToken ct)
    {
        var resp = await _http.PostAsync($"/api/deployments/{id}/reject", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    private sealed record ConflictBody(string? Error, string? Message);
}
