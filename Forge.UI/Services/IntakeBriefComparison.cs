using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;

namespace Forge.Dashboard.Services;

public sealed record IntakeBriefEdit(string Text, string Kind);

public sealed record IntakeBriefChange(string Heading, string? Before, string? After, bool Moved = false,
    string Section = "Brief", IReadOnlyList<IntakeBriefEdit>? Edits = null, int? Ordinal = null, bool HasMarkup = false)
{
    public bool Changed => Moved || !string.Equals(Before, After, StringComparison.Ordinal);
    public IReadOnlyList<IntakeBriefEdit> InlineEdits => Edits ?? [];
}

/// <summary>Compares individual paragraphs and criteria, retaining complete recorded Markdown.</summary>
public static class IntakeBriefComparison
{
    private const int MaximumUnits = 512;
    private sealed record Unit(string SectionKey, string Section, string Heading, string Body, string Kind, string? Label, int? Ordinal = null);

    public static IReadOnlyList<IntakeBriefChange> Compare(string before, string after)
    {
        var original = Units(before);
        var revised = Units(after);
        if (original.Count > MaximumUnits || revised.Count > MaximumUnits)
            return [new("Complete brief", before, after)];

        var matches = new int?[revised.Count];
        var used = new HashSet<int>();
        // Stable criterion labels and exact text survive insertions and reordered lists.
        Match((old, next) => old.Label is not null && old.Label == next.Label);
        Match((old, next) => old.Body == next.Body);
        Match((old, next) => old.Label is null && next.Label is null);
        var oldOrder = matches.OfType<int>().Order().ToArray();
        var newOrder = matches.OfType<int>().ToArray();
        var result = new List<IntakeBriefChange>();
        for (var i = 0; i < revised.Count; i++)
        {
            var next = revised[i];
            var prior = matches[i] is { } index ? original[index] : null;
            var moved = matches[i] is { } matched && Array.IndexOf(oldOrder, matched) != Array.IndexOf(newOrder, matched);
            var hasMarkup = false;
            var edits = prior is not null && prior.Body != next.Body && next.Kind is "paragraph" or "criterion"
                ? WordChanges(prior.Body, next.Body, out hasMarkup) : [];
            result.Add(new(next.Heading, prior?.Body, next.Body, moved, next.Section, edits, next.Ordinal, hasMarkup));
        }
        for (var i = 0; i < original.Count; i++)
            if (!used.Contains(i))
                result.Add(new(original[i].Heading, original[i].Body, null, Section: original[i].Section, Ordinal: original[i].Ordinal));
        // Markdown may change the document's structure without changing the extracted prose.
        // Keep both versions reviewable in that case (for example, a list now starts at 5).
        if (before != after && !result.Any(change => change.Changed))
            return [new("Document formatting or structure", before, after, HasMarkup: true)];
        return result;

        void Match(Func<Unit, Unit, bool> predicate)
        {
            for (var i = 0; i < revised.Count; i++)
            {
                if (matches[i] is not null) continue;
                var next = revised[i];
                var match = -1;
                for (var j = 0; j < original.Count; j++)
                {
                    var old = original[j];
                    if (used.Contains(j) || old.SectionKey != next.SectionKey || old.Kind != next.Kind || !predicate(old, next)) continue;
                    match = j;
                    break;
                }
                if (match < 0) continue;
                matches[i] = match;
                used.Add(match);
            }
        }
    }

    private static List<Unit> Units(string source)
    {
        var document = Markdown.Parse(source);
        var result = new List<Unit>();
        var ancestors = new List<(int Level, string Title)>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var sectionKey = "preamble";
        var section = document.OfType<HeadingBlock>().Any() ? "Introduction" : "Brief";
        var hasBody = true;
        foreach (var block in document)
        {
            if (block is HeadingBlock heading)
            {
                AddEmptyHeading();
                var title = Markdown.ToPlainText(Slice(heading)).Trim();
                ancestors.RemoveAll(ancestor => ancestor.Level >= heading.Level);
                ancestors.Add((heading.Level, title));
                var titles = ancestors.Select(ancestor => ancestor.Title).ToArray();
                var path = JsonSerializer.Serialize(titles);
                occurrences.TryGetValue(path, out var occurrence);
                occurrences[path] = ++occurrence;
                sectionKey = $"{occurrence}:{path}";
                section = string.Join(" / ", titles) + (occurrence > 1 ? $" ({occurrence})" : "");
                hasBody = false;
                continue;
            }
            hasBody = true;
            if (block is ListBlock list)
            {
                var start = Regex.Match(Slice(list), @"^\s*(\d+)[.)]\s");
                int? ordinal = list.IsOrdered && int.TryParse(start.Groups[1].Value, out var number) ? number : null;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var body = Slice(item);
                    body = Regex.Replace(body, @"^\s*(?:[-+*]|\d+[.)])\s+", "");
                    var label = Regex.Match(body, @"^\*\*(?<label>[^\r\n]+?)\*\*\s*");
                    var caption = label.Success ? Markdown.ToPlainText(label.Groups["label"].Value).Trim().TrimEnd(':').TrimEnd() : null;
                    if (label.Success) body = body[label.Length..];
                    var kind = item.Count == 1 && item[0] is ParagraphBlock ? "criterion" : "listblock";
                    result.Add(new(sectionKey, section, caption ?? section, body.Trim(), kind, caption, ordinal));
                    if (ordinal is not null) ordinal++;
                }
            }
            else
                result.Add(new(sectionKey, section, section, Slice(block).Trim(), block is ParagraphBlock ? "paragraph" : "block", null));
        }
        AddEmptyHeading();
        return result;

        string Slice(Block block) => source[block.Span.Start..Math.Min(source.Length, block.Span.End + 1)];
        void AddEmptyHeading()
        {
            if (!hasBody) result.Add(new(sectionKey, section, section, "", "heading", null));
        }
    }

    private static IReadOnlyList<IntakeBriefEdit> WordChanges(string before, string after, out bool hasMarkup)
    {
        var first = Markdown.ToPlainText(before).Trim();
        var second = Markdown.ToPlainText(after).Trim();
        hasMarkup = first != before || second != after;
        // Formatting-only edits must keep their recorded Markdown visible.
        if (first == second) return [];
        var oldWords = Regex.Matches(first, @"\S+\s*|\s+").Select(match => match.Value).ToArray();
        var newWords = Regex.Matches(second, @"\S+\s*|\s+").Select(match => match.Value).ToArray();
        if ((long)oldWords.Length * newWords.Length > 262144)
            return [];
        var lengths = new int[oldWords.Length + 1, newWords.Length + 1];
        for (var i = oldWords.Length - 1; i >= 0; i--)
            for (var j = newWords.Length - 1; j >= 0; j--)
                lengths[i, j] = oldWords[i] == newWords[j] ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
        var edits = new List<IntakeBriefEdit>();
        var oldIndex = 0;
        var newIndex = 0;
        while (oldIndex < oldWords.Length || newIndex < newWords.Length)
        {
            if (oldIndex < oldWords.Length && newIndex < newWords.Length && oldWords[oldIndex] == newWords[newIndex])
            {
                Add(oldWords[oldIndex++], "unchanged");
                newIndex++;
            }
            else if (oldIndex < oldWords.Length && (newIndex == newWords.Length || lengths[oldIndex + 1, newIndex] >= lengths[oldIndex, newIndex + 1]))
                Add(oldWords[oldIndex++], "removed");
            else
                Add(newWords[newIndex++], "added");
        }
        return edits;

        void Add(string text, string kind)
        {
            if (edits.Count > 0 && edits[^1].Kind == kind)
                edits[^1] = edits[^1] with { Text = edits[^1].Text + text };
            else edits.Add(new(text, kind));
        }
    }
}
