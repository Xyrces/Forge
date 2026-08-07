namespace Forge.Core;

/// <summary>
/// The completion-time follow-up triage (operator-approved design
/// 2026-07-31): one batch pass over a sprint's tracked follow-up
/// drafts — merge duplicates, group themes into epics, discard junk
/// (softly), pass through the rest. Implemented by an agent; the
/// assembler validates the output and falls back to 1:1
/// materialization when the triage is unavailable or invalid.
/// </summary>
public interface IFollowUpTriage
{
    /// <summary>Triage a sprint's unconsumed drafts. Returns null
    /// when the triage cannot run (outage, unparseable output) — the
    /// caller falls back to 1:1 materialization.</summary>
    Task<FollowUpTriageDecision?> TriageAsync(
        string projectId, IReadOnlyList<FollowUpDraft> drafts, CancellationToken ct = default);
}

/// <summary>One validated triage action. Actions:
/// <c>create</c> (a task from one draft), <c>merge</c> (one task
/// from several drafts, descriptions concatenated), <c>epic</c>
/// (a spec from several drafts, flowing the normal groom path),
/// <c>discard</c> (soft — nothing created, reason recorded).</summary>
public sealed record TriageItem(
    string Action,
    IReadOnlyList<long> SourceDraftIds,
    string? Title = null,
    string? Description = null,
    int? Priority = null,
    string? Reason = null);

public sealed record FollowUpTriageDecision(IReadOnlyList<TriageItem> Items);
