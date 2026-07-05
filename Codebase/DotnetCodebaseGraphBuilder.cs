using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Forge.Codebase;

/// <summary>
/// Builds a C# / .csproj codebase import graph for a repo root.
/// Parses:
///   - <c>.csproj</c> files for <c>&lt;ProjectReference Include="..." /&gt;</c>
///     (project -> project edges).
///   - <c>.cs</c> files for top-level <c>using Foo.Bar;</c> directives
///     (file -> project / module edges).
///
/// <para>
/// The graph is per-file but the UI can collapse to per-module by
/// joining on <see cref="FileNode.Module"/>. The spec overlay uses
/// per-class names where they're mentioned in spec bodies; this
/// builder does not yet emit class nodes.
/// </para>
///
/// <para>
/// Incremental behavior (per §4.2a / §9.5 of the workflow doc):
/// <list type="number">
///   <item>Compute current <c>git rev-parse HEAD</c>.</item>
///   <item>If matches prior cache sha → return the prior graph (no work).</item>
///   <item>Else: <c>git diff --name-only prior..HEAD</c> for changed files.
///   Re-parse only those. Remove their old edges, replace with new.</item>
///   <item>Persist new graph JSON at <c>.portHorizon/codebase-graph/&lt;sha&gt;.json</c>.</item>
///   <item>Return the merged graph.</item>
/// </list>
/// If prior cache is null, do a full walk.
/// </para>
/// </summary>
public sealed class DotnetCodebaseGraphBuilder : ICodebaseGraphBuilder
{
    private static readonly Regex UsingDirectiveRegex = new(
        @"^\s*using\s+(?<ns>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*;\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex ProjectReferenceRegex = new(
        @"<ProjectReference\s+Include\s*=\s*""(?<path>[^""]+)""\s*/?>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool SupportsLanguage(string language)
        => language.Equals("csharp", StringComparison.OrdinalIgnoreCase) ||
           language.Equals("csproj", StringComparison.OrdinalIgnoreCase) ||
           language.Equals("xml", StringComparison.OrdinalIgnoreCase);

    public async Task<CodebaseGraph> BuildAsync(
        string repoRoot,
        CodebaseGraphCache? priorCache,
        string? cacheDirectory = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(repoRoot) || !Directory.Exists(repoRoot))
            throw new DirectoryNotFoundException($"Repo root not found: {repoRoot}");

        var currentSha = await GitRevParseHeadAsync(repoRoot, ct);
        if (currentSha is null)
        {
            // Not a git repo (e.g. a fresh fixture); we still build the
            // graph, just without sha-tracking. Use a fixed sentinel
            // sha so the cache key is stable across runs.
            currentSha = "no-git-" + Environment.MachineName;
        }

        cacheDirectory ??= Path.Combine(repoRoot, ".portHorizon", "codebase-graph");

        // Warm path: same sha. Return the prior graph.
        if (priorCache is not null
            && priorCache.RepoSha == currentSha
            && File.Exists(priorCache.DiskPath))
        {
            var cached = JsonSerializer.Deserialize<CodebaseGraph>(await File.ReadAllTextAsync(priorCache.DiskPath, ct));
            if (cached is not null) return cached;
        }

        // Cold or warm-but-changed. Read changed files via git if available.
        IReadOnlyList<string> changedFiles = Array.Empty<string>();
        if (priorCache is not null && priorCache.RepoSha != "no-git-" + Environment.MachineName)
        {
            changedFiles = await GitDiffNameOnlyAsync(repoRoot, priorCache.RepoSha, currentSha, ct);
        }
        bool fullWalk = priorCache is null || changedFiles.Count == 0 && priorCache.RepoSha != currentSha;

        // The graph we'll build. For warm path, start from the prior
        // graph and mutate (remove + add edges for changed files). For
        // cold path, walk the whole repo.
        var files = new List<FileNode>();
        var imports = new List<ImportEdge>();
        var projects = new List<ProjectEdge>();
        var projectNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // .csproj path -> module id

        if (fullWalk)
        {
            WalkFull(repoRoot, files, imports, projects, projectNames);
        }
        else
        {
            // Load prior graph as a starting point.
            if (priorCache is not null && File.Exists(priorCache.DiskPath))
            {
                var prior = JsonSerializer.Deserialize<CodebaseGraph>(await File.ReadAllTextAsync(priorCache.DiskPath, ct));
                if (prior is not null)
                {
                    files.AddRange(prior.Files);
                    imports.AddRange(prior.Imports);
                    projects.AddRange(prior.Projects);
                    // Reconstruct projectNames from prior Projects edges.
                    foreach (var p in prior.Files) projectNames.TryAdd(p.Path, p.Module);
                }
            }
            // Remove edges involving changed files.
            var changedSet = new HashSet<string>(changedFiles, StringComparer.OrdinalIgnoreCase);
            imports.RemoveAll(e => changedSet.Contains(e.From) || changedSet.Contains(e.To));
            // Re-parse changed files. Skip ones that were deleted.
            foreach (var rel in changedFiles)
            {
                if (!rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    continue;
                var abs = Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(abs)) continue;
                if (rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    ParseCsproj(abs, rel, projects);
                else
                {
                    // Drop the file's old node and re-parse.
                    files.RemoveAll(f => string.Equals(f.Path, rel, StringComparison.OrdinalIgnoreCase));
                    var module = ResolveModule(rel, projectNames);
                    ParseCsFile(abs, rel, module, imports, files);
                }
            }
        }

        var built = new CodebaseGraph(
            RepoRoot: repoRoot,
            BuiltAt: DateTime.UtcNow,
            RepoSha: currentSha,
            Files: files,
            Imports: imports,
            Projects: projects);

        // Persist JSON.
        Directory.CreateDirectory(cacheDirectory);
        var diskPath = Path.Combine(cacheDirectory, SanitizeShaForFilename(currentSha) + ".json");
        var json = JsonSerializer.Serialize(built, new JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(diskPath, json, ct);

        return built;
    }

    private static string SanitizeShaForFilename(string sha)
    {
        var safe = new char[sha.Length];
        for (var i = 0; i < sha.Length; i++)
        {
            var c = sha[i];
            safe[i] = char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_';
        }
        return new string(safe);
    }

    private void WalkFull(
        string repoRoot,
        List<FileNode> files,
        List<ImportEdge> imports,
        List<ProjectEdge> projects,
        Dictionary<string, string> projectNames)
    {
        // Pass 1: csproj files. Build projectNames map.
        foreach (var csproj in Directory.EnumerateFiles(repoRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(repoRoot, csproj).Replace('\\', '/');
            var module = Path.GetFileNameWithoutExtension(csproj);
            projectNames[rel] = module;
            ParseCsproj(csproj, rel, projects);
        }

        // Pass 2: .cs files.
        foreach (var cs in Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(repoRoot, cs).Replace('\\', '/');
            var module = ResolveModule(rel, projectNames);
            ParseCsFile(cs, rel, module, imports, files);
        }
    }

    private static string ResolveModule(string rel, Dictionary<string, string> projectNames)
    {
        // Walk up the directory tree from the file until we find a
        // directory that contains a .csproj (recorded in projectNames).
        // Fall back to "<root>" if no match.
        var parts = rel.Split('/');
        for (var i = parts.Length - 1; i > 0; i--)
        {
            var dirRel = string.Join('/', parts.Take(i)) + "/<placeholder>.csproj";
            // The directory portion is parts[..i] joined; the csproj file
            // name is the file that lives in that directory. We don't
            // know the file name, but we stored entries keyed by their
            // exact csproj relative path. So we scan projectNames for any
            // entry whose directory matches.
        }
        // Simpler: scan projectNames for the longest-prefix match.
        var bestMatch = "";
        var bestLen = -1;
        foreach (var key in projectNames.Keys)
        {
            var dir = key.Contains('/') ? key[..key.LastIndexOf('/')] : "";
            if (rel.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase) && dir.Length > bestLen)
            {
                bestLen = dir.Length;
                bestMatch = key;
            }
        }
        return bestLen >= 0 ? projectNames[bestMatch] : "<root>";
    }

    private void ParseCsproj(string absPath, string relPath, List<ProjectEdge> projects)
    {
        string text;
        try { text = File.ReadAllText(absPath); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        var from = Path.GetFileNameWithoutExtension(relPath);
        foreach (Match m in ProjectReferenceRegex.Matches(text))
        {
            var raw = m.Groups["path"].Value;
            // Resolve relative path: ../Foo/Foo.csproj -> <basedir>/Foo/Foo.csproj.
            // We don't track basedir explicitly; the .csproj lives in
            // its own directory. So `<basedir>` is the .csproj's dir.
            var basedir = relPath.Contains('/') ? relPath[..relPath.LastIndexOf('/')] : "";
            var resolved = ResolveRelativePath(basedir, raw).Replace('\\', '/');
            var to = Path.GetFileNameWithoutExtension(resolved);
            if (to.Length > 0 && to != from)
                projects.Add(new ProjectEdge(from, to));
        }
    }

    private void ParseCsFile(
        string absPath, string relPath, string module,
        List<ImportEdge> imports, List<FileNode> files)
    {
        string text;
        try { text = File.ReadAllText(absPath); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        files.Add(new FileNode(relPath, "csharp", module));

        // Edge target: we map a `using Foo.Bar.Baz;` to a project whose
        // module id matches Foo.Bar, or to a synthetic node for
        // unresolved namespaces. For v1 we record the using-namespace
        // as the target file (relative-path-form) so the graph is
        // navigable in Mermaid; the per-module rollup is done by the
        // dashboard overlay.
        var matches = UsingDirectiveRegex.Matches(text);
        var seenEdges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in matches)
        {
            var ns = m.Groups["ns"].Value;
            if (ns.StartsWith("System", StringComparison.Ordinal)) continue; // skip BCL
            var edgeKey = relPath + "->" + ns;
            if (seenEdges.Add(edgeKey))
                imports.Add(new ImportEdge(relPath, ns));
        }
    }

    /// <summary>
    /// Resolve a path like "../Foo/Foo.csproj" relative to a base dir
    /// (e.g. "src/MyProj"). Returns a forward-slash relative path.
    /// </summary>
    internal static string ResolveRelativePath(string baseRel, string path)
    {
        var combined = string.IsNullOrEmpty(baseRel)
            ? path
            : baseRel + "/" + path;
        var parts = combined.Split('/');
        var stack = new System.Collections.Generic.Stack<string>();
        foreach (var raw in parts)
        {
            // Strip any leading slashes (a part may have come from
            // a path that starts with `/`, like "/Foo").
            var p = raw.TrimStart('/');
            if (p.Length == 0) continue;
            if (p == "." || p == "./") continue;
            if (p == ".." || p == "../") { if (stack.Count > 0) stack.Pop(); continue; }
            stack.Push(p);
        }
        return string.Join('/', stack.Reverse());
    }

    private static async Task<string?> GitRevParseHeadAsync(string repoRoot, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return null;
            var sha = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
            return string.IsNullOrEmpty(sha) ? null : sha;
        }
        catch { return null; }
    }

    private static async Task<IReadOnlyList<string>> GitDiffNameOnlyAsync(
        string repoRoot, string fromSha, string toSha, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"diff --name-only {fromSha} {toSha}",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return Array.Empty<string>();
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) return Array.Empty<string>();
            var text = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch { return Array.Empty<string>(); }
    }
}