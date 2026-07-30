using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Forge.Agents;
using Forge.Core;

namespace Forge.Orchestrator;

/// <summary>
/// P5.5: Auto-extract durable project memory from a completed
/// agent response.
///
/// <para>
/// The orchestrator just committed the agent's edits, pushed the
/// branch, and is about to open a PR. Before returning, we ask a
/// cheap LLM call: "given the model text that drove the edits,
/// are there any insights worth persisting as project memory?".
/// The model is expected to reply with a fenced
/// <c>&lt;memory&gt;...&lt;/memory&gt;</c> block containing one or
/// more <c>&lt;memory&gt;&lt;key&gt;...&lt;/key&gt;&lt;value&gt;...&lt;/value&gt;&lt;/memory&gt;</c>
/// entries. Each entry is namespaced under
/// <c>extraction/{issueId}/...</c> and persisted via
/// <see cref="MemoryStore.RememberAsync"/>.
/// </para>
///
/// <para>
/// Idempotency: <see cref="MemoryStore.RememberAsync"/> is a
/// upsert by key, so re-running the extractor on the same
/// response replaces the prior value (preserving the original
/// timestamp logic). Namespacing the key with the issue id
/// keeps per-task extractions out of <c>RecallAsync</c>'s global
/// set unless the operator queries with the
/// <c>extraction/{id}/</c> prefix — see
/// <c>GET /api/memory/extractions/{taskId}</c> in P5.6.
/// </para>
///
/// <para>
/// Failure mode: if the LLM call fails, the model returns
/// non-conforming text, or no block is present, the call is
/// treated as a no-op and the engineering dispatch continues.
/// Memory extraction is advisory; a flaky LLM must not break
/// the closed loop.
/// </para>
/// </summary>
public sealed class MemoryExtractor : IMemoryExtractor
{
    private readonly IChatClientFactory _chatClientFactory;
    private readonly LlmConfig _llmConfig;
    private readonly MemoryStore _memories;
    private readonly ISprintStore? _sprints;
    private readonly ILogger<MemoryExtractor> _logger;

    public MemoryExtractor(
        IChatClientFactory chatClientFactory,
        LlmConfig llmConfig,
        MemoryStore memories,
        ILogger<MemoryExtractor> logger,
        ISprintStore? sprints = null)
    {
        _chatClientFactory = chatClientFactory;
        _llmConfig = llmConfig;
        _memories = memories;
        _logger = logger;
        _sprints = sprints;
    }

    /// <summary>
    /// Extract and persist memories for one issue's commit.
    /// Returns a result envelope that the caller can log + the
    /// dashboard can render.
    /// </summary>
    public async Task<ExtractionResult> ExtractAsync(
        string issueId, string? modelText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelText))
        {
            return new ExtractionResult(
                IssueId: issueId, SourceChars: 0, ExtractedCount: 0,
                PersistedKeys: Array.Empty<string>(), Error: null);
        }

        IReadOnlyList<ExtractedMemory> items;
        try
        {
            var prompt = BuildPrompt(modelText);
            var client = _chatClientFactory.Create(_llmConfig, AgentType.Intake);
            var response = await client.GetResponseAsync(
                new[] { new ChatMessage(ChatRole.User, prompt) },
                new ChatOptions
                {
                    Temperature = 0.0f,  // extraction is deterministic
                    MaxOutputTokens = 600,
                },
                ct);
            var raw = response.Messages.LastOrDefault()?.Text ?? string.Empty;
            items = ParseBlock(raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Memory extraction LLM call failed for {Id}; continuing without memories",
                issueId);
            return new ExtractionResult(
                IssueId: issueId, SourceChars: modelText.Length, ExtractedCount: 0,
                PersistedKeys: Array.Empty<string>(),
                Error: ex.GetType().Name + ": " + ex.Message);
        }

        if (items.Count == 0)
        {
            return new ExtractionResult(
                IssueId: issueId, SourceChars: modelText.Length, ExtractedCount: 0,
                PersistedKeys: Array.Empty<string>(), Error: null);
        }

        var persisted = new List<string>(items.Count);
        // Sprint flow: when the issue belongs to the ACTIVE sprint,
        // each extracted memory is ALSO persisted under the shared
        // sprint namespace so sibling (and later) tasks in the same
        // sprint recall it via the `sprint/{id}/` prefix. Upsert by
        // key means later tasks enrich the same slot — that's the
        // shared-memory semantics.
        string? sprintPrefix = null;
        if (_sprints is not null)
        {
            try
            {
                var active = await _sprints.GetActiveAsync(ct);
                if (active is not null
                    && (await _sprints.GetIssueIdsAsync(active.Id, ct)).Contains(issueId))
                {
                    sprintPrefix = $"sprint/{active.Id}/";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sprint membership lookup failed for {Id}; skipping sprint memory", issueId);
            }
        }
        foreach (var item in items)
        {
            // Namespace under issueId so a future extractor run for a different
            // task can't collide, and so the operator can query per-task.
            var namespacedKey = string.IsNullOrEmpty(item.Key)
                ? $"extraction/{issueId}/{Guid.NewGuid():N}"
                : $"extraction/{issueId}/{SanitizeKey(item.Key)}";
            try
            {
                await _memories.RememberAsync(namespacedKey, item.Value, ttlDays: null, ct);
                persisted.Add(namespacedKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Memory persist failed for {Id}/{Key}; skipping", issueId, namespacedKey);
            }
            if (sprintPrefix is not null && !string.IsNullOrEmpty(item.Key))
            {
                var sprintKey = sprintPrefix + SanitizeKey(item.Key);
                try
                {
                    await _memories.RememberAsync(sprintKey, item.Value, ttlDays: null, ct);
                    persisted.Add(sprintKey);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Sprint memory persist failed for {Id}/{Key}; skipping", issueId, sprintKey);
                }
            }
        }
        return new ExtractionResult(
            IssueId: issueId, SourceChars: modelText.Length, ExtractedCount: items.Count,
            PersistedKeys: persisted, Error: null);
    }

    /// <summary>
    /// Build the prompt we send to the LLM. Static + public so
    /// tests can assert the shape.
    /// </summary>
    public static string BuildPrompt(string modelText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a memory curator for an AI coding agent system.");
        sb.AppendLine();
        sb.AppendLine("Given the agent's final response text below, extract any");
        sb.AppendLine("durable project insights worth persisting: decisions made,");
        sb.AppendLine("non-obvious facts learned, conventions discovered. Skip");
        sb.AppendLine("transient status (\"I just committed X\") and skip anything");
        sb.AppendLine("already obvious from code or commit messages.");
        sb.AppendLine();
        sb.AppendLine("Respond with a fenced block in this exact shape:");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine("<memory>");
        sb.AppendLine("<memory><key>short-snake-case-key</key><value>one-paragraph insight, &lt;=400 chars</value></memory>");
        sb.AppendLine("<memory><key>another-key</key><value>another insight</value></memory>");
        sb.AppendLine("</memory>");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("If there are no durable insights, respond with:");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine("<memory></memory>");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Do not include any prose outside the fenced block.");
        sb.AppendLine();
        sb.AppendLine("--- agent response ---");
        sb.AppendLine(Truncate(modelText, 6000));
        return sb.ToString();
    }

    /// <summary>
    /// Parse the model's response into a list of extracted memories.
    /// Tolerant: handles missing wrappers, multiple blocks, and
    /// stray whitespace. Public + static for testability.
    /// </summary>
    public static IReadOnlyList<ExtractedMemory> ParseBlock(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return Array.Empty<ExtractedMemory>();

        // We don't bother stripping an outer <memory>...</memory>
        // wrapper because the per-entry regex below matches each
        // inner <memory>...</memory> block whether or not an
        // outer wrapper is present. This is more robust when the
        // model forgets the wrapper (a real failure mode we've
        // seen).
        var result = new List<ExtractedMemory>();
        var matches = Regex.Matches(
            response,
            @"<memory>\s*<key>(?<key>.*?)</key>\s*<value>(?<value>.*?)</value>\s*</memory>",
            RegexOptions.Singleline);
        foreach (Match m in matches)
        {
            var key = Unescape(m.Groups["key"].Value.Trim());
            var value = Unescape(m.Groups["value"].Value.Trim());
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;
            if (value.Length > 2000) value = value[..2000];
            result.Add(new ExtractedMemory(key, value));
        }
        return result;
    }

    private static string SanitizeKey(string key)
    {
        // Memory keys are unique by exact match. Allow alnum,
        // dash, underscore, dot, slash. Collapse runs to single
        // dash to keep the namespace readable.
        var s = Regex.Replace(key.ToLowerInvariant(), @"[^a-z0-9._/-]+", "-");
        return s.Trim('-');
    }

    private static string Unescape(string s)
        => s.Replace("&lt;", "<")
           .Replace("&gt;", ">")
           .Replace("&quot;", "\"")
           .Replace("&apos;", "'")
           .Replace("&amp;", "&");

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}

public sealed record ExtractedMemory(string Key, string Value);

public sealed record ExtractionResult(
    string IssueId,
    int SourceChars,
    int ExtractedCount,
    IReadOnlyList<string> PersistedKeys,
    string? Error);