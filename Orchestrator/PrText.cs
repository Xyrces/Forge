using System.Text;
using Forge.Core;

namespace Forge.Orchestrator;

/// <summary>
/// PR title/body composition (operator rule 2026-08-06, after PR
/// #817 shipped the whole model conversation as its description):
/// titles carry the task id and stay succinct; bodies are a
/// structured, bounded summary — never a raw transcript dump. The
/// model's final text is sanitized (resume artifacts like
/// "Conversation resumed" and leaked tool-call markup are junk
/// context, not a summary) and capped; when nothing usable remains
/// the task description is the fallback summary.
/// </summary>
public static class PrText
{
    /// <summary>GitHub's hard title limit is 256; PR lists truncate
    /// far earlier. 120 keeps the task id + a meaningful slug.</summary>
    private const int MaxTitleLength = 120;
    private const int MaxSummaryChars = 2000;
    private const int MaxDescriptionChars = 600;

    public static string Title(IssueRecord issue)
    {
        var prefix = $"Task({issue.Id}): ";
        var room = MaxTitleLength - prefix.Length;
        var title = issue.Title.Length <= room ? issue.Title : issue.Title[..room].TrimEnd() + "…";
        return prefix + title;
    }

    public static string Body(IssueRecord issue, string? headSha, string? modelText, string? note = null)
    {
        var sb = new StringBuilder();
        sb.Append("## Task\n\n");
        sb.Append("**").Append(issue.Id).Append("** — ").Append(issue.Title).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(issue.Description))
        {
            sb.Append(Cap(issue.Description.Trim(), MaxDescriptionChars)).Append("\n\n");
        }

        var summary = SanitizeModelSummary(modelText);
        // When the model text is junk but the description already
        // carried the load above, don't repeat it verbatim.
        if (summary is not null
            && !string.Equals(summary.Trim(), issue.Description?.Trim(), StringComparison.Ordinal))
        {
            sb.Append("## Implementation\n\n").Append(summary).Append("\n\n");
        }

        if (note is not null)
        {
            sb.Append('_').Append(note).Append("_\n\n");
        }
        sb.Append("---\n<sub>Opened by Forge");
        if (!string.IsNullOrEmpty(headSha))
        {
            sb.Append(" · head `").Append(headSha.Length > 8 ? headSha[..8] : headSha).Append('`');
        }
        sb.Append("</sub>");
        return sb.ToString();
    }

    /// <summary>Null when the text is unusable as a summary.</summary>
    internal static string? SanitizeModelSummary(string? modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText)) return null;
        var text = modelText.Trim();
        // Cold-restart / resume artifacts carry zero information
        // about the change ("Conversation resumed", "Session
        // resumed — continuing").
        if (text.StartsWith("Conversation resumed", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Session resumed", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        // Leaked tool-call markup (the minimax quirk) is protocol
        // garbage, not prose.
        var lines = text.Split('\n')
            .Where(l => !l.Contains("]<]minimax[", StringComparison.Ordinal)
                && !l.Contains("<tool_call>", StringComparison.Ordinal)
                && !l.Contains("</tool_call>", StringComparison.Ordinal))
            .ToList();
        text = string.Join('\n', lines).Trim();
        if (text.Length == 0) return null;
        return Cap(text, MaxSummaryChars);
    }

    private static string Cap(string s, int n)
    {
        if (s.Length <= n) return s;
        var cut = s[..n];
        var lastBreak = cut.LastIndexOfAny(new[] { '\n', '.', ' ' });
        if (lastBreak > n / 2) cut = cut[..lastBreak];
        return cut.TrimEnd() + " …";
    }
}
