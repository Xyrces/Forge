using System.Text.RegularExpressions;

namespace Forge.Dashboard.Services;

public static class IntakeBriefReading
{
    public const string DraftingNote = "Draft only; explicit operator approval is required.";

    public static bool IsDraftingNote(IntakeBriefChange change) =>
        (change.Before is null || change.Before.Trim() == DraftingNote)
        && (change.After is null || change.After.Trim() == DraftingNote);

    public static string BodyWithoutDraftingNote(string body)
    {
        var text = body.TrimEnd();
        if (!text.EndsWith(DraftingNote, StringComparison.Ordinal)) return body;
        var index = text.Length - DraftingNote.Length;
        if (index > 0 && text[index - 1] != '\n') return body;
        return text[..index].TrimEnd();
    }

    // Quote complete sentences around an early substantive edit. The complete diff remains available.
    public static (string Text, bool Previous) Excerpt(IntakeBriefChange change)
    {
        var previous = !change.InlineEdits.Any(edit => edit.Kind == "added")
            && change.InlineEdits.Any(edit => edit.Kind == "removed");
        var omitted = previous ? "added" : "removed";
        var changedKind = previous ? "removed" : "added";
        var current = string.Concat(change.InlineEdits.Where(edit => edit.Kind != omitted).Select(edit => edit.Text));
        if (current.Length == 0) return (change.After ?? change.Before ?? "", previous);
        var additions = new List<(int Start, int End)>();
        var position = 0;
        foreach (var edit in change.InlineEdits.Where(edit => edit.Kind != omitted))
        {
            if (edit.Kind == changedKind) additions.Add((position, position + edit.Text.Length));
            position += edit.Text.Length;
        }
        var boundaries = Regex.Matches(current, @"(?<=[.!?])\s+(?=[\p{Lu}\d""“])",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(match => (End: match.Index, Next: match.Index + match.Length))
            .Append((End: current.Length, Next: current.Length));
        var start = 0;
        var best = current;
        var bestScore = -1d;
        var candidates = new List<(int Start, int End, int Changed)>();
        foreach (var boundary in boundaries)
        {
            var length = boundary.End - start;
            var changed = additions.Sum(span => Math.Max(0, Math.Min(boundary.End, span.End) - Math.Max(start, span.Start)));
            var score = changed / Math.Sqrt(Math.Max(1, length));
            if (length > 0) candidates.Add((start, boundary.End, changed));
            if (length > 0 && score > bestScore)
            {
                best = current[start..boundary.End];
                bestScore = score;
            }
            start = boundary.Next;
        }
        var selected = candidates.FindIndex(sentence => sentence.Changed >= 24 && sentence.End - sentence.Start <= 360);
        if (selected >= 0)
        {
            var first = candidates[selected];
            var end = first.End;
            if (selected + 1 < candidates.Count && candidates[selected + 1].Changed > 0
                && candidates[selected + 1].End - first.Start <= 360)
                end = candidates[selected + 1].End;
            best = current[first.Start..end];
        }
        return (best.Trim(), previous);
    }
}
