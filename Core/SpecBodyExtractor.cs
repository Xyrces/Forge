using System.Text;
using System.Text.RegularExpressions;
using Forge.Core;

namespace Forge.Specs;

/// <summary>
/// Pure-function pipeline that extracts structured information
/// from a spec body. Used by:
///
/// <list type="bullet">
///   <item><see cref="Orchestrator.DesignHygieneChecker"/> —
///   mermaid diagrams, Touches module list, deps graph. P2.a.</item>
///   <item>P5.3 — the spec body split: header (always inlined into
///   prompts) + per-artifact bodies (read on demand via
///   <c>read_artifact</c>). The marker syntax
///   <c>&lt;!-- artifact:kind:title --&gt;</c>...</item>
/// </list>
///
/// <para>
/// Hand-written markdown subset parser. NOT a general markdown
/// lib — we only handle the <c>##</c> sections we care about + the
/// <c>```mermaid</c> blocks. Intentional: we want the body to remain
/// portable markdown, not the C# extractor dictating body shape.
/// </para>
/// </summary>
public sealed class SpecBodyExtractor
{
    // Mermaid-fence regex: matches ```mermaid ... ``` blocks.
    // We capture the body + the kind (sequenceDiagram, flowchart,
    // classDiagram, etc.) so the dashboard can render each
    // appropriately.
    private static readonly Regex MermaidFence = new(
        @"```mermaid\s*\n(?<kind>\w+)?[^\n]*\n(?<body>.*?)```",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // Mermaid-kind detection: line 1 of a mermaid block starts
    // with the kind token (sequenceDiagram, flowchart, etc.).
    private static readonly Regex MermaidKind = new(
        @"^\s*(?<kind>\w+)\b",
        RegexOptions.Compiled);

    // Section regex: matches a ## heading line. Captures the
    // heading text. Sections are parsed in order; content is
    // everything after the heading line until the next ## (or
    // end-of-document).
    private static readonly Regex SectionHeading = new(
        @"^##\s+(?<title>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // P5.3 — artifact marker. Each marker is the start of an
    // artifact block; the block extends to the next marker (or
    // end-of-document).
    private static readonly Regex ArtifactMarker = new(
        @"<!--\s*artifact\s*:\s*(?<kind>[\w-]+)\s*:\s*(?<title>[^\r\n]+?)\s*-->",
        RegexOptions.Compiled);

    // Touches bullet: a leading dash + module id. Sub-bullets
    // (indented by 4+ spaces + dash) attach as rationale to
    // the parent module.
    private static readonly Regex TouchesBullet = new(
        @"^[\s-]+(?<mod>[\w.]+)(?:\s*[—\-]\s*(?<why>.+?))?\s*$",
        RegexOptions.Compiled);

    // Deps bullet: <kind> <spec-id> [— rationale]. Kinds are
    // 'blocks', 'depends_on', 'related'. Unknown kinds are
    // dropped (DesignHygieneChecker flags them separately).
    private static readonly Regex DepsBullet = new(
        @"^[\s-]+(?<kind>\w+)\s+(?<target>[\w\-]+)(?:\s*[—\-]\s*(?<why>.+?))?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// The structured extraction returned by <see cref="Extract"/>.
    /// Diagrams: mermaid blocks in document order. Touches: a
    /// per-module list (with optional sub-bullet rationale).
    /// Deps: dep-graph edges (kind + target + optional
    /// rationale).
    /// </summary>
    public sealed record SpecExtraction(
        IReadOnlyList<MermaidBlock> Diagrams,
        IReadOnlyList<TouchEntry> Touches,
        IReadOnlyList<DepEntry> Deps);

    public sealed record MermaidBlock(int Ordinal, string Kind, string Source, string? Title);
    public sealed record TouchEntry(string ModuleId, string? Rationale);
    public sealed record DepEntry(string Kind, string TargetSpecId, string? Rationale);
    public sealed record Section(string Title, string Content);

    /// <summary>
    /// Extract the structured fields (diagrams / touches / deps)
    /// from the spec body. Null or empty body yields an empty
    /// extraction (no findings, no findings rules fired).
    /// </summary>
    public SpecExtraction Extract(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return new SpecExtraction(
                Array.Empty<MermaidBlock>(),
                Array.Empty<TouchEntry>(),
                Array.Empty<DepEntry>());
        }

        var sections = SplitSections(body);

        var diagrams = ExtractDiagrams(body, sections);
        var touches = ExtractTouches(sections);
        var deps = ExtractDeps(sections);

        return new SpecExtraction(diagrams, touches, deps);
    }

    /// <summary>
    /// Split the body into <c>## Heading</c> sections. The
    /// preamble (any prose before the first <c>##</c> heading)
    /// is returned as a section with an empty title.
    /// </summary>
    public static IReadOnlyList<Section> SplitSections(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return Array.Empty<Section>();
        }

        var result = new List<Section>();
        var matches = SectionHeading.Matches(body).ToList();
        if (matches.Count == 0)
        {
            // No headings: the entire body is the preamble.
            return new List<Section> { new Section("", body.Trim()) };
        }

        // Preamble.
        if (matches[0].Index > 0)
        {
            var pre = body.Substring(0, matches[0].Index).Trim();
            if (pre.Length > 0)
            {
                result.Add(new Section("", pre));
            }
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var title = m.Groups["title"].Value.Trim();
            var start = m.Index + m.Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : body.Length;
            var content = body.Substring(start, end - start).Trim();
            result.Add(new Section(title, content));
        }
        return result;
    }

    // -- P5.3: spec body split for read_artifact --
    // --
    // The post-processor extracts every block prefixed by
    // '<!-- artifact:kind:title -->' into a new design_artifact
    // row, and replaces the marker in the header with a
    // '[read_artifact design-{id}]' placeholder. The next MAF
    // agent sees the slim header (the index) and calls
    // read_artifact only for the bodies it actually needs.

    /// <summary>
    /// Result of a post-process pass. <see cref="NewArtifacts"/>
    /// is the list of new design_artifact rows to insert;
    /// <see cref="Header"/> is the body with the markers
    /// replaced by [read_artifact design-{id}] placeholders.
    /// </summary>
    public sealed record ExtractedBody(
        string Header,
        IReadOnlyList<NewArtifact> NewArtifacts);

    /// <summary>
    /// A new design_artifact row produced by the post-processor.
    /// The spec store's caller persists these via
    /// <see cref="DesignArtifactStore.CreateAsync"/>.
    /// </summary>
    public sealed record NewArtifact(
        string Id,
        string Kind,
        string Title,
        string Body,
        string MarkerText);

    public ExtractedBody ExtractForReadArtifact(string specId, int currentVersion, string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return new ExtractedBody(body ?? string.Empty, Array.Empty<NewArtifact>());
        }
        var matches = ArtifactMarker.Matches(body);
        if (matches.Count == 0)
        {
            return new ExtractedBody(body, Array.Empty<NewArtifact>());
        }

        var newArtifacts = new List<NewArtifact>();
        var header = new StringBuilder();
        var cursor = 0;

        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            if (m.Index > cursor)
            {
                header.Append(body, cursor, m.Index - cursor);
            }
            var blockStart = m.Index + m.Length;
            var blockEnd = i + 1 < matches.Count ? matches[i + 1].Index : body.Length;
            var blockRaw = body.Substring(blockStart, blockEnd - blockStart);
            var blockTrimmed = blockRaw.Trim('\r', '\n', ' ', '\t');
            var kindRaw = m.Groups["kind"].Value;
            var title = m.Groups["title"].Value.Trim();
            // Empty block: skip the artifact insert but still
            // replace the marker with a placeholder so the
            // header has consistent structure. The DesignHygiene
            // Checker (P5.4) reports empty blocks as a warning.
            if (string.IsNullOrEmpty(blockTrimmed))
            {
                header.Append($"[read_artifact empty-{i + 1}]");
                cursor = blockEnd;
                continue;
            }
            TryNormalizeKind(kindRaw, out var kind);
            var id = $"design-{SanitizeForId(specId)}-{currentVersion}-{i + 1}";
            newArtifacts.Add(new NewArtifact(
                Id: id,
                Kind: KindToStringValue(kind),
                Title: title,
                Body: blockTrimmed,
                MarkerText: m.Value));
            header.Append($"[read_artifact {id}]");
            cursor = blockEnd;
        }
        if (cursor < body.Length)
        {
            header.Append(body, cursor, body.Length - cursor);
        }
        return new ExtractedBody(header.ToString(), newArtifacts);
    }

    // -- private helpers --

    private static IReadOnlyList<MermaidBlock> ExtractDiagrams(
        string? body, IReadOnlyList<Section> sections)
    {
        var result = new List<MermaidBlock>();
        if (string.IsNullOrEmpty(body)) return result;
        // Find each mermaid fence in document order. Use the
        // source-position to look up the section title (## Diagrams
        // vs orphan). Orphans are picked up via a fallback walk
        // over all matches.
        var allFences = MermaidFence.Matches(body);
        for (var i = 0; i < allFences.Count; i++)
        {
            var m = allFences[i];
            var source = m.Groups["body"].Value.TrimEnd();
            var kindLine = m.Groups["kind"].Success ? m.Groups["kind"].Value : "";
            var kind = InferKind(kindLine, source);
            // Find the containing section title: the most recent
            // ## heading before m.Index.
            string? title = null;
            for (var s = sections.Count - 1; s >= 0; s--)
            {
                if (sections[s].Title.Length == 0) continue;
                // The section's content is its body; we don't have
                // a per-section offset here, but we can find the
                // heading by string-search. Faster: precompute
                // heading offsets in SplitSections. For the
                // current data sizes (typical spec body < 20K
                // tokens) this is fine.
                if (body.IndexOf("## " + sections[s].Title, StringComparison.Ordinal) <= m.Index)
                {
                    title = sections[s].Title;
                    break;
                }
            }
            result.Add(new MermaidBlock(i, kind, source, title));
        }
        return result;
    }

    private static string InferKind(string kindLine, string source)
    {
        if (!string.IsNullOrEmpty(kindLine))
        {
            return kindLine.ToLowerInvariant();
        }
        // Fallback: parse the first non-empty line of the source.
        var first = source.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (first is not null)
        {
            var m = MermaidKind.Match(first);
            if (m.Success) return m.Groups["kind"].Value.ToLowerInvariant();
        }
        return "unknown";
    }

    private static IReadOnlyList<TouchEntry> ExtractTouches(IReadOnlyList<Section> sections)
    {
        var result = new List<TouchEntry>();
        var matching = sections.Where(s => s.Title.Equals("Touches", StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 0) return result;
        var sectionContent = string.Join("\n", matching.Select(s => s.Content));
        // Walk lines; sub-bullets attach as rationale to the
        // most recent top-level bullet. Sub-bullet is a line
        // starting with 4+ spaces + dash.
        TouchEntry? current = null;
        foreach (var raw in sectionContent.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var top = TouchesBullet.Match(line);
            if (top.Success && (line.StartsWith("-") || line.StartsWith(" *")))
            {
                var mod = top.Groups["mod"].Value;
                var why = top.Groups["why"].Success ? top.Groups["why"].Value : null;
                current = new TouchEntry(mod, string.IsNullOrEmpty(why) ? null : why);
                result.Add(current);
            }
            else if (line.StartsWith("    -") || line.StartsWith("   -") || line.StartsWith("  -"))
            {
                // Sub-bullet: append as rationale to current.
                if (current is not null)
                {
                    var sub = line.TrimStart().TrimStart('-').Trim();
                    current = current with { Rationale = current.Rationale is null
                        ? sub
                        : current.Rationale + "; " + sub };
                    // Replace the last entry in the result list.
                    result[result.Count - 1] = current;
                }
            }
        }
        return result;
    }

    private static IReadOnlyList<DepEntry> ExtractDeps(IReadOnlyList<Section> sections)
    {
        var result = new List<DepEntry>();
        var matching = sections.Where(s => s.Title.Equals("Dependencies", StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 0) return result;
        var sectionContent = string.Join("\n", matching.Select(s => s.Content));
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocks", "depends_on", "related" };
        foreach (var raw in sectionContent.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("-") && !line.StartsWith(" *")) continue;
            var m = DepsBullet.Match(line);
            if (!m.Success) continue;
            var kind = m.Groups["kind"].Value.ToLowerInvariant();
            if (!valid.Contains(kind)) continue;
            var target = m.Groups["target"].Value;
            var why = m.Groups["why"].Success ? m.Groups["why"].Value : null;
            result.Add(new DepEntry(kind, target, string.IsNullOrEmpty(why) ? null : why));
        }
        return result;
    }

    private static bool TryNormalizeKind(string raw, out DesignArtifactKind kind)
    {
        var span = raw.AsSpan().Trim();
        if (span.Equals("wireframe", StringComparison.OrdinalIgnoreCase)) { kind = DesignArtifactKind.Wireframe; return true; }
        if (span.Equals("mockup", StringComparison.OrdinalIgnoreCase)) { kind = DesignArtifactKind.Mockup; return true; }
        if (span.Equals("component-spec", StringComparison.OrdinalIgnoreCase)
            || span.Equals("componentspec", StringComparison.OrdinalIgnoreCase))
        { kind = DesignArtifactKind.ComponentSpec; return true; }
        if (span.Equals("visual-rule", StringComparison.OrdinalIgnoreCase)
            || span.Equals("visualrule", StringComparison.OrdinalIgnoreCase))
        { kind = DesignArtifactKind.VisualRule; return true; }
        // Unknown kinds map to ComponentSpec as a safe default;
        // the DesignHygieneChecker (P5.4) flags the original
        // kind string so the operator can see "this marker said
        // 'weird-kind' but we treated it as ComponentSpec".
        kind = DesignArtifactKind.ComponentSpec;
        return false;
    }

    private static string KindToStringValue(DesignArtifactKind k) => k switch
    {
        DesignArtifactKind.Wireframe => "wireframe",
        DesignArtifactKind.Mockup => "mockup",
        DesignArtifactKind.ComponentSpec => "component-spec",
        DesignArtifactKind.VisualRule => "visual-rule",
        _ => "component-spec",
    };

    private static string SanitizeForId(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
        }
        var truncated = sb.ToString().Trim('-');
        return truncated.Length > 32 ? truncated[..32].TrimEnd('-') : truncated;
    }
}
