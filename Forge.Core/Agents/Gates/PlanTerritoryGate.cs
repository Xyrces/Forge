using System.Text.RegularExpressions;

namespace Forge.Agents.Gates;

/// <summary>
/// Deterministic territory + file-existence gate: every file path
/// named in the plan must (a) fall inside the role's territory
/// (prefix set + optional root-file allowance) and (b) exist under
/// the worktree, unless explicitly marked "(new)". Zero LLM.
/// </summary>
public sealed partial class PlanTerritoryGate : IRunGate
{
    public const string GateName = "plan-territory";
    public string Name => GateName;

    /// <summary>One-line description for the catalog UI.</summary>
    public const string DescriptionText =
        "Verifies every file path in the plan is inside the role's territory and exists in the worktree (or is marked (new)).";
    public string Description => DescriptionText;
    public GateKind Kind => GateKind.Deterministic;

    public Task<RunGateVerdict> EvaluateAsync(RunGateContext ctx)
    {
        if (ctx.TerritoryPrefixes.Count == 0 && !ctx.TerritoryAllowsRootFiles)
        {
            return Task.FromResult(RunGateVerdict.Approved);   // role has no territory constraint
        }
        var problems = new List<string>();
        foreach (var raw in ExtractPaths(ctx.Plan))
        {
            var (path, isNew) = raw;
            if (!InTerritory(path, ctx))
            {
                problems.Add($"{path} is outside {ctx.RoleName}'s territory " +
                    $"({string.Join(", ", ctx.TerritoryPrefixes)}{(ctx.TerritoryAllowsRootFiles ? ", repo-root files" : "")})");
                continue;
            }
            if (!isNew && !File.Exists(Path.Combine(ctx.WorktreePath, path)))
            {
                problems.Add($"{path} does not exist in the worktree — if you intend to create it, mark it \"(new)\"");
            }
        }
        if (problems.Count > 0)
        {
            return Task.FromResult(new RunGateVerdict(GateOutcome.Revise,
                "Plan territory/existence violations:\n- " + string.Join("\n- ", problems) +
                "\n(Paths must be REPO-RELATIVE like `Dashboard/Foo.cs` — never absolute filesystem paths; mark intentional creations with '(new)'.)"));
        }
        return Task.FromResult(RunGateVerdict.Approved);
    }

    private static bool InTerritory(string path, RunGateContext ctx)
    {
        foreach (var prefix in ctx.TerritoryPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return ctx.TerritoryAllowsRootFiles && !path.Contains('/');
    }

    /// <summary>Extract candidate repo-relative file paths from the
    /// plan's <c>## Files</c> section ONLY. Prose in Goal/Approach
    /// sections mentions bare filenames naturally (observed live
    /// 2026-07-26: task-196 burned all 3 revisions on "RunGate.cs
    /// does not exist" while its Files section had full paths) —
    /// the Files section is what the schema gate mandates for
    /// exactly this purpose. A trailing "(new)" marks intentional
    /// creation. Bare filenames that duplicate a full-path entry
    /// are skipped to avoid false violations.</summary>
    internal static IEnumerable<(string Path, bool IsNew)> ExtractPaths(string plan)
    {
        var rawPaths = new List<(string Path, bool IsNew)>();
        var inFiles = false;
        foreach (var line in plan.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
            {
                inFiles = trimmed.TrimStart('#', ' ').StartsWith("files", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inFiles) continue;
            foreach (Match m in PathRegex().Matches(line))
            {
                // Glob mentions (`Data/Ships/*.ship.json`, `**/*.json`)
                // are prose about data, not file entries — the match
                // starting right after a '*' would extract a phantom
                // bare filename ("ship.json") the model can't fix
                // (observed live 2026-07-29: task-13 rejected 3x).
                if (m.Index > 0 && line[m.Index - 1] == '*') continue;
                var p = m.Value.Trim('`', '\'', '"', ' ', ',', ';', '(', ')');
                if (p.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                p = p.TrimStart('.', '/');
                if (p.Length == 0 || p.StartsWith("bin/") || p.StartsWith("obj/")) continue;
                var isNew = line.Contains("(new)", StringComparison.OrdinalIgnoreCase);
                rawPaths.Add((p, isNew));
            }
        }

        // Build set of filenames from full-paths for dedup
        var fullPathFilenames = new HashSet<string>(
            rawPaths.Where(rp => rp.Path.Contains('/'))
                    .Select(rp => System.IO.Path.GetFileName(rp.Path)),
            StringComparer.OrdinalIgnoreCase);

        // Yield, skipping bare filenames that duplicate a full-path entry
        foreach (var (path, isNew) in rawPaths)
        {
            if (!path.Contains('/') && fullPathFilenames.Contains(path))
                continue;
            yield return (path, isNew);
        }
    }


    [GeneratedRegex(@"[\w./-]+\.(?:cs|razor|css|json|md|csproj|sln|ya?ml|sh|sql)\b")]
    private static partial Regex PathRegex();
}
