using System.Text.Json;
using Markdig;
using Markdig.Syntax;

namespace Forge.Dashboard.Services;

public sealed record IntakeProposal(string Id, string ProjectId, int Revision, string Hash, string Title,
    string Origin, string? SessionId, DateTime? ApprovedAt, string? ApprovedBy,
    string? EpicId, string? SpecId, string? Body = null, string? SourceJson = null, int Priority = 2);
public sealed record IntakeProposalPage(List<IntakeProposal> Items, string? NextCursor);
public sealed record IntakeIngestion(string EpicId, string SpecId, string RefinementState, string? LastError);
public sealed record IntakeProposalDetail(IntakeProposal Proposal, IntakeIngestion? Ingestion);
public sealed record IntakeProposalRevision(int Revision, string Title, string Body, int Priority, string Hash);
public sealed record IntakeBriefSpec(string Id, string Status);
public sealed record IntakeBriefChange(string Heading, string? Before, string? After, bool Moved = false)
{
    public bool Changed => Moved || !string.Equals(Before, After, StringComparison.Ordinal);
}

/// <summary>Compares recorded Markdown sections without treating code-block headings as structure.</summary>
public static class IntakeBriefComparison
{
    public static IReadOnlyList<IntakeBriefChange> Compare(string before, string after)
    {
        var original = Sections(before);
        var revised = Sections(after);
        var originalOrder = original.Where(section => revised.Any(other => other.Key == section.Key))
            .Select(section => section.Key).ToList();
        var revisedOrder = revised.Where(section => original.Any(other => other.Key == section.Key))
            .Select(section => section.Key).ToList();
        var changes = revised.Select(section => new IntakeBriefChange(section.Heading,
            original.FirstOrDefault(other => other.Key == section.Key)?.Text, section.Text,
            originalOrder.IndexOf(section.Key) != revisedOrder.IndexOf(section.Key))).ToList();
        changes.AddRange(original.Where(section => revised.All(other => other.Key != section.Key))
            .Select(section => new IntakeBriefChange(section.Heading, section.Text, null)));
        return changes;
    }

    private sealed record Section(string Key, string Heading, string Text);

    private static IReadOnlyList<Section> Sections(string source)
    {
        var headings = Markdown.Parse(source).OfType<HeadingBlock>().ToArray();
        if (headings.Length == 0) return [new("preamble", "Brief", source)];
        var result = new List<Section>();
        if (headings[0].Span.Start > 0) result.Add(new("preamble", "Introduction", source[..headings[0].Span.Start]));
        var ancestors = new List<(int Level, string Title)>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < headings.Length; i++)
        {
            var heading = headings[i];
            var title = Markdown.ToPlainText(source[heading.Span.Start..(heading.Span.End + 1)]).Trim();
            ancestors.RemoveAll(ancestor => ancestor.Level >= heading.Level);
            ancestors.Add((heading.Level, title));
            var titles = ancestors.Select(ancestor => ancestor.Title).ToArray();
            var path = JsonSerializer.Serialize(titles);
            occurrences.TryGetValue(path, out var occurrence);
            occurrences[path] = ++occurrence;
            var label = string.Join(" / ", titles);
            if (occurrence > 1) label += $" ({occurrence})";
            var end = i + 1 < headings.Length ? headings[i + 1].Span.Start : source.Length;
            result.Add(new($"{occurrence}:{path}", label, source[heading.Span.Start..end]));
        }
        return result;
    }
}
