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
    public static string RootFor(ProjectOptions project)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(project.Root))
            throw new InvalidOperationException($"Project '{project.Id}' has empty Root.");
        return project.Root;
    }

    /// <summary>
    /// Returns the state directory for <paramref name="project"/>.
    /// For <c>Id == "default"</c> and no explicit per-project override,
    /// returns <c>{root}/.portHorizon/state</c> (legacy layout).
    /// All other projects use <c>{root}/.portHorizon/state/{id}</c>.
    /// </summary>
    public static string StateDirFor(ProjectOptions project)
        => Path.Combine(RootFor(project), ".portHorizon", "state", StateSubdirFor(project));

    public static string StateSubdirFor(ProjectOptions project)
        => string.Equals(project.Id, "default", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : project.Id;

    public static string IssuesDbFor(ProjectOptions project)
        => Path.Combine(StateDirFor(project), "issues.db");

    public static string MemoryDbFor(ProjectOptions project)
        => Path.Combine(StateDirFor(project), "memory.db");

    public static string IssuesJsonlFor(ProjectOptions project)
        => Path.Combine(StateDirFor(project), "issues.jsonl");
}
