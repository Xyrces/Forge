using System.Net.Http.Json;
using System.Text.Json;
using Forge.Dashboard.Components.Shared;
using Forge.Dashboard.Features.AppShell;
using Forge.Dashboard.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Forge.Dashboard.Components.Pages;

public partial class Intake
{
    [SupplyParameterFromQuery(Name = "project")] public string? QueryProject { get; set; }
    [SupplyParameterFromQuery(Name = "session")] public string? QuerySession { get; set; }
    [SupplyParameterFromQuery(Name = "proposal")] public string? QueryProposal { get; set; }
    private string? _loadedProject, _activeSessionId, _selectedProposalId, _actionError;
    private string _sessionTitle = "New idea", _draft = "", _liveText = "";
    private int _proposalVersion;
    private IntakeBrief? _workingBrief;
    private IntakeProposalDetail? _shownBrief;
    private long? _expandedExchange;
    private bool _followLatest;
    private string? _mobilePane;
    private bool IsApproved => _shownBrief?.Proposal.ApprovedAt is not null;
    private string MobilePane => _mobilePane ?? (IsApproved ? "brief" : "conversation");
    private string WorkspaceTitle => _shownBrief?.Proposal.Title ?? _sessionTitle;

    private void OnBriefLoaded(IntakeProposalDetail? detail)
    {
        if (detail is not null && (!Current(detail.Proposal.ProjectId, detail.Proposal.SessionId)
            || (_selectedProposalId is not null && detail.Proposal.Id != _selectedProposalId))) return;
        _shownBrief = detail;
    }

    private sealed record Exchange(int Start, int End, string Label, string Excerpt);

    private IEnumerable<Exchange> Exchanges(IReadOnlyList<MessageDto> messages)
    {
        if (!IsApproved)
        {
            yield return new(0, messages.Count, "", "");
            yield break;
        }
        for (var start = 0; start < messages.Count;)
        {
            var end = start + 1;
            while (end < messages.Count && messages[end].Role != "User") end++;
            var opener = messages[start];
            var isUser = opener.Role == "User";
            var label = isUser ? (start == 0 ? "Opening request" : "Follow-up") : "Conversation opened";
            var excerpt = isUser ? Preview(opener.Content) : opener.ProposedEpicTitle ?? "Forge opened this discussion.";
            yield return new(start, end, label, excerpt);
            start = end;
        }
    }

    private static string Preview(string text)
    {
        var plain = Markdig.Markdown.ToPlainText(text).Trim();
        var end = plain.IndexOfAny(['.', '!', '?', '\n']);
        // Some older operator messages lead with a routing instruction and a machine ID.
        // Prefer the following complete sentence for the preview. The transcript stays verbatim.
        if (end > 0 && plain[..end].Contains("proposal-", StringComparison.OrdinalIgnoreCase) && end < plain.Length - 1)
        {
            plain = plain[(end + 1)..].TrimStart();
            end = plain.IndexOfAny(['.', '!', '?', '\n']);
        }
        if (end > 0) plain = plain[..(end + 1)].TrimEnd();
        return Truncate(plain, 180);
    }

    private bool ExchangeOpen(Exchange exchange) => !IsApproved
        || (_followLatest && exchange.End == _messages?.Count)
        || (!_followLatest && _expandedExchange == _messages?[exchange.Start].Id);

    private void ToggleExchange(Exchange exchange)
    {
        _expandedExchange = ExchangeOpen(exchange) ? null : _messages?[exchange.Start].Id;
        _followLatest = false;
    }
    private IntakeInbox? _inbox;
    private Task RefreshInbox() => _inbox?.RefreshAsync() ?? Task.CompletedTask;
    private bool _creating, _opening, _accepting, _liveActive, _mermaidDirty, _applyingLocation, _disposed;
    private List<MessageDto>? _messages;
    private readonly List<string> _liveTools = new();
    private readonly List<CanvasDiagram> _diagrams = new();
    private readonly Dictionary<string, string> _drafts = new();
    private readonly HashSet<string> _pending = new();
    private long _readGeneration;
    private long _transcriptRequest;
    private CancellationTokenSource _readCts = new();
    private DotNetObjectReference<Intake>? _selfRef;
    private bool _streamSubscribed;
    private string ContextKey => $"{_loadedProject}/{_activeSessionId}";
    private bool _sending => _pending.Contains(ContextKey);
    public sealed record SessionDto(string Id, string ProjectId, string Title, DateTime CreatedAt, DateTime UpdatedAt, List<MessageDto>? Messages);
    public sealed record MessageDto(long Id, string Role, string Content, DateTime Timestamp, string? ProposedEpicId, string? ProposedEpicTitle, List<QuestionDto>? Questions = null, string? ProposedEpicDescription = null);
    public sealed record QuestionDto(string? Header, string Question, bool Multiple, List<string>? Options)
    {
        public List<string> Options { get; } = Options ?? new();
    }
    private sealed record CanvasDiagram(string ElementId, string Source, string Caption);
    private IEnumerable<MessageDto> LegacyDrafts => (_messages ?? new()).Where(m =>
        m.ProposedEpicDescription is not null || m.ProposedEpicId is not null ||
        (m.ProposedEpicTitle is not null && !HasDurableProposalRecord(m.ProposedEpicTitle)));
    private bool HasDurableProposalRecord(string title) => _messages?.Any(m =>
        m.Role.Equals("System", StringComparison.OrdinalIgnoreCase) && m.ProposedEpicTitle == title &&
        m.Content.StartsWith("Proposal ", StringComparison.Ordinal) &&
        m.Content.Contains("Draft only; explicit operator approval is required.", StringComparison.Ordinal)) == true;
    private string? LegacyEpic(MessageDto message) => message.ProposedEpicId ??
        _messages?.FirstOrDefault(m => m.ProposedEpicId is not null && m.Role.Equals("System", StringComparison.OrdinalIgnoreCase)
            && m.Content.Contains($"(proposal message #{message.Id})", StringComparison.Ordinal))?.ProposedEpicId;

    private readonly Dictionary<string, List<string>> _answers = new();
    private readonly Dictionary<string, string> _otherText = new();
    private readonly HashSet<string> _otherOpen = new();
    private readonly HashSet<long> _dismissedQuestionCards = new();

    /// <summary>Split an em-dash encoded option ("Label — description")
    /// into its parts; a leading "(Recommended)" becomes a badge.</summary>
    private static (string Label, string? Description, bool Recommended) SplitOption(string option)
    {
        var s = option.Trim();
        var recommended = s.StartsWith("(Recommended)", StringComparison.OrdinalIgnoreCase);
        if (recommended) s = s["(Recommended)".Length..].TrimStart(' ', '—', '-');
        var dash = s.IndexOf(" — ", StringComparison.Ordinal);
        return dash < 0
            ? (s, null, recommended)
            : (s[..dash].Trim(), s[(dash + 3)..].Trim(), recommended);
    }

    private string? GetOther(string key) => _otherText.TryGetValue(key, out var v) ? v : null;

    private void SetOther(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) _otherText.Remove(key);
        else _otherText[key] = value;
    }

    private bool IsQuestionCardActive(int messageIndex)
    {
        if (_messages is null) return false;
        var m = _messages[messageIndex];
        if (m.Id < 0 || _dismissedQuestionCards.Contains(m.Id)) return false;
        // Only the LATEST unanswered card is interactive: a user
        // message after it (typed or composed) means it was answered.
        for (var i = messageIndex + 1; i < _messages.Count; i++)
        {
            if (_messages[i].Role.Equals("User", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private void ToggleAnswer(string key, string label, bool multiple)
    {
        if (!_answers.TryGetValue(key, out var cur))
        {
            _answers[key] = new List<string> { label };
            return;
        }
        if (cur.Contains(label))
        {
            cur.Remove(label);
            if (cur.Count == 0) _answers.Remove(key);
            return;
        }
        if (multiple) cur.Add(label);
        else
        {
            cur.Clear();
            cur.Add(label);
        }
    }

    private int AnsweredCount(MessageDto m, IReadOnlyList<QuestionDto> qs)
    {
        var n = 0;
        for (var qi = 0; qi < qs.Count; qi++)
        {
            var key = $"{m.Id}:{qi}";
            if ((_answers.TryGetValue(key, out var sel) && sel.Count > 0)
                || (_otherText.TryGetValue(key, out var t) && !string.IsNullOrWhiteSpace(t)))
            {
                n++;
            }
        }
        return n;
    }

    private void DismissQuestionCard(MessageDto m) => _dismissedQuestionCards.Add(m.Id);


    private async Task SendAnswers(MessageDto m, IReadOnlyList<QuestionDto> qs)
    {
        var lines = new List<string> { "Answers to your questions:" };
        var unanswered = new List<int>();
        for (var qi = 0; qi < qs.Count; qi++)
        {
            var key = $"{m.Id}:{qi}";
            var label = qs[qi].Header
                ?? Truncate(qs[qi].Question.Replace("**", "").Trim(), 60);
            var parts = new List<string>();
            if (_answers.TryGetValue(key, out var sel)) parts.AddRange(sel);
            if (_otherText.TryGetValue(key, out var t) && !string.IsNullOrWhiteSpace(t))
                parts.Add($"Other: {t.Trim()}");
            if (parts.Count == 0)
            {
                unanswered.Add(qi + 1);
                continue;
            }
            lines.Add($"{qi + 1}. {label} → {string.Join(", ", parts)}");
        }
        if (lines.Count == 1) return;
        // Unanswered questions stay open — say so explicitly so the
        // agent doesn't assume silence = answered.
        if (unanswered.Count > 0)
            lines.Add($"(questions {string.Join(", ", unanswered)} still open)");
        await PostUserMessage(string.Join("\n", lines));
    }


    protected override void OnInitialized() => ShellStore.StateChanged += OnShellChanged;
    protected override Task OnParametersSetAsync() => ApplyLocation();
    private async Task ApplyLocation()
    {
        if (_disposed) return;
        var project = ShellStore.Value.CurrentProjectId;
        if (QueryProject is not null && QueryProject != project)
        {
            if (ShellStore.Value.Projects.Count == 0) return;
            if (!ShellStore.Value.Projects.Any(p => p.Id == QueryProject))
            {
                ResetContext(null, null);
                _actionError = "This project is not registered.";
                return;
            }
            _applyingLocation = true;
            try
            {
                Dispatch.Dispatch(new AppShellActions.SelectProjectAction(QueryProject));
            }
            finally
            {
                _applyingLocation = false;
            }
            project = QueryProject;
        }
        if (project != _loadedProject || QuerySession != _activeSessionId)
        {
            ResetContext(project, QuerySession);
            _selectedProposalId = QueryProposal;
            if (project is not null && QuerySession is not null) await ReloadActive();
        }
        else if (_selectedProposalId != QueryProposal)
        {
            _selectedProposalId = QueryProposal;
            if (QueryProposal is not null && _shownBrief?.Proposal.Id != QueryProposal)
                _shownBrief = null;
        }
    }
    private void ResetContext(string? project, string? session)
    {
        if (_activeSessionId is not null) _drafts[ContextKey] = _draft;
        ++_readGeneration;
        _readCts.Cancel();
        _readCts.Dispose();
        _readCts = new();
        _loadedProject = project;
        _activeSessionId = session;
        _actionError = null;
        _messages = null;
        _shownBrief = null;
        _expandedExchange = null;
        _followLatest = false;
        _mobilePane = null;
        _selectedProposalId = null;
        _sessionTitle = "New idea";
        _draft = _drafts.GetValueOrDefault(ContextKey, "");
        _liveActive = false;
        _liveText = "";
        _liveTools.Clear();
        _diagrams.Clear();
        _answers.Clear();
        _otherText.Clear();
        _otherOpen.Clear();
        _dismissedQuestionCards.Clear();
        _proposalVersion++;
    }
    private void OnShellChanged(object? sender, EventArgs e)
    {
        if (_disposed || _applyingLocation) return;
        _ = InvokeAsync(async () =>
        {
            if (_loadedProject is null) await ApplyLocation();
            else if (ShellStore.Value.CurrentProjectId != _loadedProject)
                Navigation.NavigateTo(Location(ShellStore.Value.CurrentProjectId));
            StateHasChanged();
        });
    }
    private static string Location(string? project, string? session = null, string? proposal = null) =>
        "/intake?project=" + Uri.EscapeDataString(project ?? "")
        + (session is null ? "" : "&session=" + Uri.EscapeDataString(session))
        + (proposal is null ? "" : "&proposal=" + Uri.EscapeDataString(proposal));
    private string Scope(string project) => "?projectId=" + Uri.EscapeDataString(project);
    private bool Current(string? project, string? session) => !_disposed && project == _loadedProject && session == _activeSessionId && project == ShellStore.Value.CurrentProjectId;
    private void BackToInbox()
    {
        _actionError = null;
        Navigation.NavigateTo(Location(_loadedProject));
    }
    private Task SelectProposal(string id)
    {
        Navigation.NavigateTo(Location(_loadedProject, _activeSessionId, id), replace: true);
        return Task.CompletedTask;
    }
    private Task BriefChanged() => ReloadActive();

    private async Task ReloadActive()
    {
        var project = _loadedProject;
        var session = _activeSessionId;
        var generation = _readGeneration;
        var request = ++_transcriptRequest;
        if (project is null || session is null) return;
        try
        {
            var value = await Http.GetFromJsonAsync<SessionDto>($"/api/intake/sessions/{Uri.EscapeDataString(session)}{Scope(project)}", _readCts.Token);
            if (generation != _readGeneration || request != _transcriptRequest || !Current(project, session)) return;
            if (value is null || value.ProjectId != project) throw new InvalidOperationException("The conversation does not belong to the selected project.");
            _messages = value.Messages ?? new();
            _sessionTitle = value.Title;
            _diagrams.Clear();
            foreach (var message in _messages.Where(m => m.Role.Equals("Assistant", StringComparison.OrdinalIgnoreCase)))
            {
                var index = 0;
                foreach (var source in ExtractMermaidBlocks(message.Content))
                    _diagrams.Add(new($"mmd-msg-{message.Id}-{index++}", source, $"Reply · {message.Timestamp.ToLocalTime():MMM d, h:mm tt}"));
            }
            _mermaidDirty = true;
            _proposalVersion++;
        }
        catch (OperationCanceledException) when (generation != _readGeneration || _disposed) { }
        catch (Exception ex) { if (request == _transcriptRequest && Current(project, session)) _actionError = "Conversation could not be loaded: " + ex.Message; }
    }
    private async Task StartNewSession()
    {
        var project = _loadedProject;
        var generation = _readGeneration;
        if (project is null || _creating) return;
        _creating = true;
        _actionError = null;
        try
        {
            using var response = await Http.PostAsJsonAsync("/api/intake/sessions", new { projectId = project, title = "New idea" });
            response.EnsureSuccessStatusCode();
            var created = await response.Content.ReadFromJsonAsync<SessionDto>() ?? throw new InvalidOperationException("Conversation was not returned.");
            if (!_disposed && generation == _readGeneration && project == _loadedProject && project == ShellStore.Value.CurrentProjectId)
                Navigation.NavigateTo(Location(project, created.Id));
        }
        catch (Exception ex)
        {
            if (generation == _readGeneration && project == _loadedProject)
                _actionError = "New idea could not be opened: " + ex.Message;
        }
        finally { _creating = false; }
    }
    private async Task OpenInboxItem(IntakeInbox.Item item)
    {
        var project = _loadedProject;
        var generation = _readGeneration;
        if (project is null || _opening) return;
        _opening = true;
        _actionError = null;
        try
        {
            var session = item.SessionId;
            if (item.Kind == "conversation") session ??= item.Id;
            if (session is null)
            {
                using var response = await Http.PostAsJsonAsync($"/api/intake/proposals/{Uri.EscapeDataString(item.Id)}/expand{Scope(project)}", new { });
                response.EnsureSuccessStatusCode();
                var value = await response.Content.ReadFromJsonAsync<JsonElement>();
                session = value.GetProperty("sessionId").GetString() ?? throw new InvalidOperationException("Conversation was not returned.");
            }
            if (!_disposed && generation == _readGeneration && project == _loadedProject && project == ShellStore.Value.CurrentProjectId)
                Navigation.NavigateTo(Location(project, session, item.Kind == "proposal" ? item.Id : null));
        }
        catch (Exception ex)
        {
            if (generation == _readGeneration && project == _loadedProject)
                _actionError = "This item could not be opened: " + ex.Message;
        }
        finally { _opening = false; }
    }
    private async Task ImportLegacyDraft(long id)
    {
        var project = _loadedProject;
        var session = _activeSessionId;
        if (project is null || session is null || _accepting) return;
        _accepting = true;
        try
        {
            using var response = await Http.PostAsJsonAsync($"/api/intake/sessions/{Uri.EscapeDataString(session)}/review-draft/{id}{Scope(project)}", new { });
            response.EnsureSuccessStatusCode();
            var value = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!Current(project, session)) return;
            var proposal = value.GetProperty("id").GetString();
            await ReloadActive();
            if (Current(project, session)) Navigation.NavigateTo(Location(project, session, proposal));
        }
        catch (Exception ex) { if (Current(project, session)) _actionError = "Earlier draft could not be opened: " + ex.Message; }
        finally { _accepting = false; }
    }
    private Task ComposerKeyDown(KeyboardEventArgs e) => e.Key == "Enter" && (e.CtrlKey || e.MetaKey) ? Send() : Task.CompletedTask;
    private async Task Send()
    {
        if (_sending || string.IsNullOrWhiteSpace(_draft)) return;
        var text = _draft.Trim();
        _draft = "";
        await PostUserMessage(text);
    }
    private async Task PostUserMessage(string text)
    {
        var project = _loadedProject;
        var session = _activeSessionId;
        var key = ContextKey;
        var reference = _workingBrief?.DisplayedRevision;
        if (project is null || session is null || !_pending.Add(key)) return;
        _actionError = null;
        _followLatest = true;
        _liveActive = true;
        _liveText = "";
        _liveTools.Clear();
        _messages ??= new();
        _messages.Add(new(-DateTime.UtcNow.Ticks, "User", text, DateTime.UtcNow, null, null));
        StateHasChanged();
        try
        {
            using var response = await HttpFactory.CreateClient("ForgeApiLongPoll").PostAsJsonAsync(
                $"/api/intake/sessions/{Uri.EscapeDataString(session)}/messages{Scope(project)}",
                new { text, proposalId = reference?.Id, revision = reference?.Revision });
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            _drafts[key] = text;
            if (Current(project, session))
            {
                _actionError = "Send did not finish: " + ex.Message;
                if (string.IsNullOrEmpty(_draft)) _draft = text;
            }
        }
        finally
        {
            _pending.Remove(key);
            if (Current(project, session))
            {
                _liveActive = false;
                _liveText = "";
                _liveTools.Clear();
                await ReloadActive();
            }
        }
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _selfRef = DotNetObjectReference.Create(this);
            try
            {
                await JS.InvokeVoidAsync("forge.intakeStream.subscribe", _selfRef);
                _streamSubscribed = true;
            }
            catch (InvalidOperationException) { /* Prerender has no browser. */ }
        }
        if (_mermaidDirty)
        {
            _mermaidDirty = false;
            try
            {
                foreach (var d in _diagrams)
                    await JS.InvokeVoidAsync("forge.intakeMermaid.renderInto", d.ElementId, d.Source);
            }
            catch (InvalidOperationException) { /* Prerender has no browser. */ }
            catch (JSException ex) { Logger.LogWarning(ex, "Could not render an intake diagram"); }
        }
    }
    [JSInvokable]
    public async Task OnIntakeEvent(string kind, string json)
    {
        if (_disposed) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("taskId", out var task) || task.GetString() != _activeSessionId) return;
            switch (kind)
            {
                case "intake-run-started":
                    _liveActive = true;
                    _liveText = "";
                    _liveTools.Clear();
                    break;
                case "intake-run-delta":
                    if (root.TryGetProperty("data", out var data) && data.TryGetProperty("delta", out var delta)) _liveText += delta.GetString();
                    _liveActive = true;
                    break;
                case "intake-run-tool":
                    if (root.TryGetProperty("data", out var toolData) && toolData.TryGetProperty("tool", out var tool) && tool.GetString() is { } name) _liveTools.Add(name);
                    break;
                case "intake-run-completed":
                    _liveActive = false;
                    await ReloadActive();
                    break;
                case "intake-epic-proposed":
                    await ReloadActive();
                    break;
                case "intake-run-failed":
                    _liveActive = false;
                    _actionError = root.TryGetProperty("detail", out var detail) ? detail.GetString() : "Forge could not finish its reply.";
                    await ReloadActive();
                    break;
            }
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Could not process intake update"); }
    }
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
    private static IEnumerable<string> ExtractMermaidBlocks(string content)
    {
        const string fence = "```mermaid";
        var pos = 0;
        while (true)
        {
            var start = content.IndexOf(fence, pos, StringComparison.OrdinalIgnoreCase);
            if (start < 0) yield break;
            var bodyStart = content.IndexOf('\n', start);
            if (bodyStart < 0) yield break;
            var end = content.IndexOf("```", bodyStart, StringComparison.Ordinal);
            if (end < 0) yield break;
            var block = content[(bodyStart + 1)..end].Trim();
            if (block.Length > 0) yield return block;
            pos = end + 3;
        }
    }


    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        ++_readGeneration;
        _readCts.Cancel();
        _readCts.Dispose();
        ShellStore.StateChanged -= OnShellChanged;
        if (_streamSubscribed)
        {
            try { await JS.InvokeVoidAsync("forge.intakeStream.unsubscribe"); }
            catch (JSDisconnectedException) { /* Browser disconnected. */ }
            catch (InvalidOperationException) { /* Prerender. */ }
        }
        _selfRef?.Dispose();
    }
}
