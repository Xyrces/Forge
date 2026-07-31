using System.Text.Json;
using Forge.Codebase;
using Forge.Core;
using Forge.Specs;

namespace Forge.Orchestrator;

/// <summary>
/// Severity of a single hygiene finding. <see cref="Error"/> findings
/// block the Designer from running the LLM; <see cref="Warning"/>
/// findings are recorded but the LLM still runs (the operator
/// sees the warnings in the design_artifact hygiene_report).
/// </summary>
public enum HygieneSeverity { Warning, Error }

public sealed record HygieneFinding(
    string Rule,
    HygieneSeverity Severity,
    string Message,
    string? FixSuggestion);

public sealed record HygieneReport(
    bool Passed,
    IReadOnlyList<HygieneFinding> Findings)
{
    public string ToJson() => JsonSerializer.Serialize(this, DesignerHygieneJsonContext.Default.HygieneReport);
}

/// <summary>
/// Deterministic, non-LLM spec hygiene check. Runs BEFORE the
/// Designer's LLM step. If the report's <see cref="HygieneReport.Passed"/>
/// is false, the Designer run is marked <c>hygiene_failed</c> and
/// the LLM is NOT called — the operator sees the report on the
/// Design tab and decides what to do (rewrite, mark
/// NeedsRevision, or skip design).
///
/// <para>
/// Rules are listed below. New rules register as methods on
/// <see cref="DesignHygieneChecker"/> + entries in
/// <see cref="CheckAsync"/>'s rule list. Each rule produces zero or
/// one <see cref="HygieneFinding"/>. Rules are pure (no LLM, no IO
/// beyond reading the spec body + the codebase graph).
/// </para>
/// </summary>
public sealed class DesignHygieneChecker
{
    private readonly ISpecStore _specs;
    private readonly ICodebaseGraphCacheStore _graphCache;
    private readonly ICodebaseGraphBuilder _graphBuilder;
    private readonly string _workspaceRoot;
    private readonly Func<string, string?>? _projectRootLookup;

    public DesignHygieneChecker(
        ISpecStore specs,
        ICodebaseGraphCacheStore graphCache,
        ICodebaseGraphBuilder graphBuilder,
        string workspaceRoot,
        Func<string, string?>? projectRootLookup = null)
    {
        _specs = specs;
        _graphCache = graphCache;
        _graphBuilder = graphBuilder;
        _workspaceRoot = workspaceRoot;
        _projectRootLookup = projectRootLookup;
    }

    /// <summary>The codebase graph is per repo: a spec's touches are
    /// checked against the graph of the PROJECT that owns the spec,
    /// not the primary workspace (multi-project fix — a porthorizon
    /// spec's PortHorizon.Core.* touches all read as "undefined
    /// module" against Forge's graph).</summary>
    private string RootFor(SpecRecord spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.ProjectId) && _projectRootLookup is not null)
        {
            try
            {
                var root = _projectRootLookup(spec.ProjectId);
                if (!string.IsNullOrWhiteSpace(root)) return root;
            }
            catch (Exception)
            {
                // lookup failure → primary root (single-project behavior)
            }
        }
        return _workspaceRoot;
    }

    public async Task<HygieneReport> CheckAsync(SpecRecord spec, CancellationToken ct = default)
    {
        var findings = new List<HygieneFinding>();
        var extracted = new SpecBodyExtractor().Extract(spec.Body);

        // 1. status_mismatch — defensive. The Designer's other gates
        //    (the scheduler, the manual endpoint) reject specs in
        //    invalid states; this is the last line of defense.
        if (spec.Status is not (SpecStatus.ReadyForDesign or SpecStatus.NeedsRevision or SpecStatus.Draft))
        {
            findings.Add(new HygieneFinding(
                "status_mismatch", HygieneSeverity.Error,
                $"Spec is in {spec.Status}; Designer only processes ReadyForDesign / NeedsRevision / Draft.",
                "Use SpecStore.SetStatusAsync to move the spec to ReadyForDesign first."));
        }

        // 2. missing_acceptance_criteria — the spec is unsafe to
        //    commit because engineering will build *something* but
        //    not what was meant.
        var ac = FindSection(spec.Body, "Acceptance criteria");
        if (string.IsNullOrWhiteSpace(ac) || !HasBullet(ac))
        {
            findings.Add(new HygieneFinding(
                "missing_acceptance_criteria", HygieneSeverity.Error,
                "Spec has no ## Acceptance criteria section (or it's empty).",
                "Add a ## Acceptance criteria section with at least one checkboxed bullet, e.g. `- [ ] player can load a level`."));
        }

        // 3. broken_dep_chain — spec's ## Dependencies references a
        //    spec_id that doesn't exist in SpecStore.
        foreach (var dep in extracted.Deps)
        {
            if (string.IsNullOrWhiteSpace(dep.TargetSpecId)) continue;
            var target = await _specs.GetAsync(dep.TargetSpecId, ct);
            if (target is null)
            {
                findings.Add(new HygieneFinding(
                    "broken_dep_chain", HygieneSeverity.Error,
                    $"## Dependencies references spec '{dep.TargetSpecId}' ({dep.Kind}) but no such spec exists.",
                    "Fix or remove the dependency; the spec_id is wrong."));
            }
        }

        // 4. circular_dep — the spec's dep chain forms a cycle.
        //    Cycle detection: walk the dep graph starting at the
        //    spec; if we revisit the spec, there's a cycle.
        await CheckCyclesAsync(spec, extracted.Deps, findings, ct);

        // 5. touches_undefined_module — ## Touches references a
        //    module id that's not in the codebase graph.
        if (extracted.Touches.Count > 0)
        {
            var graph = await LoadGraphAsync(RootFor(spec), ct);
            if (graph is not null)
            {
                var known = graph.Files.Select(f => f.Module).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var t in extracted.Touches)
                {
                    if (string.IsNullOrWhiteSpace(t.ModuleId)) continue;
                    // Graph modules are csproj-granular; a touches entry
                    // naming a namespace INSIDE a known module (e.g.
                    // 'PortHorizon.Core.Construction' under module
                    // 'PortHorizon.Core') is more specific, not undefined.
                    if (!known.Contains(t.ModuleId)
                        && !known.Any(m => t.ModuleId.StartsWith(m + ".", StringComparison.OrdinalIgnoreCase)))
                    {
                        findings.Add(new HygieneFinding(
                            "touches_undefined_module", HygieneSeverity.Error,
                            $"## Touches references module '{t.ModuleId}' which is not in the codebase graph.",
                            "Either fix the module id or build the codebase graph (the graph may be stale)."));
                    }
                }
            }
        }

        // 6. no_touches — soft warning, not all specs touch modules
        //    (e.g. a CI build spec). Many legitimate specs have
        //    empty Touches.
        if (extracted.Touches.Count == 0)
        {
            findings.Add(new HygieneFinding(
                "no_touches", HygieneSeverity.Warning,
                "## Touches is empty. Engineering won't know which modules to update.",
                "List the modules this spec affects. If it's truly a no-touch spec (e.g. CI), ignore."));
        }

        // 7. duplicate_epic_in_active_sprint — another spec with
        //    the same Title exists in Approved | Grooming | Groomed
        //    in the same project. The Designer doesn't know if this
        //    is intentional, so it rejects.
        var allSpecs = await _specs.ListAsync(spec.ProjectId, status: null, ct);
        var dup = allSpecs
            .Where(s => s.Id != spec.Id)
            .Where(s => s.Status is SpecStatus.Approved or SpecStatus.Grooming or SpecStatus.Groomed or SpecStatus.Designed)
            .Where(s => string.Equals(s.Title.Trim(), spec.Title.Trim(), StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (dup is not null)
        {
            findings.Add(new HygieneFinding(
                "duplicate_epic_in_active_sprint", HygieneSeverity.Error,
                $"Spec '{dup.Id}' has the same title and is in {dup.Status} in this project.",
                "Rename this spec (or mark the other Superseded) — engineering would create duplicate work."));
        }

        // 8. body_too_long — operator pasted a log dump. The LLM
        //    would struggle to summarize; force a rewrite.
        if (spec.Body.Length > 50_000)
        {
            findings.Add(new HygieneFinding(
                "body_too_long", HygieneSeverity.Warning,
                $"Spec body is {spec.Body.Length:N0} chars (>50KB). This is almost always a log dump or pasted transcript.",
                "Trim to the actual spec — link out to long artifacts instead of pasting them inline."));
        }

        // 9. stale_open_questions — the spec has too many unresolved
        //    open questions. Designer can't proceed with that much
        //    uncertainty; flag it so the operator cleans up first.
        var oq = FindSection(spec.Body, "Open questions");
        var oqCount = oq is null ? 0 : CountBullets(oq);
        if (oqCount > 3)
        {
            findings.Add(new HygieneFinding(
                "stale_open_questions", HygieneSeverity.Warning,
                $"Spec has {oqCount} unresolved open questions (>3). Designer can't proceed with that much uncertainty.",
                "Resolve or move open questions to follow-up specs; leave at most 3 here."));
        }

        // 10. empty_artifact_block — the Designer emitted an
        //     <!-- artifact:kind:title --> marker with no body
        //     (the post-processor keeps the placeholder but
        //     stores no design_artifact row). The next agent
        //     would see "[read_artifact empty-N]" with nothing
        //     to fetch. Warning, not error: the Designer may be
        //     planning to fill it in next iteration.
        //
        // 11. unknown_artifact_kind — the marker kind isn't
        //     one of wireframe | mockup | component-spec |
        //     visual-rule. The post-processor stores the block
        //     as component-spec as a safe default, but the
        //     operator should know the Designer drifted.
        var markerCheck = new System.Text.RegularExpressions.Regex(
            @"<!--\s*artifact\s*:\s*([\w-]+)\s*:\s*([^\r\n]+?)\s*-->",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var validKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "wireframe", "mockup", "component-spec", "visual-rule" };

        foreach (System.Text.RegularExpressions.Match m in markerCheck.Matches(spec.Body))
        {
            var afterMarker = m.Index + m.Length;
            // The "body" of this marker is everything until the
            // next marker or end-of-document. We extract that
            // via the post-processor's already-built split; but
            // to keep this rule independent of the post-processor,
            // we look at the next ~200 chars and check whether
            // they're whitespace-only or hit another marker.
            var windowEnd = Math.Min(afterMarker + 200, spec.Body.Length);
            var window = spec.Body.Substring(afterMarker, windowEnd - afterMarker);
            // If the trimmed window is empty OR the next
            // marker appears within the window, the block is
            // empty.
            var empty = string.IsNullOrWhiteSpace(window)
                || window.TrimStart().StartsWith("<!--")
                || window.TrimStart().StartsWith("\r\n--")
                || window.TrimStart().StartsWith("\n--");
            if (empty)
            {
                findings.Add(new HygieneFinding(
                    "empty_artifact_block", HygieneSeverity.Warning,
                    $"Spec has an <!-- artifact:{m.Groups[1].Value}:{m.Groups[2].Value} --> marker with no body.",
                    "Fill the artifact body or remove the marker. The post-processor keeps the placeholder but stores no row, so the next agent sees [read_artifact empty-N] with nothing to fetch."));
            }

            var kind = m.Groups[1].Value;
            if (!validKinds.Contains(kind))
            {
                findings.Add(new HygieneFinding(
                    "unknown_artifact_kind", HygieneSeverity.Warning,
                    $"Spec has an <!-- artifact:{kind}:... --> marker with an unknown kind '{kind}'.",
                    $"Use one of: {string.Join(", ", validKinds)}. Unknown kinds are stored as component-spec as a safe default."));
            }
        }

        // Determine pass: report passes if there are no Error findings.
        var passed = !findings.Any(f => f.Severity == HygieneSeverity.Error);
        return new HygieneReport(passed, findings);
    }

    private async Task CheckCyclesAsync(
        SpecRecord spec,
        IReadOnlyList<SpecBodyExtractor.DepEntry> directDeps,
        List<HygieneFinding> findings,
        CancellationToken ct)
    {
        // BFS through the spec's transitive dep graph. If we revisit
        // the spec, there's a cycle.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { spec.Id };
        var queue = new Queue<string>();
        foreach (var d in directDeps) if (!string.IsNullOrWhiteSpace(d.TargetSpecId)) queue.Enqueue(d.TargetSpecId);

        var localVisited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0 && localVisited.Count < 100)  // cap depth
        {
            var id = queue.Dequeue();
            if (!localVisited.Add(id)) continue;
            if (visited.Contains(id))
            {
                findings.Add(new HygieneFinding(
                    "circular_dep", HygieneSeverity.Error,
                    $"Spec has a circular dependency chain back to itself (via '{id}').",
                    "Break the cycle: one of the specs in the chain should not depend on another."));
                return;
            }
            var target = await _specs.GetAsync(id, ct);
            if (target is null) continue;  // already flagged by broken_dep_chain
            var sub = new SpecBodyExtractor().Extract(target.Body);
            foreach (var d in sub.Deps)
            {
                if (!string.IsNullOrWhiteSpace(d.TargetSpecId)) queue.Enqueue(d.TargetSpecId);
            }
        }
    }

    private async Task<CodebaseGraph?> LoadGraphAsync(string repoRoot, CancellationToken ct)
    {
        var cached = await _graphCache.GetAsync(repoRoot, ct);
        if (cached is not null && File.Exists(cached.DiskPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(cached.DiskPath, ct);
                return JsonSerializer.Deserialize<CodebaseGraph>(json);
            }
            catch
            {
                // fall through to rebuild
            }
        }
        return await _graphBuilder.BuildAsync(repoRoot, cached, cacheDirectory: null, ct: ct);
    }

    private static string? FindSection(string? body, string title)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var lines = body.Split('\n');
        int? start = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("## "))
            {
                var heading = trimmed.Substring(3).Trim();
                if (start is int s && heading.Equals(title, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Join("\n", lines.Skip(s).Take(i - s));
                }
                if (heading.Equals(title, StringComparison.OrdinalIgnoreCase))
                {
                    start = i + 1;
                }
                else if (start is not null)
                {
                    // Past the section, didn't find body.
                    return string.Join("\n", lines.Skip(start.Value).Take(i - start.Value));
                }
            }
        }
        if (start is int s2) return string.Join("\n", lines.Skip(s2));
        return null;
    }

    private static bool HasBullet(string section)
    {
        foreach (var line in section.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("- ") || t.StartsWith("* ") || t.StartsWith("+ ")) return true;
        }
        return false;
    }

    private static int CountBullets(string section)
    {
        int n = 0;
        foreach (var line in section.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("- ") || t.StartsWith("* ") || t.StartsWith("+ ")) n++;
        }
        return n;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(HygieneReport))]
internal partial class DesignerHygieneJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}