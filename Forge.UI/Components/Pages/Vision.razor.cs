using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forge.Dashboard.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Forge.Dashboard.Components.Pages;

public partial class Vision
{
    private readonly Dictionary<string, VisionSessionDto> _sessions = new(StringComparer.Ordinal);
    private VisionSessionDto _session = new();
    private VisionMapDto? _map;
    private string? _projectId;
    private string? _error;
    private string? _storageError;
    private string? _status;
    private string? _draftTaskId;
    private string _sentText = "";
    private string _sourceText = "";
    private string _sourceRevision = "";
    private string? _repositorySource;
    private bool _repositoryChecked;
    private bool _sourceOpen;
    private bool _sourceEditing;
    private bool _loading;
    private bool _sending;
    private bool _saving;
    private bool _stale;
    private bool _interactive;
    private bool _disposed;
    private long _generation;
    private CancellationTokenSource _request = new();
    private IJSObjectReference? _module;

    private bool Busy => _sending || _saving;
    private string ProjectName => ShellStore.Value.Projects.FirstOrDefault(p => p.Id == _projectId)?.Name ?? _projectId ?? "Project";
    private string Api => $"/api/projects/{Uri.EscapeDataString(_projectId!)}/vision";
    private IReadOnlyList<VisionNodeDto> VisibleNodes => _map?.Nodes ?? [];
    private VisionNodeDto? SelectedNode
    {
        get
        {
            var id = _session.SelectedId;
            if (id?.StartsWith("draft:", StringComparison.Ordinal) == true &&
                int.TryParse(id[6..], out var index) && index >= 0 && index < _session.Pending.Count)
            {
                var edit = _session.Pending[index];
                return new(id, edit.NodeId, edit.Title ?? "New idea", edit.After);
            }
            return VisibleNodes.FirstOrDefault(n => n.Id == id);
        }
    }

    protected override void OnInitialized() => ShellStore.StateChanged += ShellChanged;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try
        {
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./vision-map.js");
            _interactive = true;
            await SwitchProjectAsync();
        }
        catch (JSDisconnectedException) { }
        catch (Exception ex)
        {
            _error = "The vision workspace could not start. " + ex.Message;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void ShellChanged(object? sender, EventArgs e)
    {
        if (_interactive && !_disposed && ShellStore.Value.CurrentProjectId != _projectId)
            _ = InvokeAsync(SwitchProjectAsync);
    }

    private async Task SwitchProjectAsync()
    {
        if (_disposed) return;
        var next = ShellStore.Value.CurrentProjectId;
        if (next == _projectId && _map is not null) return;
        // Persist the old project before any awaited work can change the active session.
        var oldProject = _projectId;
        var oldSession = _session;
        RememberSourceDraft();
        ResetRequest();
        var generation = _generation;
        _projectId = next;
        _map = null;
        _session = next is not null && _sessions.TryGetValue(next, out var cached) ? cached : new();
        _error = null;
        _status = null;
        _draftTaskId = null;
        _stale = false;
        _sourceOpen = false;
        _sourceEditing = false;
        _repositorySource = null;
        _repositoryChecked = false;
        _sending = _saving = false;
        _loading = next is not null;
        StateHasChanged();
        if (oldProject is not null) await PersistSessionAsync(oldProject, oldSession);
        if (generation != _generation || next is null) return;
        if (!_sessions.ContainsKey(next) && _module is not null)
        {
            try
            {
                var restored = await _module.InvokeAsync<VisionSessionDto?>("readSession", next, VisionUiLimits.MaxSessionBytes);
                if (generation != _generation) return;
                if (restored is { Messages: not null, Pending: not null, Input: not null })
                    _session = restored;
            }
            catch (JSException ex) { _storageError = "Saved conversation could not be restored. " + ex.Message; }
        }
        if (generation != _generation) return;
        _sessions[next] = _session;
        if (_session.SourceDraft is not null)
        {
            _sourceText = _session.SourceDraft;
            _sourceRevision = _session.SourceRevision ?? "";
            _sourceEditing = _sourceOpen = true;
        }
        await LoadAsync(generation);
    }

    private void ResetRequest()
    {
        _generation++;
        _request.Cancel();
        _request.Dispose();
        _request = new();
    }

    private async Task LoadAsync(long generation)
    {
        try
        {
            using var response = await Http.GetAsync(Api + "/map", _request.Token);
            await RequireSuccessAsync(response);
            var map = await response.Content.ReadFromJsonAsync<VisionMapDto>(_request.Token)
                ?? throw new InvalidOperationException("The project returned an empty vision response.");
            if (generation != _generation) return;
            _map = map;
            _stale = _session.Pending.Count > 0 && _session.BaseRevision != map.Revision;
            if (!_stale) _session.BaseRevision = map.Revision;
            if (_session.SelectedId is not null && !map.Nodes.Any(n => n.Id == _session.SelectedId))
                _session.SelectedId = null;
        }
        catch (OperationCanceledException) when (generation != _generation || _disposed) { }
        catch (Exception ex) { if (generation == _generation) _error = "Vision could not be loaded. " + ex.Message; }
        finally
        {
            if (generation == _generation && !_disposed)
            {
                _loading = false;
                StateHasChanged();
            }
        }
    }

    private async Task RefreshAsync()
    {
        if (Busy || _projectId is null || _sourceEditing && !_session.MustRefresh) return;
        ResetRequest();
        var generation = _generation;
        _error = null;
        _loading = true;
        try
        {
            using var response = await Http.PostAsync("/api/vision/refresh?projectId=" + Uri.EscapeDataString(_projectId), null, _request.Token);
            await RequireSuccessAsync(response);
            if (generation != _generation) return;
            _session.GuidancePending = false;
        }
        catch (OperationCanceledException) when (_disposed || generation != _generation) { return; }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            _error = "Vision could not be refreshed. " + ex.Message;
            _loading = false;
            return;
        }
        if (generation != _generation) return;
        await LoadAsync(generation);
        if (generation == _generation && _error is null)
            _session.MustRefresh = false;
        await PersistAsync();
    }

    private async Task SendAsync()
    {
        if (Busy || _stale || _session.MustRefresh || _session.GuidancePending || _map is null || string.IsNullOrWhiteSpace(_session.Input)) return;
        var generation = _generation;
        var session = _session;
        var text = session.Input.Trim();
        var messages = session.Messages.Append(new VisionMessageDto("user", text)).ToArray();
        _sending = true;
        _sentText = text;
        _error = _status = null;
        try
        {
            var client = HttpFactory.CreateClient("ForgeApiLongPoll");
            using var response = await client.PostAsJsonAsync(Api + "/chat",
                new { baseRevision = session.BaseRevision, messages, pending = session.Pending }, _request.Token);
            await RequireSuccessAsync(response);
            var reply = await response.Content.ReadFromJsonAsync<VisionReplyDto>(_request.Token)
                ?? throw new InvalidOperationException("The editor returned an empty reply.");
            if (generation != _generation) return;
            if (reply.Edits is null || string.IsNullOrWhiteSpace(reply.Message))
                throw new InvalidOperationException("The editor returned an incomplete reply. Your draft is ready to retry.");
            session.Messages.Add(new("user", text));
            session.Messages.Add(new("assistant", reply.Message));
            session.Pending = reply.Edits.ToList();
            session.SelectedId = null;
            session.Input = "";
            _status = session.Pending.Count > 0 ? "Draft ready for review." : null;
            await PersistAsync();
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation) _status = "Request cancelled. Your message is ready to retry.";
        }
        catch (VisionRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            if (generation == _generation)
            {
                _session.MustRefresh = true;
                _error = "The source changed during this conversation. " + ex.Message;
                await PersistAsync();
            }
        }
        catch (Exception ex) { if (generation == _generation) _error = "Message could not be completed. " + ex.Message; }
        finally
        {
            if (generation == _generation) _sending = false;
        }
    }

    private void CancelRequest()
    {
        _request.Cancel();
        // Retain this generation so the active call can display its cancellation.
        _request.Dispose();
        _request = new();
    }

    private async Task ApplyAsync()
    {
        if (Busy || _stale || _session.MustRefresh || _session.GuidancePending || _map is null || _session.Pending.Count == 0) return;
        var generation = _generation;
        _saving = true;
        _error = null;
        try
        {
            using var response = await Http.PostAsJsonAsync(Api + "/apply",
                new { baseRevision = _session.BaseRevision, edits = _session.Pending }, _request.Token);
            await RequireSuccessAsync(response);
            var map = await response.Content.ReadFromJsonAsync<VisionMapDto>(_request.Token)
                ?? throw new InvalidOperationException("The save returned an empty vision response.");
            if (generation != _generation) return;
            _map = map;
            _session.BaseRevision = map.Revision;
            _session.Pending.Clear();
            _session.SelectedId = null;
            _session.Messages.Add(new("assistant", "The reviewed draft has been applied to the project vision."));
            _status = "Project vision updated.";
            await PersistAsync();
        }
        catch (VisionRequestException ex) when (ex.Code == "vision_memory_sync_pending")
        {
            if (generation == _generation) await HandleCommittedFileAsync(generation);
        }
        catch (VisionRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            if (generation == _generation)
            {
                _session.MustRefresh = true;
                _error = "The draft needs a refreshed source before it can be applied. " + ex.Message;
                await PersistAsync();
            }
        }
        catch (OperationCanceledException) when (generation != _generation || _disposed) { }
        catch (Exception ex) { if (generation == _generation) _error = "The draft could not be applied. " + ex.Message; }
        finally { if (generation == _generation) _saving = false; }
    }

    private async Task DiscardAsync()
    {
        if (Busy) return;
        _session.Pending.Clear();
        _session.Messages.Add(new("assistant", "The operator discarded the proposed edits."));
        _session.BaseRevision = _map?.Revision ?? "";
        _stale = false;
        _status = "Draft discarded.";
        await PersistAsync();
    }

    private async Task SelectNodeAsync(string id)
    {
        _session.SelectedId = id;
        await PersistAsync();
    }

    private async Task ClearSelectionAsync()
    {
        _session.SelectedId = null;
        await PersistAsync();
    }

    private string EditTitle(VisionEditDto edit) =>
        edit.Operation == "add" ? edit.Title ?? "New idea" : VisibleNodes.FirstOrDefault(n => n.Id == edit.NodeId)?.Title ?? "Changed passage";

    private string CurrentWording(VisionEditDto edit) => edit.Operation switch
    {
        "add" => VisibleNodes.FirstOrDefault(n => n.Id == edit.NodeId)?.Title ?? ProjectName,
        "append" => VisibleNodes.FirstOrDefault(n => n.Id == edit.NodeId)?.Title ?? ProjectName,
        _ => edit.Before
    };

    private void ShowSource() => _sourceOpen = true;
    private async Task RequestDraftAsync()
    {
        if (Busy || _projectId is null || _map is null || !string.IsNullOrWhiteSpace(_map.Source) || _draftTaskId is not null) return;
        var generation = _generation;
        _saving = true;
        _error = null;
        try
        {
            using var response = await Http.PostAsync("/api/vision/draft-request?projectId=" + Uri.EscapeDataString(_projectId), null, _request.Token);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                if (generation == _generation) _error = "A vision draft task is already open in this project’s backlog.";
                return;
            }
            await RequireSuccessAsync(response);
            var result = await response.Content.ReadFromJsonAsync<VisionDraftResponse>(_request.Token)
                ?? throw new InvalidOperationException("The draft request returned no task.");
            if (generation == _generation) _draftTaskId = result.TaskId;
        }
        catch (OperationCanceledException) when (generation != _generation || _disposed) { }
        catch (Exception ex) { if (generation == _generation) _error = "The draft task could not be created. " + ex.Message; }
        finally { if (generation == _generation) _saving = false; }
    }
    private sealed record VisionDraftResponse(string TaskId);
    private void CloseSource() => _sourceOpen = false;
    private async Task EditSource()
    {
        if ((_map?.Source.Length ?? 0) > VisionUiLimits.MaxSourceCharacters) return;
        _sourceText = _map?.Source ?? "";
        _sourceRevision = _map?.Revision ?? "";
        _sourceEditing = true;
        _repositorySource = null;
        _repositoryChecked = false;
        var generation = _generation;
        try
        {
            using var response = await Http.GetAsync(Api + "/repository", _request.Token);
            await RequireSuccessAsync(response);
            var repository = await response.Content.ReadFromJsonAsync<RepositoryVisionDto>(_request.Token);
            if (generation != _generation) return;
            _repositorySource = repository is { Exists: true } ? repository.Content : null;
            _repositoryChecked = true;
        }
        catch (OperationCanceledException) when (generation != _generation || _disposed) { }
        catch (Exception ex) { if (generation == _generation) _error = "Repository source could not be checked. " + ex.Message; }
    }
    private sealed record RepositoryVisionDto(bool Exists, string Content);
    private async Task LoadRepositorySourceAsync()
    {
        if (Busy || _repositorySource is null || _sourceText != _map?.Source || _repositorySource.Length > VisionUiLimits.MaxSourceCharacters) return;
        _sourceText = _repositorySource;
        _status = "Repository Markdown loaded for preview. Save source to adopt it as the project vision.";
        await PersistAsync();
    }
    private async Task UseLatestSourceBaselineAsync()
    {
        if (Busy || _map is null || _session.MustRefresh) return;
        _sourceRevision = _map.Revision;
        await PersistAsync();
    }
    private async Task CancelSourceAsync()
    {
        _sourceEditing = false;
        await PersistAsync();
    }

    private async Task CopySourceAsync()
    {
        try
        {
            var copied = await JS.InvokeAsync<bool>("forge.clipboard.copy", _map?.Source ?? "");
            _status = copied ? "Source copied." : "The browser could not copy the source.";
        }
        catch (JSException ex) { _error = "The source could not be copied. " + ex.Message; }
    }

    private async Task SaveSourceAsync()
    {
        if (Busy || _projectId is null || _session.MustRefresh || _session.GuidancePending || _session.Pending.Count > 0 || _sourceRevision != _map?.Revision) return;
        if (_sourceText.Length > VisionUiLimits.MaxSourceCharacters)
        {
            _error = "The source exceeds the browser editor’s 256 Ki character limit.";
            return;
        }
        var generation = _generation;
        _saving = true;
        _error = null;
        try
        {
            using var response = await Http.PutAsJsonAsync("/api/vision?projectId=" + Uri.EscapeDataString(_projectId),
                new { content = _sourceText, baseRevision = _sourceRevision }, _request.Token);
            await RequireSuccessAsync(response);
            if (generation != _generation) return;
            _sourceEditing = false;
            _session.SourceDraft = null;
            _session.SourceRevision = null;
            await LoadAsync(generation);
            if (generation != _generation) return;
            _status = "Source document saved.";
            await PersistAsync();
        }
        catch (VisionRequestException ex) when (ex.Code == "vision_memory_sync_pending")
        {
            if (generation == _generation) await HandleCommittedFileAsync(generation);
        }
        catch (VisionRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            if (generation == _generation)
            {
                _session.MustRefresh = true;
                _error = "Your source draft is retained. Refresh to inspect the latest version before saving. " + ex.Message;
                await PersistAsync();
            }
        }
        catch (OperationCanceledException) when (generation != _generation || _disposed) { }
        catch (Exception ex) { if (generation == _generation) _error = "Source could not be saved. " + ex.Message; }
        finally { if (generation == _generation) _saving = false; }
    }

    private static async Task RequireSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync();
        string? code = null;
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("code", out var codeField) && codeField.ValueKind == JsonValueKind.String)
                code = codeField.GetString();
            foreach (var key in new[] { "detail", "message", "error" })
            {
                if (json.RootElement.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String)
                {
                    text = field.GetString()!;
                    break;
                }
            }
        }
        catch (JsonException) { text = response.ReasonPhrase ?? "Request failed."; }
        if (text.Length > 600) text = text[..600];
        if (response.StatusCode == HttpStatusCode.Conflict)
            text += " Refresh the source to review the latest version.";
        throw new VisionRequestException(text, response.StatusCode, code);
    }

    private async Task HandleCommittedFileAsync(long generation)
    {
        _session.Pending.Clear();
        _session.SelectedId = null;
        _session.GuidancePending = true;
        _session.MustRefresh = true;
        _sourceEditing = false;
        _session.SourceDraft = null;
        _session.SourceRevision = null;
        _session.Messages.Add(new("assistant", "The project vision has been saved. Agent guidance still needs synchronization. Refresh retries that synchronization."));
        _status = "Vision saved. Refresh to synchronize agent guidance.";
        // Persist the committed state before loading, so a failed read cannot revive the applied draft.
        await PersistAsync();
        if (generation != _generation) return;
        await LoadAsync(generation);
        if (generation != _generation) return;
        await PersistAsync();
    }

    private void RememberSourceDraft()
    {
        _session.SourceDraft = _sourceEditing ? _sourceText : null;
        _session.SourceRevision = _sourceEditing ? _sourceRevision : null;
    }

    private async Task PersistAsync()
    {
        if (_projectId is null) return;
        RememberSourceDraft();
        await PersistSessionAsync(_projectId, _session);
    }

    private async Task PersistSessionAsync(string project, VisionSessionDto session)
    {
        if (_module is null || _disposed) return;
        try
        {
            var stored = await _module.InvokeAsync<bool>("writeSession", project, session, VisionUiLimits.MaxSessionBytes);
            _storageError = stored ? null : "This browser could not save the conversation within its 512 KiB limit. Keep this page open to retain the draft.";
        }
        catch (JSDisconnectedException) { }
        catch (JSException ex) { _storageError = "Conversation could not be saved in this browser. " + ex.Message; }
    }

    public async ValueTask DisposeAsync()
    {
        ShellStore.StateChanged -= ShellChanged;
        await PersistAsync();
        _disposed = true;
        ResetRequest();
        _request.Dispose();
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}
