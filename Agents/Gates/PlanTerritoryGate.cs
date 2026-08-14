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
        // Territory is enforced only when the project configured it
        // (roles_json $territory — ResolveTerritory no longer falls
        // back to the Forge-shaped registry list). The existence
        // check runs ALWAYS: it catches hallucinated edits against
        // existing areas regardless of territory config.
        var territoryEnforced = ctx.TerritoryPrefixes.Count > 0 || ctx.TerritoryAllowsRootFiles;
        var problems = new List<string>();
        foreach (var raw in ExtractPaths(ctx.Plan))
        {
            var (path, isNew) = raw;
            if (territoryEnforced && !InTerritory(path, ctx))
            {
                problems.Add($"{path} is outside {ctx.RoleName}'s territory " +
                    $"({string.Join(", ", ctx.TerritoryPrefixes)}{(ctx.TerritoryAllowsRootFiles ? ", repo-root files" : "")})" +
                    $"{SuggestExistingPath(ctx.WorktreePath, path)}");
                continue;
            }
            if (!isNew && !File.Exists(Path.Combine(ctx.WorktreePath, path)))
            {
                // A missing file inside a MISSING directory tree is an
                // intentional new scaffold (new test project, new docs
                // set) — not a hallucinated edit. The existence check's
                // anti-hallucination value is catching typos against
                // EXISTING areas (task-196's RunGate.cs sat in an
                // existing dir). Observed live 2026-08-11: talaria
                // task-12 burned 3 runs x 3 revisions because its
                // brand-new tests/Talaria.Ci.Tests/ tree didn't exist.
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)
                    && !Directory.Exists(Path.Combine(ctx.WorktreePath, dir)))
                {
                    continue;
                }
                problems.Add($"{path} does not exist in the worktree — if you intend to create it, mark it \"(new)\" (or start the line with Create/Add){SuggestExistingPath(ctx.WorktreePath, path)}");
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

    /// <summary>
    /// When a bare filename fails existence, look for it elsewhere in
    /// the worktree and suggest the real repo-relative path — "did you
    /// mean src/X/Y.csproj?" turns a two-revision stall into a one-line
    /// fix (observed live 2026-08-12: talaria task-26 burned both
    /// revisions listing the csproj bare instead of at src/…). Returns
    /// "" when there is nothing useful to suggest.
    /// </summary>
    internal static string SuggestExistingPath(string worktree, string path)
    {
        if (path.Contains('/')) return "";          // full path: nothing to suggest
        if (!Directory.Exists(worktree)) return "";
        try
        {
            var matches = new List<string>(3);
            FindByName(worktree, worktree, path, matches);
            return matches.Count == 0 ? "" : $" — did you mean {string.Join(" or ", matches)}?";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";   // suggestion is best-effort; never fail the gate over it
        }
    }

    private static readonly string[] SkippedDirs = { ".git", "bin", "obj", "node_modules", ".forge" };

    private static void FindByName(string root, string dir, string fileName, List<string> matches)
    {
        if (matches.Count >= 3) return;
        foreach (var file in Directory.EnumerateFiles(dir, fileName))
        {
            matches.Add(Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'));
            if (matches.Count >= 3) return;
        }
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (SkippedDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            FindByName(root, sub, fileName, matches);
            if (matches.Count >= 3) return;
        }
    }

    /// <summary>
    /// A Files-section line marks intentional creation when it carries
    /// a parenthetical marker — (new), (new file), (create…), (add…) —
    /// or starts with a creation verb (Create/Add/New …). The literal
    /// "(new)"-only rule burned plan revisions on phrasing instead of
    /// substance (observed live 2026-08-11: talaria task-12 rejected
    /// 3x for listing 8 files-to-create without the exact marker).
    /// </summary>
    internal static bool IsCreationLine(string line)
    {
        if (line.Contains("(new", StringComparison.OrdinalIgnoreCase)
            || line.Contains("(creat", StringComparison.OrdinalIgnoreCase)
            || line.Contains("(add", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var t = line.TrimStart('-', '*', '+', ' ', '`');
        return t.StartsWith("create ", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("add ", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("new ", StringComparison.OrdinalIgnoreCase);
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
                // Normalize "./path" → "path" and strip leading slashes,
                // but NEVER strip a bare leading dot: dot-prefixed repo
                // paths (.github/, .kilo/) are legitimate territory
                // prefixes, and TrimStart('.') mangles them into
                // "github/..." which then fails the territory check
                // (observed live 2026-08-11: talaria task-12 burned both
                // plan revisions on phantom territory violations).
                if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
                p = p.TrimStart('/');
                if (p.Length == 0 || p.StartsWith("bin/") || p.StartsWith("obj/")) continue;
                var isNew = IsCreationLine(line);
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
