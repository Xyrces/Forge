using System.Text.RegularExpressions;
using Forge.Core;

namespace Forge.Agents;

/// <summary>
/// Fallback for models that don't call the ask_questions tool: detect
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
///   <item>Options are the indented sub-bullets immediately following
///   the question line (a sub-bullet containing '?' ends the option
///   run — it's the next question).</item>
///   <item>Questions without sub-bullets get an empty option list —
///   the page renders a free-form affordance.</item>
/// </list>
/// Capped at 8 questions / 6 options each so a runaway list never
/// floods the card.
/// </summary>
public static class IntakeQuestionParser
{
    private const int MaxQuestions = 8;
    private const int MaxOptions = 6;
    private const int MaxQuestionLength = 240;
    private const int MaxOptionLength = 120;

    private static readonly Regex ListItem = new(
        @"^\s*(?:\d+[\.)]|[-*•])\s+", RegexOptions.Compiled);

    private static readonly Regex SubBullet = new(
        @"^\s{2,}[-*•]\s+", RegexOptions.Compiled);

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

            questions.Add(new IntakeQuestion(questionText, options));
        }

        return questions;
    }
}
