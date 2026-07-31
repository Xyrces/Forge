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

    public FollowUpTool(IIssueStore issues, string sourceIssueId, string sourceRole)
    {
        _issues = issues;
        _sourceIssueId = sourceIssueId;
        _sourceRole = sourceRole;
    }

    public AIFunction AsAIFunction() => AIFunctionFactory.Create(
        ([Description("One-line task title stating the follow-up work.")] string title,
         [Description("What you found, where (file:line / PR / command output), and why it matters. Enough context for grooming + a future engineering run that has never seen this conversation.")] string description,
         [Description("Priority 1-5; defaults to 3.")] int? priority = null)
            => FileAsync(title, description, priority),
        name: "file_followup",
        description: "File a follow-up task for out-of-scope work you discovered " +
                     "(tech debt, a bug elsewhere, a deferred review finding). The task is " +
                     "NOT scheduled immediately: it goes through technical grooming " +
                     "(vision + current-state check) before it can enter a sprint. " +
                     "Do NOT use this for work that is part of your current task.");

    private async Task<string> FileAsync(string title, string description, int? priority)
    {
        if (string.IsNullOrWhiteSpace(title)) return "title_required";
        if (string.IsNullOrWhiteSpace(description)) return "description_required";

        var issue = await _issues.CreateAsync(new NewIssue(
            Type: "task",
            Title: title.Trim(),
            Description: description.Trim(),
            Priority: Math.Clamp(priority ?? 3, 1, 5),
            Metadata: new Dictionary<string, object>
            {
                ["source"] = _sourceRole,
                ["followUpOf"] = _sourceIssueId,
            }));

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

        return $"filed:{issue.Id} (pending technical grooming; not yet sprint-eligible)";
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
