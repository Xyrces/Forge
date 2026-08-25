namespace Forge.Reviewer;

/// <summary>QA applicability tier for a PR head, classified
/// deterministically from the head's diff (the agent never
/// self-declares applicability). Highest applicable tier wins on a
/// mixed diff.</summary>
public enum QaEvidenceTier
{
    /// <summary>Visual: the diff touches a visual prefix — full QA,
    /// raster PNG/JPG evidence mandatory.</summary>
    Visual = 1,

    /// <summary>Code: non-visual, non-docs paths — QA runs and plays
    /// the sim; pass requires evidence files (any type) under
    /// test-results/qa/&lt;taskId&gt;/; raster not demanded.</summary>
    Code = 2,

    /// <summary>Docs: every path is docs/config/evidence — no agent
    /// run, no attempt spent; the dispatcher stamps
    /// qaVerdict=not-applicable at the head.</summary>
    Docs = 3,
}

/// <summary>
/// Deterministic 3-tier QA applicability gate over a head's diff
/// (<c>git diff --name-only origin/&lt;default&gt;...HEAD</c>). Pure
/// function — unit-testable, recomputed per QA attempt so a head that
/// flips tier between attempts re-classifies correctly.
/// </summary>
public static class QaEvidenceTierClassifier
{
    /// <summary>Tier-3 path set: docs/, **.md, .gitignore,
    /// .gitattributes, LICENSE*, test-results/. Anything else is not
    /// docs. (.github/ workflows are deliberately NOT docs — code
    /// tier, conservative.)</summary>
    public static bool IsDocsPath(string path)
    {
        if (path.StartsWith("docs/", StringComparison.Ordinal)
            || path.StartsWith("test-results/", StringComparison.Ordinal))
            return true;
        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return true;
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name.Equals(".gitignore", StringComparison.Ordinal)
            || name.Equals(".gitattributes", StringComparison.Ordinal)
            || name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Classify a diff's path list. Empty/unclassifiable ⇒
    /// tier 2 (conservative: QA runs). Highest applicable tier wins on
    /// a mixed diff: any visual path ⇒ tier 1; otherwise all-docs ⇒
    /// tier 3; otherwise tier 2.</summary>
    public static QaEvidenceTier Classify(
        IReadOnlyList<string> paths, IReadOnlyList<string> visualPrefixes)
    {
        if (paths.Count == 0) return QaEvidenceTier.Code;
        foreach (var path in paths)
        {
            foreach (var prefix in visualPrefixes)
            {
                if (!string.IsNullOrEmpty(prefix)
                    && path.StartsWith(prefix, StringComparison.Ordinal))
                    return QaEvidenceTier.Visual;
            }
        }
        return paths.All(IsDocsPath) ? QaEvidenceTier.Docs : QaEvidenceTier.Code;
    }

    /// <summary>Metadata stamp value for a tier (task metadata
    /// <c>qaTier</c> — TaskDetail/audit).</summary>
    public static string MetadataValue(QaEvidenceTier tier) => tier switch
    {
        QaEvidenceTier.Visual => "visual",
        QaEvidenceTier.Code => "code",
        QaEvidenceTier.Docs => "docs",
        _ => "code",
    };

    /// <summary>Resolve the visual prefixes for a project: the
    /// <c>$qa.visualPaths</c> override when set (null = unset; empty =
    /// explicitly nothing visual), otherwise the project's clientdev
    /// <c>$territory</c> prefixes. Nothing configured anywhere ⇒
    /// nothing is visual (all code is tier 2) — fail-open toward LESS
    /// demand only when no visual surface is configured at all.</summary>
    public static IReadOnlyList<string> ResolveVisualPrefixes(
        IReadOnlyDictionary<string, Core.RoleTerritory>? territories,
        IReadOnlyList<string>? qaVisualPaths)
    {
        if (qaVisualPaths is not null) return qaVisualPaths;
        if (territories is not null
            && territories.TryGetValue("clientdev", out var clientdev))
            return clientdev.Prefixes;
        return Array.Empty<string>();
    }
}
