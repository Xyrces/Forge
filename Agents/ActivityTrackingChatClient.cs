using Microsoft.Extensions.AI;
using Forge.Core;

namespace Forge.Agents;

/// <summary>
/// Per-round-trip activity heartbeat for the run registry. Sits
/// BETWEEN the function-invocation layer and the real provider
/// client: MAF's FunctionInvokingChatClient loops (model → tools →
/// model) INSIDE a single agent.RunAsync, so heartbeating only when
/// the outer call returns leaves a 10-minute agentic run looking
/// dead the whole time (observed live 2026-07-24). After every raw
/// model round-trip this bumps the run's message/tool/text counters
/// + last_activity_at — the dashboard can tell "the model just
/// responded" from "nothing for 4 minutes — the provider is hung".
/// Best-effort: tracking failures never break a run.
/// </summary>
internal sealed class ActivityTrackingChatClient : DelegatingChatClient
{
    private readonly string _runId;
    private readonly AgentRunStore _runs;
    private readonly Func<string?>? _phaseProvider;
    private int _roundTrips;
    private int _toolCalls;
    private int _textChars;
    // Live transcript accumulation. The response only carries the
    // assistant turn; tool RESULTS arrive in the NEXT call's incoming
    // history (appended by the function-invocation layer), so we diff
    // the incoming history against what we've already seen.
    private readonly List<ChatMessage> _liveTranscript = new();
    private int _seenHistory;

    public ActivityTrackingChatClient(IChatClient inner, string runId, AgentRunStore runs,
        Func<string?>? phaseProvider = null)
        : base(inner)
    {
        _runId = runId;
        _runs = runs;
        _phaseProvider = phaseProvider;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var incoming = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var response = await base.GetResponseAsync(incoming, options, cancellationToken);

        var roundTrips = Interlocked.Increment(ref _roundTrips);
        var tools = response.Messages.Sum(m =>
            m.Contents.OfType<FunctionCallContent>().Count());
        var chars = response.Messages
            .Where(m => m.Role == ChatRole.Assistant)
            .Sum(m => (m.Text ?? "").Length);
        var toolCalls = Interlocked.Add(ref _toolCalls, tools);
        var textChars = Interlocked.Add(ref _textChars, chars);

        // New history since the last call (tool results, layer-added
        // messages) + this response = the conversation as it stands.
        for (var i = _seenHistory; i < incoming.Count; i++)
            _liveTranscript.Add(incoming[i]);
        _liveTranscript.AddRange(response.Messages);
        _seenHistory = incoming.Count + response.Messages.Count;

        try
        {
            await _runs.UpdateProgressAsync(_runId, roundTrips, toolCalls, textChars,
                transcriptJson: MafAgentRunner.BuildTranscriptJson(_liveTranscript),
                ct: CancellationToken.None,
                phase: _phaseProvider?.Invoke());
        }
        catch { /* best-effort — never break a run */ }

        return response;
    }
}
