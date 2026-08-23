using System.Net.Http.Json;
using Fluxor;

namespace Forge.Dashboard.Features.Triage;

[FeatureState]
public sealed record TriageState
{
    public bool Loading { get; init; }
    public string? Error { get; init; }
    public TriageSummaryRow? Summary { get; init; }
    public IReadOnlyList<TriageGroupRow> Groups { get; init; } = Array.Empty<TriageGroupRow>();
    public TriageHealthRow? Health { get; init; }
    public string? ExpandedSignature { get; init; }
    public bool DetailLoading { get; init; }
    public IReadOnlyList<TriageEntryRow> DetailRows { get; init; } = Array.Empty<TriageEntryRow>();
}

public sealed record TriageSummaryRow(
    int OpenFailures,
    int DistinctSignatures7d,
    int Escalations7d,
    int EscalationBudget,
    int AutoCleared7d,
    IReadOnlyList<int> DailyOpenFailures7d,
    IReadOnlyList<int> DailyDistinctSignatures7d,
    IReadOnlyList<int> DailyAutoCleared7d);

public sealed record TriageGroupRow(
    string Signature,
    string Classification,
    int Count,
    int DistinctTasks,
    DateTime LastSeenAt,
    string? LastTaskId,
    string? DominantOutcome,
    bool BugSuspect);

public sealed record TriageHealthRow(int PlanGateRejections7d, int NoDiffBounces7d, int VerificationTimeouts7d);

public sealed record TriageEntryRow(
    long Id, string TaskId, string? TaskTitle, DateTime FailedAt,
    string? ErrorExcerpt, string? Action, string? Actor, DateTime? ActedAt, string? Outcome);

public static class TriageActions
{
    public sealed record LoadLedgerAction(string? ProjectId = null);
    public sealed record LedgerLoadedAction(TriageSummaryRow Summary, IReadOnlyList<TriageGroupRow> Groups, TriageHealthRow Health);
    public sealed record LedgerLoadFailedAction(string Error);

    public sealed record ExpandSignatureAction(string Signature, string? ProjectId = null);
    public sealed record CollapseSignatureAction;
    public sealed record SignatureDetailLoadedAction(string Signature, IReadOnlyList<TriageEntryRow> Rows);
    public sealed record SignatureDetailFailedAction(string Error);
}

public sealed class TriageClient
{
    private readonly HttpClient _http;

    public TriageClient(HttpClient http)
    {
        _http = http;
    }

    private sealed record LedgerResponse(TriageSummaryRow Summary, List<TriageGroupRow> Groups, TriageHealthRow Health);

    public async Task<(TriageSummaryRow Summary, IReadOnlyList<TriageGroupRow> Groups, TriageHealthRow Health)> GetLedgerAsync(
        string? projectId, CancellationToken ct)
    {
        var url = projectId is null
            ? "/api/triage/ledger"
            : $"/api/triage/ledger?projectId={Uri.EscapeDataString(projectId)}";
        var resp = await _http.GetFromJsonAsync<LedgerResponse>(url, ct);
        return resp is null
            ? (new TriageSummaryRow(0, 0, 0, 5, 0, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()),
               Array.Empty<TriageGroupRow>(), new TriageHealthRow(0, 0, 0))
            : (resp.Summary, resp.Groups, resp.Health);
    }

    public async Task<IReadOnlyList<TriageEntryRow>> GetSignatureRowsAsync(
        string signature, string? projectId, CancellationToken ct)
    {
        var url = $"/api/triage/ledger/{Uri.EscapeDataString(signature)}";
        if (projectId is not null) url += $"?projectId={Uri.EscapeDataString(projectId)}";
        return await _http.GetFromJsonAsync<List<TriageEntryRow>>(url, ct) ?? new List<TriageEntryRow>();
    }
}
