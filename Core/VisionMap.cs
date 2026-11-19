using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Forge.Core;

public sealed record VisionMapDocument(string Revision, string Source, IReadOnlyList<VisionMapNode> Nodes);

/// <summary>Text is the exact source owned by this node, including its heading and line endings.</summary>
public sealed record VisionMapNode(string Id, string? ParentId, string Title, string Text);

public sealed record VisionMapEdit(
    string NodeId, string Operation, string Before, string After, string? Title = null);

/// <summary>
/// A lossless view of a Markdown document. All edits are validated against one source revision
/// before any result is returned. Heading boundaries are parsed by the same Markdown engine
/// used to render recorded prose, so headings in code, lists and quotations remain content.
/// </summary>
public static class VisionMap
{
    public static VisionMapDocument Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var nodes = ReadSections(source)
            .Select(section => new VisionMapNode(section.Id, section.ParentId, section.Title,
                source[section.Start..section.End]))
            .ToArray();
        return new VisionMapDocument(Hash(source), source, nodes);
    }

    public static string Apply(string source, string baseRevision, IReadOnlyList<VisionMapEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!string.Equals(Hash(source), baseRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("The vision changed after this draft was prepared. Review a fresh draft.");
        if (edits is null)
            throw new InvalidOperationException("A vision edit list is required.");

        var sections = ReadSections(source);
        var headings = ReadHeadings(source);
        var expectedHeadings = headings
            .Select(heading => new ExpectedHeading(heading.Start, 1, heading.Level, heading.Title))
            .ToList();
        var replacements = new List<Replacement>();
        foreach (var edit in edits)
        {
            if (edit is null || edit.Before is null || edit.After is null)
                throw new InvalidOperationException("Each vision edit needs its original and proposed text.");
            var section = sections.Find(item => item.Id == edit.NodeId)
                ?? throw new InvalidOperationException("The edited vision node no longer exists.");
            switch (edit.Operation)
            {
                case "replace":
                    replacements.Add(Replace(source, section, edit));
                    break;
                case "append":
                    RequireInsertion(edit);
                    replacements.Add(new Replacement(section.End, 0, Separate(source, section.End, edit.After)));
                    break;
                case "add":
                    RequireInsertion(edit);
                    if (string.IsNullOrWhiteSpace(edit.Title) || edit.Title.Any(char.IsControl))
                        throw new InvalidOperationException("A new node needs a single-line title.");
                    var level = section.Id == "root"
                        ? Math.Min(2, sections.Where(item => item.ParentId == "root")
                            .Select(item => item.Level).DefaultIfEmpty(2).Min())
                        : section.Level + 1;
                    if (level > 6)
                        throw new InvalidOperationException("Markdown supports at most six heading levels.");
                    var end = SubtreeEnd(source, sections, section);
                    var newline = PreferredNewline(source);
                    var content = new string('#', level) + " " + edit.Title.Trim() + newline + newline + edit.After;
                    var insertion = new Replacement(end, 0, Separate(source, end, content));
                    replacements.Add(insertion);
                    var addedHeadings = ReadHeadings(content);
                    if (addedHeadings.Count != 1 || addedHeadings[0].Level != level)
                        throw new InvalidOperationException("Add one node per edit, with its details in the body.");
                    expectedHeadings.Add(new ExpectedHeading(end, 0, level, addedHeadings[0].Title,
                        insertion, insertion.Text.IndexOf(new string('#', level), StringComparison.Ordinal)));
                    break;
                default:
                    throw new InvalidOperationException("Vision edits support replace, append and add.");
            }
        }

        var ordered = replacements.OrderBy(item => item.Start).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];
            if (current.Start < previous.Start + previous.Length
                || current.Start == previous.Start && current.Length > 0)
                throw new InvalidOperationException("Vision edits overlap. Combine them into one proposed change.");
        }

        var result = new StringBuilder(source);
        foreach (var edit in ordered.Reverse())
        {
            result.Remove(edit.Start, edit.Length);
            result.Insert(edit.Start, edit.Text);
        }
        var updated = result.ToString();
        var actualHeadings = ReadHeadings(updated).Select(item => (item.Start, item.Level, item.Title));
        var expected = expectedHeadings.OrderBy(item => item.Start).ThenBy(item => item.Order)
            .Select(item => (ShiftedStart(item, ordered), item.Level, item.Title));
        if (!actualHeadings.SequenceEqual(expected))
            throw new InvalidOperationException("The edit changes another node's Markdown structure. Prepare a more precise edit.");
        return updated;
    }

    private static Replacement Replace(string source, Section section, VisionMapEdit edit)
    {
        if (edit.Before.Length == 0)
            throw new InvalidOperationException("A replacement needs an exact original excerpt.");
        var owned = source[section.Start..section.End];
        var match = owned.IndexOf(edit.Before, StringComparison.Ordinal);
        if (match < 0 || owned.IndexOf(edit.Before, match + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("The original excerpt must match exactly once within its node.");
        var start = section.Start + match;
        var end = start + edit.Before.Length;
        if (section.HeadingStart >= 0 && start <= section.HeadingEnd && end > section.HeadingStart)
            throw new InvalidOperationException("Replace the node's content, leaving its heading intact.");
        return new Replacement(start, edit.Before.Length, edit.After);
    }

    private static int ShiftedStart(ExpectedHeading heading, IReadOnlyList<Replacement> edits)
    {
        var start = heading.Start + heading.WithinInsertion;
        foreach (var edit in edits)
        {
            if (edit.Start > heading.Start || ReferenceEquals(edit, heading.Insertion)) break;
            start += edit.Text.Length - edit.Length;
        }
        return start;
    }

    private static void RequireInsertion(VisionMapEdit edit)
    {
        if (edit.Before.Length != 0 || string.IsNullOrWhiteSpace(edit.After))
            throw new InvalidOperationException("An insertion needs empty original text and nonempty proposed content.");
    }

    private static int SubtreeEnd(string source, List<Section> sections, Section section)
    {
        if (section.Id == "root") return source.Length;
        return sections.FirstOrDefault(item => item.Start > section.Start && item.Level <= section.Level)?.Start
            ?? source.Length;
    }

    private static string Separate(string source, int position, string content)
    {
        var newline = PreferredNewline(source);
        var before = source[..position];
        var prefix = before.Length == 0 || before.EndsWith(newline + newline, StringComparison.Ordinal)
            ? ""
            : before.EndsWith(newline, StringComparison.Ordinal) ? newline : newline + newline;
        var suffix = content.EndsWith(newline + newline, StringComparison.Ordinal)
            ? ""
            : content.EndsWith(newline, StringComparison.Ordinal) ? newline : newline + newline;
        return prefix + content + suffix;
    }

    private static string PreferredNewline(string source)
    {
        var index = source.IndexOfAny(['\r', '\n']);
        if (index < 0) return "\n";
        return source[index] == '\r' && index + 1 < source.Length && source[index + 1] == '\n'
            ? "\r\n"
            : source[index].ToString();
    }

    private static List<Section> ReadSections(string source)
    {
        var headings = ReadHeadings(source);
        var rootHeading = headings.FirstOrDefault() is { Level: 1 } first ? first : null;
        var root = new Section("root", null, rootHeading?.Title ?? "Vision", 0, source.Length,
            rootHeading?.Level ?? 0, rootHeading?.Start ?? -1, rootHeading?.End ?? -1);
        var sections = new List<Section> { root };
        var parents = new Stack<Section>();
        parents.Push(root);
        foreach (var heading in headings)
        {
            if (ReferenceEquals(heading, rootHeading)) continue;
            sections[^1] = sections[^1] with { End = heading.Start };
            while (parents.Count > 1 && parents.Peek().Level >= heading.Level) parents.Pop();
            var id = "node-" + heading.Start.ToString("x", CultureInfo.InvariantCulture);
            var section = new Section(id, parents.Peek().Id, heading.Title, heading.Start,
                source.Length, heading.Level, heading.Start, heading.End);
            sections.Add(section);
            parents.Push(section);
        }
        return sections;
    }

    private static List<Heading> ReadHeadings(string source)
    {
        var headings = new List<Heading>();
        foreach (var heading in Markdown.Parse(source).OfType<HeadingBlock>())
        {
            var start = heading.Span.Start;
            while (start > 0 && source[start - 1] is not ('\r' or '\n')) start--;
            var title = new StringBuilder();
            if (heading.Inline is not null) AppendTitle(heading.Inline, title);
            headings.Add(new Heading(start, heading.Span.End, heading.Level, title.ToString().Trim()));
        }
        return headings;
    }

    private static void AppendTitle(ContainerInline container, StringBuilder title)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: title.Append(literal.Content); break;
                case CodeInline code: title.Append(code.Content); break;
                case AutolinkInline link: title.Append(link.Url); break;
                case LineBreakInline: title.Append(' '); break;
                case ContainerInline child: AppendTitle(child, title); break;
            }
        }
    }

    private static string Hash(string source) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

    private sealed record Heading(int Start, int End, int Level, string Title);
    private sealed record Section(
        string Id, string? ParentId, string Title, int Start, int End, int Level, int HeadingStart, int HeadingEnd);
    private sealed record Replacement(int Start, int Length, string Text);
    private sealed record ExpectedHeading(
        int Start, int Order, int Level, string Title, Replacement? Insertion = null, int WithinInsertion = 0);
}
