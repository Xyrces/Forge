using Microsoft.Data.Sqlite;

namespace Forge.Core;

/// <summary>
/// One read-only snippet from the codebase that the agent
/// receives as part of the project context.
/// </summary>
public sealed record CodeSnippet(string Path, string Language, string Body, int MaxBytes);

/// <summary>
/// The full context a project-aware agent sees before its run.
/// Built once per agent run; the agent's instructions reference
/// the field names ("open issues", "recent specs", etc.).
/// </summary>
public sealed record ProjectContext(
    string ProjectId,
    string RepoRoot,
    IReadOnlyList<CodeSnippet> CodeSnippets,
    IReadOnlyList<IssueRecord> OpenIssues,
    IReadOnlyList<SpecRecord> RecentSpecs,
    IReadOnlyList<SkillRecord> ProjectSkills)
{
    public static ProjectContext Empty(string projectId) =>
        new(projectId, string.Empty, Array.Empty<CodeSnippet>(),
            Array.Empty<IssueRecord>(), Array.Empty<SpecRecord>(),
            Array.Empty<SkillRecord>());
}

/// <summary>
/// Source of project context. Phase 2 implements a snapshot
/// builder; later phases can swap in a RAG-backed source.
/// </summary>
public interface IProjectContextSource
{
    Task<ProjectContext> BuildAsync(string projectId, CancellationToken ct = default);
}

/// <summary>
/// Phase 2 implementation: walks the repo + IssueStore + SpecStore
/// + SkillStore to produce a hand-curated snapshot. No RAG.
///
/// <para>
/// The snapshot is intentionally simple and explainable:
/// <list type="bullet">
///   <item>README.md (or the first .md found at the repo root).</item>
///   <item>Top-level <c>*.sln</c> and <c>*.csproj</c> file paths (not contents).</item>
///   <item>One representative <c>.cs</c> per top-level subdir (the
///   <c>Class1.cs</c> or <c>Program.cs</c> if present, else the
///   first by name).</item>
///   <item>Open issues (status != Completed) and the last 5 specs
///   (by updated_at).</item>
///   <item>All skills for the project.</item>
/// </list>
/// </para>
/// </summary>
public sealed class FilesystemProjectContextSource : IProjectContextSource
{
    private const int MaxSnippetBytes = 4_000;
    private readonly IssueStore _issues;
    private readonly IAgentStore _agents;
    private readonly ISpecStore _specs;
    private readonly ISkillStore _skills;
    private readonly string _repoRoot;

    public FilesystemProjectContextSource(
        IssueStore issues,
        IAgentStore agents,
        ISpecStore specs,
        ISkillStore skills,
        string repoRoot)
    {
        _issues = issues;
        _agents = agents;
        _specs = specs;
        _skills = skills;
        _repoRoot = repoRoot;
    }

    public async Task<ProjectContext> BuildAsync(string projectId, CancellationToken ct = default)
    {
        var snippets = new List<CodeSnippet>();
        IReadOnlyList<IssueRecord> openIssues = Array.Empty<IssueRecord>();
        IReadOnlyList<SpecRecord> recentSpecs = Array.Empty<SpecRecord>();
        IReadOnlyList<SkillRecord> projectSkills = Array.Empty<SkillRecord>();

        if (Directory.Exists(_repoRoot))
        {
            // README at repo root.
            var readme = Path.Combine(_repoRoot, "README.md");
            if (File.Exists(readme))
                snippets.Add(SnippetFromFile(readme, MaxSnippetBytes));

            // .sln and .csproj at the top level.
            foreach (var sln in Directory.EnumerateFiles(_repoRoot, "*.sln", SearchOption.TopDirectoryOnly))
                snippets.Add(SnippetFromFile(sln, MaxSnippetBytes, isPathOnly: true));
            foreach (var csproj in Directory.EnumerateFiles(_repoRoot, "*.csproj", SearchOption.TopDirectoryOnly))
                snippets.Add(SnippetFromFile(csproj, MaxSnippetBytes, isPathOnly: true));

            // One representative .cs per top-level subdir.
            foreach (var dir in Directory.EnumerateDirectories(_repoRoot))
            {
                try
                {
                    var skipDirs = new[] { "bin", "obj", ".git", ".vs", "node_modules", ".portHorizon" };
                    var name = Path.GetFileName(dir);
                    if (skipDirs.Contains(name)) continue;
                    var cs = Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly)
                        .OrderBy(p => p.EndsWith("Program.cs", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(p => p, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (cs is not null) snippets.Add(SnippetFromFile(cs, MaxSnippetBytes));
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }

        // Open issues (Pending + InProgress, not just Created).
        var allIssues = await _issues.ListAsync(new IssueFilter(), ct);
        openIssues = allIssues
            .Where(i => i.Status == IssueStatus.Pending || i.Status == IssueStatus.InProgress)
            .Take(25)
            .ToList();

        // Recent specs.
        var allSpecs = await _specs.ListAsync(projectId, status: null, ct);
        recentSpecs = allSpecs
            .OrderByDescending(s => s.UpdatedAt)
            .Take(5)
            .ToList();

        // Project skills: not yet project-scoped (skill store has no
        // project_id yet). For now we return all skills; future
        // version filters by project.
        var allSkills = await _skills.ListAsync(agentId: null, globalOnly: true, ct);
        projectSkills = allSkills.ToList();

        return new ProjectContext(
            projectId, _repoRoot, snippets, openIssues, recentSpecs, projectSkills);
    }

    private static CodeSnippet SnippetFromFile(string path, int maxBytes, bool isPathOnly = false)
    {
        try
        {
            var body = isPathOnly ? "(path only)" : File.ReadAllText(path);
            if (body.Length > maxBytes) body = body[..maxBytes] + "\n... (truncated)";
            return new CodeSnippet(
                Path: Path.GetFileName(path),
                Language: Path.GetExtension(path).TrimStart('.'),
                Body: body,
                MaxBytes: maxBytes);
        }
        catch (Exception)
        {
            return new CodeSnippet(Path.GetFileName(path), Path.GetExtension(path).TrimStart('.'),
                "(unreadable)", maxBytes);
        }
    }
}