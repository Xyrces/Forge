namespace Forge.Configuration;

/// <summary>
/// Resolves the on-disk state directory for a given project. Today the
/// v1 multi-project surface only differs by <see cref="ProjectOptions.Id"/>:
/// each project gets <c>{root}/.portHorizon/state/{id}/</c>, while
/// the legacy synthesized <c>"default"</c> project continues to use
/// the v0 path <c>{root}/.portHorizon/state/</c> for full backward
/// compatibility with existing on-disk DB files.
/// </summary>
public static class ProjectStateDirs
{
    public static string RootFor(ProjectOptions project, string dataRoot)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (!string.IsNullOrWhiteSpace(project.Root)) return project.Root;
        if (string.IsNullOrWhiteSpace(project.RepoUrl))
            throw new InvalidOperationException(
                $"Project '{project.Id}' has neither Root nor RepoUrl; cannot resolve a working copy path.");
        return ForgesystemPaths.ProjectDir(dataRoot, project.Id);
    }

    /// <summary>
    /// Returns the state directory for <paramref name="project"/>.
    /// The legacy <c>"default"</c> project keeps the v0 layout
    /// (<c>{root}/.portHorizon/state</c>) for backward compatibility
    /// with existing on-disk DB files. All other projects use the
    /// canonical layout that <c>ProjectBootstrap</c> creates:
    /// <c>{dataRoot}/projects/{id}/.forge/state</c> — uniform for
    /// both clone-managed and operator-managed repos, so state never
    /// pollutes an operator-owned working copy.
    /// </summary>
    public static string StateDirFor(ProjectOptions project, string dataRoot)
        => string.Equals(project.Id, "default", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(RootFor(project, dataRoot), ".portHorizon", "state")
            : ForgesystemPaths.StateDir(dataRoot, project.Id);

    public static string StateSubdirFor(ProjectOptions project)
        => string.Equals(project.Id, "default", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : project.Id;

    public static string IssuesDbFor(ProjectOptions project, string dataRoot)
        => Path.Combine(StateDirFor(project, dataRoot), "issues.db");

    public static string MemoryDbFor(ProjectOptions project, string dataRoot)
        => Path.Combine(StateDirFor(project, dataRoot), "memory.db");

    public static string IssuesJsonlFor(ProjectOptions project, string dataRoot)
        => Path.Combine(StateDirFor(project, dataRoot), "issues.jsonl");
}
