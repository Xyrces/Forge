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
    public GateKind Kind => GateKind.Deterministic;
    public string Description => DescriptionText;

    /// <summary>User-facing description of this gate for the catalog.</summary>
    public const string DescriptionText =
        "Verifies every file path in the plan is inside the role's territory and exists in the worktree (or is marked as new).";

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
                "Plan territory/existence violations:\n- " + string.Join("\n- ", problems)));
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
    /// plan text. Paths are code-formatted tokens with a known source
    /// extension; a trailing "(new)" marks intentional creation.</summary>
    internal static IEnumerable<(string Path, bool IsNew)> ExtractPaths(string plan)
    {
        foreach (var line in plan.Split('\n'))
        {
            foreach (Match m in PathRegex().Matches(line))
            {
                var p = m.Value.Trim('`', '\'', '"', ' ', ',', ';', '(', ')');
                if (p.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                p = p.TrimStart('.', '/');
                if (p.Length == 0 || p.StartsWith("bin/") || p.StartsWith("obj/")) continue;
                var isNew = line.Contains("(new)", StringComparison.OrdinalIgnoreCase);
                yield return (p, isNew);
            }
        }
    }

    [GeneratedRegex(@"[\w./-]+\.(?:cs|razor|css|json|md|csproj|sln|ya?ml|sh|sql)\b")]
    private static partial Regex PathRegex();
}
