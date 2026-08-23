using System.ComponentModel;
using Microsoft.Extensions.AI;
using Forge.Core;

namespace Forge.AgentTools;

/// <summary>
/// Lets an agent file a follow-up task for work it discovered but
/// that is out of scope for its current run — the only agent-
/// initiated issue-creation path (everything else is the operator
/// or the spec groomer).
///
/// <para>
/// Filed follow-ups are deliberately NOT sprint-eligible: they land
/// as parentless Pending tasks with no <c>groomed</c> marker, so
/// the sprint assembler ignores them until the ScheduledGroomer's
/// ad-hoc pass verifies them against the vision and current state.
/// (A follow-up parented into a groomed spec chain would inherit
/// the chain's eligibility and bypass grooming — hence parentless
/// with a <c>followUpOf</c> metadata trail instead.)
/// </para>
/// </summary>
public sealed class FollowUpTool
{
    private readonly IIssueStore _issues;
    private readonly string _sourceIssueId;
    private readonly string _sourceRole;
    private readonly FollowUpDraftStore? _drafts;
    private readonly Func<CancellationToken, Task<string?>>? _activeSprintId;
    private readonly ISprintStore? _sprints;

    public FollowUpTool(IIssueStore issues, string sourceIssueId, string sourceRole,
        FollowUpDraftStore? drafts = null,
        Func<CancellationToken, Task<string?>>? activeSprintId = null,
        ISprintStore? sprints = null)
    {
        _issues = issues;
        _sourceIssueId = sourceIssueId;
        _sourceRole = sourceRole;
        _drafts = drafts;
        _activeSprintId = activeSprintId;
        _sprints = sprints;
    }

    public AIFunction AsAIFunction() => AIFunctionFactory.Create(
        ([Description("One-line task title stating the follow-up work.")] string title,
         [Description("What you found, where (file:line / PR / command output), and why it matters. Enough context for grooming + a future engineering run that has never seen this conversation.")] string description,
         [Description("Priority 1-5; defaults to 3.")] int? priority = null,
         [Description("OPTIONAL: an issue id this follow-up BLOCKS — use ONLY when that issue genuinely cannot proceed until this work lands (the discovery blocks in-flight work). Creates a real dependency edge at filing time and adds the follow-up to the blocked work's active sprint immediately. Omit for ordinary deferred findings.")] string? blocksIssueId = null,
         [Description("Task type — routes dispatch to the right role. 'task' (default) = coredev (Core/sim/tests/docs). 'client' (or 'ui'/'godot') = clientdev (Client/Scripts, scenes, UI). 'qa' (or 'playtest'/'test') = the QA role (playthroughs, evidence). Set it when the work is obviously one of those — a mistyped follow-up dies at the plan-territory gate.")] string? taskType = null)
            => FileAsync(title, description, priority, blocksIssueId, taskType),
        name: "file_followup",
        description: "Track follow-up work you discovered that is out of scope for your current task " +
                     "(tech debt, a bug elsewhere, a deferred review finding). Tracked findings do " +
                     "NOT become tasks now: they are reviewed and created at sprint completion, then " +
                     "groomed before any sprint. Do NOT use this for work that is part of your " +
                     "current task. If the discovery BLOCKS in-flight work, pass blocksIssueId — that " +
                     "creates a real blocking task immediately (the only exception).");

    private async Task<string> FileAsync(string title, string description, int? priority, string? blocksIssueId, string? taskType)
    {
        if (string.IsNullOrWhiteSpace(title)) return "title_required";
        if (string.IsNullOrWhiteSpace(description)) return "description_required";
        var type = string.IsNullOrWhiteSpace(taskType) ? "task" : taskType.Trim().ToLowerInvariant();

        // Operator model 2026-07-31: deferred findings are TRACKED,
        // not created — no follow-up tasks against unmerged work.
        // They materialize at sprint completion and go through
        // grooming. Only a genuine blocker (blocksIssueId) becomes a
        // real task immediately.
        if (string.IsNullOrWhiteSpace(blocksIssueId) && _drafts is not null)
        {
            var sprintId = _activeSprintId is not null ? await _activeSprintId(CancellationToken.None) : null;
            var draftId = await _drafts.FileAsync(new FollowUpDraft(
                Id: 0,
                SprintId: sprintId,
                SourceIssueId: _sourceIssueId,
                SourceRole: _sourceRole,
                Title: title.Trim(),
                Description: description.Trim(),
                Priority: Math.Clamp(priority ?? 3, 1, 5),
                BlocksIssueId: null,
                CreatedAt: DateTime.UtcNow,
                ConsumedAt: null,
                TaskType: type));
            return $"tracked:draft-{draftId} (reviewed at sprint completion; pass blocksIssueId only for genuine blockers)";
        }

        var issue = await _issues.CreateAsync(new NewIssue(
            Type: type,
            Title: title.Trim(),
            Description: description.Trim(),
            Priority: Math.Clamp(priority ?? 3, 1, 5),
            Metadata: new Dictionary<string, object>
            {
                ["source"] = _sourceRole,
                ["followUpOf"] = _sourceIssueId,
            }));

        // Case A (operator model 2026-07-31): a blocking discovery is
        // marked as a real dependency at creation — the blocked work
        // gates on it (IsBlockedAsync). When the blocked target is in
        // the active sprint, the blocker is BORN into that sprint
        // (it still needs grooming before it dispatches — the
        // dispatch loop skips ungroomed parentless members); blockers
        // of non-sprint work stay outside and inject via the
        // assembler's "unblocks ongoing work" rule once groomed.
        string? blockNote = null;
        if (!string.IsNullOrWhiteSpace(blocksIssueId))
        {
            var target = await _issues.GetAsync(blocksIssueId.Trim());
            if (target is null)
            {
                blockNote = $"blocks-target '{blocksIssueId}' not found — edge NOT created";
            }
            else
            {
                await _issues.AddDependencyAsync(issue.Id, target.Id, IssueDepKind.Blocks);
                blockNote = $"blocks {target.Id} (dependency edge created)";

                // Operator rule 2026-07-31: a genuine blocker of ACTIVE
                // sprint work is born inside the sprint — the blocked
                // member is already stalled, so waiting for the
                // assembler's injection tick (or worse, sprint
                // completion) just extends the stall. Only when the
                // blocked target is actually a member of the active
                // sprint; blockers of non-sprint work stay outside.
                if (_sprints is not null)
                {
                    var active = await _sprints.GetActiveAsync(CancellationToken.None);
                    if (active is not null)
                    {
                        var members = await _sprints.GetIssueIdsAsync(active.Id, CancellationToken.None);
                        if (members.Contains(target.Id))
                        {
                            await _sprints.AddIssueAsync(active.Id, issue.Id, CancellationToken.None);
                            var created = await _issues.GetAsync(issue.Id);
                            if (created is not null)
                            {
                                var meta = ReadMetadata(created);
                                meta["sprintId"] = active.Id;
                                await _issues.TransitionAsync(created.Id, created.Status, error: null, metadata: meta);
                            }
                            blockNote = $"blocks {target.Id} (dependency edge created; added to active sprint {active.Name})";
                        }
                    }
                }
            }
        }

        // Audit trail on the source task: append to its followUps list
        // (read-modify-write; metadata is replaced wholesale by
        // TransitionAsync, so preserve everything already there).
        var source = await _issues.GetAsync(_sourceIssueId);
        if (source is not null)
        {
            var meta = ReadMetadata(source);
            var existing = source.GetMetadata("followUps");
            meta["followUps"] = string.IsNullOrWhiteSpace(existing) ? issue.Id : $"{existing},{issue.Id}";
            await _issues.TransitionAsync(source.Id, source.Status, error: null, metadata: meta);
        }

        var result = $"filed:{issue.Id} (pending technical grooming; not yet sprint-eligible)";
        return blockNote is null ? result : $"{result}; {blockNote}";
    }

    private static Dictionary<string, object> ReadMetadata(IssueRecord issue)
    {
        var meta = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(issue.MetadataJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(issue.MetadataJson);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        meta[p.Name] = p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                            ? p.Value.GetString()!
                            : p.Value.GetRawText();
                    }
                }
            }
            catch { /* malformed metadata: start fresh */ }
        }
        return meta;
    }
}
