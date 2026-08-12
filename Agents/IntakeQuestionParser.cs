using System.Text.RegularExpressions;
using Forge.Core;

namespace Forge.Agents;

/// <summary>
/// Fallback for models that don't call the ask_question tool: detect
/// numbered/bulleted clarifying questions in the assistant's reply
/// text and lift them into structured <see cref="IntakeQuestion"/>s
/// so the intake page can render clickable cards anyway (operator
/// request 2026-08-12: "We may benefit from structured responses for
/// this — though we will need a fallback for models that don't
/// support that").
///
/// Recognition rules (conservative — a false positive renders a
/// harmless card, a false negative just stays plain text):
/// <list type="bullet">
///   <item>A question is a list item (numbered or bulleted) whose
///   line contains a '?'. The question text runs through the last
///   '?' on the line.</item>
///   <item>The header comes from a leading **bold** segment
///   ("**Transport scope** — …").</item>
///   <item>Options are the indented sub-bullets immediately following
///   the question line (a sub-bullet containing '?' ends the option
///   run — it's the next question).</item>
///   <item>Without sub-bullets, yes/no-shaped questions get real
///   options: an "…, or X?" branch becomes the second choice
///   ("Also: X"); otherwise plain Yes/No. Genuinely open questions
///   keep empty options and render as a free-text input — choices are
///   never synthesized beyond what the text says.</item>
///   <item>"Which of the following/these", "select all", "which
///   apply" mark the question multi-select.</item>
/// </list>
/// Capped at 8 questions / 5 options each so a runaway list never
/// floods the card.
/// </summary>
public static class IntakeQuestionParser
{
    private const int MaxQuestions = 8;
    private const int MaxOptions = 5;
    private const int MaxQuestionLength = 240;
    private const int MaxOptionLength = 160;

    private static readonly Regex ListItem = new(
        @"^\s*(?:\d+[\.)]|[-*•])\s+", RegexOptions.Compiled);

    private static readonly Regex SubBullet = new(
        @"^\s{2,}[-*•]\s+", RegexOptions.Compiled);

    private static readonly Regex BoldHeader = new(
        @"\*\*(.+?)\*\*", RegexOptions.Compiled);

    private static readonly Regex YesNoPrefix = new(
        @"^(?:Is|Are|Should|Do|Does|Can|Confirm|Would|Could)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MultipleHint = new(
        @"\b(?:which of (?:the )?(?:following|these|existing)|select all|which apply|choose all)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "or do you also want point-to-point queues" → "point-to-point queues"
    private static readonly Regex OrBranchLead = new(
        @"^(?:(?:do|does|did|would|should|can|could|are|is|will)\s+(?:you|we)\s+)?(?:also\s+)?(?:want|need|like|prefer|have|use|go\s+with|plan(?:\s+(?:around|on|to))?(?:\s+to)?|add|include|support)\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<IntakeQuestion> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<IntakeQuestion>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var questions = new List<IntakeQuestion>();

        for (var i = 0; i < lines.Length && questions.Count < MaxQuestions; i++)
        {
            var line = lines[i];
            if (!line.Contains('?')) continue;
            if (!ListItem.IsMatch(line)) continue;

            var questionText = ListItem.Replace(line, "", 1).Trim();
            var lastQ = questionText.LastIndexOf('?');
            if (lastQ < 0) continue;
            questionText = questionText[..(lastQ + 1)].Trim();
            if (questionText.Length == 0) continue;
            if (questionText.Length > MaxQuestionLength)
                questionText = questionText[..MaxQuestionLength].TrimEnd() + "…";

            string? header = null;
            var headerMatch = BoldHeader.Match(questionText);
            if (headerMatch.Success)
                header = headerMatch.Groups[1].Value.Trim();

            var multiple = MultipleHint.IsMatch(questionText);

            var options = new List<string>();
            while (i + 1 < lines.Length && options.Count < MaxOptions)
            {
                var next = lines[i + 1];
                if (!SubBullet.IsMatch(next)) break;
                if (next.Contains('?')) break;
                var opt = SubBullet.Replace(next, "", 1).Trim();
                if (opt.Length == 0) break;
                if (opt.Length > MaxOptionLength)
                    opt = opt[..MaxOptionLength].TrimEnd() + "…";
                options.Add(opt);
                i++;
            }

            if (options.Count == 0)
                options.AddRange(SynthesizeChoiceOptions(questionText, header));

            questions.Add(new IntakeQuestion(questionText, options, header, multiple));
        }

        return questions;
    }

    /// <summary>
    /// Write-time-only option extraction for questions the model
    /// phrased as text: "Is X the right scope, or do you also want
    /// Y?" → ["X", "Also: Y"]. Plain yes/no questions (no or-branch)
    /// → ["Yes", "No"]. Everything else stays free-form. Never runs
    /// at read time — what you see is what the model said.
    /// </summary>
    private static IEnumerable<string> SynthesizeChoiceOptions(string questionText, string? header)
    {
        if (!YesNoPrefix.IsMatch(questionText)) yield break;

        // Prefer the LAST " or " before the final '?' as the
        // alternative branch.
        var trimmed = questionText.TrimEnd('?', ' ');
        var orIdx = trimmed.LastIndexOf(" or ", StringComparison.OrdinalIgnoreCase);
        if (orIdx < 0)
        {
            yield return "Yes";
            yield return "No";
            yield break;
        }

        var branch = trimmed[(orIdx + 4)..].Trim();
        branch = OrBranchLead.Replace(branch, "", 1).Trim().TrimEnd('?', ' ', '.');
        if (branch.Length == 0)
        {
            yield return "Yes";
            yield return "No";
            yield break;
        }
        branch = char.ToUpperInvariant(branch[0]) + branch[1..];
        if (branch.Length > MaxOptionLength)
            branch = branch[..MaxOptionLength].TrimEnd() + "…";

        yield return header is not null ? $"Yes — {header}" : "Yes — as described";
        yield return $"Also: {branch}";
    }
}
