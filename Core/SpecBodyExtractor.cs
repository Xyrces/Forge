using System.Text.RegularExpressions;

namespace PortHorizon.Agents.Specs;

/// <summary>
/// Hand-written markdown-subset parser that extracts the
/// derived tables (diagrams, touches, deps) from a spec body.
/// NOT a general markdown library: we only handle the
/// ## sections and ```mermaid``` blocks we defined in the
/// spec template. The body remains portable markdown.
///
/// <para>
/// The extraction is deterministic and pure. The persistence
/// layer (SpecStore.ExtractAndPersistAsync) calls this on
/// every body update and writes the result to the derived
/// tables inside the same transaction.
/// </para>
/// </summary>
public sealed class SpecBodyExtractor
{
    private static readonly Regex HeadingRegex = new(
        @"^##\s+(?<title>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex FencedCodeRegex = new(
        @"```(?<lang>[A-Za-z+#]*)\r?\n(?<body>.*?)\r?\n?```",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex MermaidKindRegex = new(
        @"^(?<kind>sequenceDiagram|flowchart|graph|classDiagram|stateDiagram|erDiagram|journey|gantt|pie|gitGraph)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// One bullet point under <c>## Touches</c>. We treat each
    /// non-empty list item as a single module id. Sub-bullets
    /// (indented under a module) become the <c>rationale</c>
    /// rather than a separate touch row.
    /// </summary>
    public sealed record TouchEntry(string ModuleId, string? Rationale);

    /// <summary>
    /// One dependency edge from the <c>## Dependencies</c> section.
    /// Each line is one edge; the syntax is:
    /// <c>- {kind} &lt;target-spec-id&gt; — rationale</c>.
    /// The &lt;kind&gt; is one of blocks | depends_on | related.
    /// </summary>
    public sealed record DepEntry(string Kind, string TargetSpecId, string? Rationale);

    /// <summary>
    /// One Mermaid block from the spec body, in order.
    /// </summary>
    public sealed record DiagramEntry(int Ordinal, string Kind, string Source, string? Title);

    public sealed record ExtractedBody(
        IReadOnlyList<DiagramEntry> Diagrams,
        IReadOnlyList<TouchEntry> Touches,
        IReadOnlyList<DepEntry> Deps,
        IReadOnlyList<string> BulletSections);

    public ExtractedBody Extract(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return new ExtractedBody(
                Array.Empty<DiagramEntry>(),
                Array.Empty<TouchEntry>(),
                Array.Empty<DepEntry>(),
                Array.Empty<string>());

        var sections = SplitSections(body);
        var diagrams = new List<DiagramEntry>();
        var touches = new List<TouchEntry>();
        var deps = new List<DepEntry>();
        var ordinal = 0;
        string? currentTitle = null;

        foreach (var (title, content) in sections)
        {
            currentTitle = title;
            if (string.Equals(title, "Touches", StringComparison.OrdinalIgnoreCase))
            {
                touches.AddRange(ParseTouches(content));
                continue;
            }
            if (string.Equals(title, "Dependencies", StringComparison.OrdinalIgnoreCase))
            {
                deps.AddRange(ParseDeps(content));
                continue;
            }
            if (string.Equals(title, "Diagrams", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var d in ParseDiagrams(content, ordinal, currentTitle))
                {
                    diagrams.Add(d);
                    ordinal++;
                }
                continue;
            }
            // Other sections: also scan for orphan ```mermaid blocks
            // so a body that puts diagrams outside the ## Diagrams
            // section still extracts them. The ## Diagrams section
            // is the recommended home; this is the safety net.
            foreach (var d in ParseDiagrams(content, ordinal, currentTitle))
            {
                diagrams.Add(d);
                ordinal++;
            }
        }
        return new ExtractedBody(diagrams, touches, deps, sections.Select(s => s.Title).ToList());
    }

    /// <summary>
    /// Split a body into <c>## section title</c> chunks. Sections
    /// before the first <c>##</c> heading (the preamble) are
    /// emitted as a synthetic section with title <c>""</c> so the
    /// orphan Mermaid scan still sees them.
    /// </summary>
    internal static IReadOnlyList<(string Title, string Content)> SplitSections(string body)
    {
        var matches = HeadingRegex.Matches(body);
        if (matches.Count == 0)
            return new[] { ("", body) };

        var sections = new List<(string, string)>();
        if (matches[0].Index > 0)
            sections.Add(("", body[..matches[0].Index]));

        for (var i = 0; i < matches.Count; i++)
        {
            var title = matches[i].Groups["title"].Value.Trim();
            var contentStart = matches[i].Index + matches[i].Length;
            var contentEnd = i + 1 < matches.Count ? matches[i + 1].Index : body.Length;
            var content = body[contentStart..contentEnd];
            sections.Add((title, content));
        }
        return sections;
    }

    /// <summary>
    /// Parse a list of <c>- module-id</c> bullets. Sub-bullets
    /// (4-space indent) attach to the previous module as the
    /// rationale.
/// </summary>
    internal static IEnumerable<TouchEntry> ParseTouches(string content)
    {
        var result = new List<TouchEntry>();
        int currentIndex = -1;
        foreach (var raw in SplitLines(content))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith("    - ") || line.StartsWith("\t- "))
            {
                var rationale = line.TrimStart(' ', '\t').TrimStart('-').Trim();
                if (currentIndex >= 0)
                {
                    var existing = result[currentIndex];
                    result[currentIndex] = existing with
                    {
                        Rationale = JoinRationale(existing.Rationale, rationale)
                    };
                }
                continue;
            }
            if (line.StartsWith("- "))
            {
                var id = line[2..].Trim();
                if (id.Length > 0)
                {
                    result.Add(new TouchEntry(id, null));
                    currentIndex = result.Count - 1;
                }
                continue;
            }
        }
        return result;
    }

    /// <summary>
    /// Parse a list of <c>- {kind} &lt;spec-id&gt; — rationale</c> lines.
    /// </summary>
    internal static IEnumerable<DepEntry> ParseDeps(string content)
    {
        foreach (var raw in SplitLines(content))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (!line.StartsWith("- ")) continue;
            var body = line[2..].Trim();
            // Expected format: "<kind> <spec-id> [— rationale]".
            // kind is the first whitespace-separated token.
            var firstSpace = body.IndexOf(' ');
            if (firstSpace <= 0) continue;
            var kind = body[..firstSpace];
            var rest = body[(firstSpace + 1)..].Trim();
            // The kind must be one of the known values.
            if (!IsKnownDepKind(kind)) continue;
            // The target spec id is the next whitespace-separated token
            // (or end of string). Rationale follows an em-dash
            // separator.
            var targetEnd = rest.IndexOf(' ');
            string targetId;
            string? rationale;
            if (targetEnd < 0)
            {
                targetId = rest;
                rationale = null;
            }
            else
            {
                targetId = rest[..targetEnd];
                var after = rest[(targetEnd + 1)..].TrimStart();
                rationale = after.StartsWith("— ")
                    ? after[2..].Trim()
                    : after.StartsWith("- ")
                        ? after[2..].Trim()
                        : null;
            }
            if (targetId.Length > 0)
                yield return new DepEntry(kind, targetId, rationale);
        }
    }

    private static bool IsKnownDepKind(string kind) =>
        kind is "blocks" or "depends_on" or "related";

    internal static IEnumerable<DiagramEntry> ParseDiagrams(string content, int startOrdinal, string? title)
    {
        foreach (Match m in FencedCodeRegex.Matches(content))
        {
            var lang = m.Groups["lang"].Value.Trim();
            if (!IsMermaidLang(lang)) continue;
            var source = m.Groups["body"].Value;
            var kind = DetectKind(source);
            yield return new DiagramEntry(startOrdinal++, kind, source, title);
        }
    }

    private static bool IsMermaidLang(string lang) =>
        lang.Equals("mermaid", StringComparison.OrdinalIgnoreCase) ||
        lang.Equals("mmd", StringComparison.OrdinalIgnoreCase);

    private static string DetectKind(string source)
    {
        // The first non-empty, non-comment line names the diagram type.
        foreach (var line in SplitLines(source))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("%%")) continue;
            var m = MermaidKindRegex.Match(trimmed);
            if (m.Success) return m.Groups["kind"].Value.ToLowerInvariant();
            return "other";
        }
        return "other";
    }

    private static IEnumerable<string> SplitLines(string s)
    {
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\n')
            {
                yield return s[start..i].TrimEnd('\r');
                start = i + 1;
            }
        }
        if (start < s.Length)
            yield return s[start..].TrimEnd('\r');
    }

    private static string? JoinRationale(string? existing, string addition)
    {
        if (string.IsNullOrEmpty(existing)) return addition;
        return existing + "; " + addition;
    }
}