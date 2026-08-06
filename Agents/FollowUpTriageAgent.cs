using System.Text.Json;
using Forge.Configuration;
using Forge.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Forge.Agents;

/// <summary>
/// The completion-time follow-up triage agent (operator-approved
/// 2026-07-31): one bounded session per sprint completion. Reviews
/// the sprint's tracked drafts as a BATCH — merges duplicates,
/// groups themes into epics, discards junk with a reason, passes the
/// rest through. Runs on the intake model; output is a strict JSON
/// contract the assembler validates (unknown source ids dropped,
/// uncited drafts 1:1-materialized — the agent can shape, never
/// invent or lose work).
/// </summary>
public sealed class FollowUpTriageAgent : IFollowUpTriage
{
    private const int MaxDraftsPerPass = 60;

    private readonly IChatClientFactory _chatClientFactory;
    private readonly LlmConfig _config;
    private readonly ILogger<FollowUpTriageAgent> _logger;

    public FollowUpTriageAgent(
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        ILogger<FollowUpTriageAgent> logger)
    {
        _chatClientFactory = chatClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<FollowUpTriageDecision?> TriageAsync(
        string projectId, IReadOnlyList<FollowUpDraft> drafts, CancellationToken ct = default)
    {
        if (drafts.Count == 0) return new FollowUpTriageDecision(Array.Empty<TriageItem>());

        var prompt = BuildPrompt(drafts.Take(MaxDraftsPerPass).ToList());
        try
        {
            var chatClient = _chatClientFactory.Create(_config, AgentType.Intake, projectId);
            var response = await chatClient.GetResponseAsync(
                new[]
                {
                    new ChatMessage(ChatRole.System, SystemPrompt),
                    new ChatMessage(ChatRole.User, prompt),
                },
                cancellationToken: ct);
            var text = response.Text ?? "";
            return Parse(text, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FollowUpTriage: triage run failed (project={Project}); caller falls back to 1:1 materialization", projectId);
            return null;
        }
    }

    private const string SystemPrompt =
        "You triage a sprint's tracked follow-up findings in ONE batch. For each draft you must choose " +
        "exactly one action:\n" +
        "- merge: this draft duplicates/overlaps others — one merged work item for the whole group.\n" +
        "- create: unique, well-formed work — one task as-is.\n" +
        "- epic: this draft belongs to a theme of 3+ related drafts — group them into one epic.\n" +
        "- discard: junk (failure artifact, stale, already fixed, incoherent) — always with a reason.\n\n" +
        "Rules: every draft id must appear in EXACTLY ONE item's sources. Never invent work that has no " +
        "source draft. Titles stay terse and technical. Output ONLY a JSON array, no prose:\n" +
        "[{\"action\":\"merge\",\"title\":\"...\",\"description\":\"...\",\"priority\":2,\"sources\":[1,7]}," +
        "{\"action\":\"epic\",\"title\":\"...\",\"description\":\"...\",\"sources\":[2,3,4]}," +
        "{\"action\":\"create\",\"title\":\"...\",\"priority\":3,\"sources\":[5]}," +
        "{\"action\":\"discard\",\"reason\":\"...\",\"sources\":[6]}]";

    private static string BuildPrompt(IReadOnlyList<FollowUpDraft> drafts)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Tracked follow-up drafts filed during the sprint:\n");
        foreach (var d in drafts)
        {
            sb.AppendLine($"--- draft {d.Id} (P{d.Priority}, from {d.SourceIssueId} via {d.SourceRole}) ---");
            sb.AppendLine($"title: {d.Title}");
            sb.AppendLine($"description: {d.Description[..Math.Min(800, d.Description.Length)]}");
            sb.AppendLine();
        }
        sb.AppendLine("Output the JSON array now.");
        return sb.ToString();
    }

    /// <summary>Parse the model's JSON contract. Lenient about
    /// surrounding prose; strict about shape. Internal for tests.</summary>
    internal static FollowUpTriageDecision? Parse(string text, ILogger? logger = null)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var items = new List<TriageItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var action = el.TryGetProperty("action", out var a) ? a.GetString()?.ToLowerInvariant() : null;
                if (action is not ("create" or "merge" or "epic" or "discard")) continue;
                var sources = el.TryGetProperty("sources", out var s) && s.ValueKind == JsonValueKind.Array
                    ? s.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt64()).ToList()
                    : new List<long>();
                if (sources.Count == 0) continue;
                items.Add(new TriageItem(
                    Action: action,
                    SourceDraftIds: sources,
                    Title: el.TryGetProperty("title", out var t) ? t.GetString() : null,
                    Description: el.TryGetProperty("description", out var d) ? d.GetString() : null,
                    Priority: el.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null,
                    Reason: el.TryGetProperty("reason", out var r) ? r.GetString() : null));
            }
            return new FollowUpTriageDecision(items);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "FollowUpTriage: unparseable model output");
            return null;
        }
    }
}
