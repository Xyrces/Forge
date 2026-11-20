using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Dashboard.Services;
using Microsoft.AspNetCore.Components;

namespace Forge.Dashboard.Components.Shared;

public partial class IntakeBrief
{
    [Inject] private HttpClient Http { get; set; } = default!;
    [Parameter] public string? ProjectId { get; set; }
    [Parameter] public string? SessionId { get; set; }
    [Parameter] public string? SelectedProposalId { get; set; }
    [Parameter] public EventCallback<string> ProposalSelected { get; set; }
    [Parameter] public EventCallback Changed { get; set; }
    [Parameter] public EventCallback<IntakeProposalDetail?> DetailLoaded { get; set; }
    [Parameter] public int RefreshVersion { get; set; }
    private readonly string _selectId = "intake-brief-" + Guid.NewGuid().ToString("N");
    private readonly List<IntakeProposal> _proposals = [];
    private IntakeProposalDetail? _detail;
    private IntakeProposalRevision? _previous;
    private IntakeBriefSpec? _spec;
    private IReadOnlyList<IntakeBriefChange> _changes = [];
    private string? _loadedProject, _loadedSession, _loadedSelection, _selectedId, _nextCursor;
    private string? _error, _comparisonError, _handoffError;
    private int _loadedVersion = -1;
    private int _compareRevision;
    private bool _loading, _busy, _stale, _showChanges, _comparing, _polling, _disposed;
    private long _generation, _comparisonGeneration;
    private CancellationTokenSource _requests = new();
    private System.Threading.Timer? _refreshTimer;
    private string? _openedProposalId;
    public (string Id, int Revision)? DisplayedRevision =>
        !_loading && !_stale && _detail?.Proposal is { } proposal
        && proposal.Id == _selectedId && proposal.ProjectId == ProjectId && proposal.SessionId == SessionId
            ? (proposal.Id, proposal.Revision) : null;

    private string Query => "projectId=" + Uri.EscapeDataString(ProjectId ?? "");
    private static MarkupString Html(string? content) => new(MarkdownRenderer.ToHtml(content));
    private static bool IsLongChange(IntakeBriefChange change) =>
        change.InlineEdits.Count > 0 && (change.Before?.Length ?? 0) + (change.After?.Length ?? 0) > 700;

    private string ProposalUrl(string id) => "/api/intake/proposals/" + Uri.EscapeDataString(id);

    protected override void OnInitialized()
    {
        _refreshTimer = new(_ => InvokeAsync(async () =>
        {
            if (!_disposed && !_busy && !_loading && !_comparing && !_polling && _detail?.Proposal.ApprovedAt is not null)
            {
                _polling = true;
                try
                {
                    await RefreshHandoffAsync();
                    StateHasChanged();
                }
                finally { _polling = false; }
            }
        }), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedProject == ProjectId && _loadedSession == SessionId
            && _loadedVersion == RefreshVersion && _loadedSelection == SelectedProposalId) return;
        var switched = _loadedProject != ProjectId || _loadedSession != SessionId;
        _loadedProject = ProjectId;
        _loadedSession = SessionId;
        _loadedVersion = RefreshVersion;
        _loadedSelection = SelectedProposalId;
        if (switched)
        {
            _selectedId = null;
            _detail = null;
            _showChanges = false;
            _stale = false;
            _proposals.Clear();
            _openedProposalId = null;
            await DetailLoaded.InvokeAsync(null);
        }
        if (SelectedProposalId is not null && _selectedId != SelectedProposalId)
        {
            _selectedId = SelectedProposalId;
            _detail = null;
            _previous = null;
            await DetailLoaded.InvokeAsync(null);
        }
        await RefreshAsync();
    }

    private long BeginRequest()
    {
        _requests.Cancel();
        _requests.Dispose();
        _requests = new();
        ++_comparisonGeneration;
        _comparing = false;
        return ++_generation;
    }

    public async Task RefreshAsync()
    {
        var generation = BeginRequest();
        var token = _requests.Token;
        _error = null;
        if (ProjectId is null || SessionId is null) return;
        _loading = true;
        try
        {
            var page = await Http.GetFromJsonAsync<IntakeProposalPage>("/api/intake/proposals?" + Query
                + "&sessionId=" + Uri.EscapeDataString(SessionId), token)
                ?? throw new InvalidOperationException("No proposal list was returned.");
            if (generation != _generation) return;
            _proposals.Clear();
            _proposals.AddRange(page.Items);
            _nextCursor = page.NextCursor;
            _selectedId ??= _proposals.FirstOrDefault()?.Id;
            if (_selectedId is { } id) await ReadDetailAsync(id, generation, token);
            else _detail = null;
        }
        catch (OperationCanceledException) when (_disposed || generation != _generation) { }
        catch (Exception ex) { if (generation == _generation) _error = "The brief could not be refreshed. " + ex.Message; }
        finally { if (generation == _generation) _loading = false; }
    }

    private async Task ReadDetailAsync(string id, long generation, CancellationToken token)
    {
        var detail = await Http.GetFromJsonAsync<IntakeProposalDetail>(ProposalUrl(id) + "?" + Query, token)
            ?? throw new InvalidOperationException("No proposal was returned.");
        if (generation != _generation) return;
        if (detail.Proposal.ProjectId != ProjectId || detail.Proposal.SessionId != SessionId)
            throw new InvalidOperationException("This brief belongs to a different conversation.");
        var changed = _detail is null || _detail.Proposal.Id != detail.Proposal.Id || _detail.Proposal.Hash != detail.Proposal.Hash;
        _detail = detail;
        _stale = false;
        if (_openedProposalId != detail.Proposal.Id)
        {
            _openedProposalId = detail.Proposal.Id;
            _showChanges = detail.Proposal.ApprovedAt is not null && detail.Proposal.Revision > 1;
        }
        if (_proposals.All(proposal => proposal.Id != id)) _proposals.Add(detail.Proposal);
        if (changed)
        {
            _previous = null;
            _compareRevision = Math.Max(1, detail.Proposal.Revision - 1);
            _comparisonError = null;
            _changes = [];
        }
        await ReadSpecAsync(generation, token);
        if (generation != _generation) return;
        await DetailLoaded.InvokeAsync(detail);
        if (generation != _generation) return;
        if (_showChanges) await ShowChangesAsync();
    }

    private async Task ChooseAsync(ChangeEventArgs args)
    {
        if (_busy || args.Value is not string id || _proposals.All(proposal => proposal.Id != id)) return;
        var generation = BeginRequest();
        _selectedId = id;
        _detail = null;
        _previous = null;
        _loading = true;
        _error = null;
        await DetailLoaded.InvokeAsync(null);
        try
        {
            await ReadDetailAsync(id, generation, _requests.Token);
            if (generation == _generation) await ProposalSelected.InvokeAsync(id);
        }
        catch (OperationCanceledException) when (_disposed || generation != _generation) { }
        catch (Exception ex) { if (generation == _generation) _error = "The selected brief could not be loaded. " + ex.Message; }
        finally { if (generation == _generation) _loading = false; }
    }

    private async Task LoadMoreAsync()
    {
        if (_busy || _loading || _nextCursor is null || SessionId is null) return;
        var generation = _generation;
        _loading = true;
        try
        {
            var page = await Http.GetFromJsonAsync<IntakeProposalPage>("/api/intake/proposals?" + Query
                + "&sessionId=" + Uri.EscapeDataString(SessionId) + "&cursor=" + Uri.EscapeDataString(_nextCursor), _requests.Token)
                ?? throw new InvalidOperationException("No proposal list was returned.");
            if (generation != _generation) return;
            foreach (var proposal in page.Items)
                if (_proposals.All(existing => existing.Id != proposal.Id)) _proposals.Add(proposal);
            _nextCursor = page.NextCursor;
        }
        catch (OperationCanceledException) when (_disposed || generation != _generation) { }
        catch (Exception ex) { if (generation == _generation) _error = "More briefs could not be loaded. " + ex.Message; }
        finally { if (generation == _generation) _loading = false; }
    }

    private async Task ShowChangesAsync()
    {
        _showChanges = true;
        if (_detail is null || _detail.Proposal.Revision == 1 || _previous is not null) return;
        var proposal = _detail.Proposal;
        var requestedRevision = _compareRevision;
        var generation = _generation;
        var comparison = ++_comparisonGeneration;
        _comparing = true;
        _comparisonError = null;
        try
        {
            var previous = await Http.GetFromJsonAsync<IntakeProposalRevision>(
                ProposalUrl(proposal.Id) + "/revisions/" + requestedRevision + "?" + Query, _requests.Token)
                ?? throw new InvalidOperationException("The previous revision was not returned.");
            if (generation != _generation || comparison != _comparisonGeneration) return;
            if (previous.Revision != requestedRevision) throw new InvalidOperationException("The revision history did not match.");
            _previous = previous;
            _changes = IntakeBriefComparison.Compare(previous.Body, proposal.Body ?? "");
        }
        catch (OperationCanceledException) when (_disposed || generation != _generation) { }
        catch (Exception ex)
        {
            if (generation == _generation && comparison == _comparisonGeneration)
                _comparisonError = "Changes could not be loaded. " + ex.Message;
        }
        finally { if (comparison == _comparisonGeneration) _comparing = false; }
    }

    private async Task CompareRevisionAsync(ChangeEventArgs args)
    {
        if (_detail is null || !int.TryParse(args.Value?.ToString(), out var revision)
            || revision < 1 || revision >= _detail.Proposal.Revision) return;
        _compareRevision = revision;
        _previous = null;
        _changes = [];
        await ShowChangesAsync();
    }

    private Task ApproveAsync() => ActAsync("approve");
    private Task RetryAsync() => ActAsync("retry-refinement");
    private async Task ActAsync(string action)
    {
        if (_busy || DisplayedRevision is null || _detail is null) return;
        var proposal = _detail.Proposal;
        var generation = _generation;
        _busy = true;
        _error = null;
        try
        {
            using var response = await Http.PostAsJsonAsync(ProposalUrl(proposal.Id) + "/" + action + "?" + Query,
                new { proposal.Revision, proposal.Hash }, _requests.Token);
            if (generation != _generation) return;
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                _stale = true;
                _error = await ErrorAsync(response);
                return;
            }
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await ErrorAsync(response));
            await RefreshAsync();
            if (proposal.ProjectId == ProjectId && proposal.SessionId == SessionId) await Changed.InvokeAsync();
        }
        catch (OperationCanceledException) when (_disposed || generation != _generation) { }
        catch (Exception ex) { if (generation == _generation) _error = "The action could not be confirmed. Refresh the brief before retrying. " + ex.Message; }
        finally { _busy = false; }
    }

    private async Task RefreshHandoffAsync()
    {
        if (_detail is null) return;
        var generation = _generation;
        var id = _detail.Proposal.Id;
        try
        {
            var detail = await Http.GetFromJsonAsync<IntakeProposalDetail>(ProposalUrl(id) + "?" + Query, _requests.Token);
            if (generation != _generation || detail is null) return;
            if (detail.Proposal.Hash != _detail?.Proposal.Hash)
            {
                _stale = true;
                return;
            }
            _detail = detail;
            await ReadSpecAsync(generation, _requests.Token);
            if (generation == _generation) await DetailLoaded.InvokeAsync(detail);
        }
        catch (OperationCanceledException) when (_disposed || generation != _generation) { }
        catch (Exception ex)
        {
            if (generation == _generation) _handoffError = "Current planning progress could not be refreshed. " + ex.Message;
        }
    }

    private async Task ReadSpecAsync(long generation, CancellationToken token)
    {
        _spec = null;
        _handoffError = null;
        if (_detail?.Ingestion is not { } receipt || receipt.RefinementState != "Completed") return;
        try
        {
            var spec = await Http.GetFromJsonAsync<IntakeBriefSpec>("/api/specs/" + Uri.EscapeDataString(receipt.SpecId)
                + "?project=" + Uri.EscapeDataString(ProjectId!), token)
                ?? throw new InvalidOperationException("No specification was returned.");
            if (generation == _generation) _spec = spec;
        }
        catch (OperationCanceledException) when (_disposed || generation != _generation) { }
        catch (Exception ex) { if (generation == _generation) _handoffError = "Current specification status is unavailable. " + ex.Message; }
    }

    private string HandoffTitle => _detail?.Ingestion?.RefinementState switch
    {
        null => "Approval recorded",
        "Pending" => "Waiting for specification",
        "Running" => "Preparing specification",
        "Failed" => "Specification needs attention",
        "Completed" => _spec?.Status switch
        {
            "ReadyForDesign" => "Ready for design",
            "Designed" => "Design complete",
            "Approved" or "AssetReady" => "Ready for technical planning",
            "Grooming" => "Technical planning in progress",
            "Groomed" => "Technical plan prepared",
            "NeedsRevision" => "Specification needs attention",
            "Shipped" => "Delivered",
            "Archived" => "Archived",
            "Superseded" => "Superseded",
            _ => "Specification prepared"
        },
        _ => "Planning status unavailable"
    };

    private string HandoffDescription => _detail?.Ingestion?.RefinementState switch
    {
        null => "Forge is recording the initiative and specification. Your approval is saved.",
        "Pending" => "The approved scope is queued for Product to turn into a specification.",
        "Running" => "Product is refining the approved scope into acceptance criteria and planning detail.",
        "Failed" => "Refinement stopped. Review the recorded error before retrying.",
        "Completed" => _spec?.Status switch
        {
            "ReadyForDesign" => "The specification is ready for the design stage.",
            "Designed" => "Design is recorded. Asset preparation is the next stage.",
            "Approved" or "AssetReady" => "The specification is eligible for technical grooming.",
            "Grooming" => "Forge is decomposing the specification into stories, tasks and dependencies.",
            "Groomed" => "Stories and tasks are recorded. Follow progress for sprint scheduling and delivery.",
            "NeedsRevision" => "A planning check requested a specification revision. Open the specification for details.",
            "Shipped" => "The specification is recorded as shipped.",
            "Archived" => "This specification has been archived.",
            "Superseded" => "This specification has been superseded.",
            _ => "Product refinement is complete. Open the specification to inspect its current state."
        },
        _ => "Refresh the brief to read its recorded planning state."
    };

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        try
        {
            using var json = JsonDocument.Parse(text);
            foreach (var field in new[] { "error", "detail", "message" })
                if (json.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "The request failed.";
        }
        catch (JsonException) { }
        return response.ReasonPhrase ?? "The request failed.";
    }

    public void Dispose()
    {
        _disposed = true;
        _refreshTimer?.Dispose();
        ++_generation;
        _requests.Cancel();
        _requests.Dispose();
    }
}
