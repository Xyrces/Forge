namespace Forge.Configuration;

/// <summary>
/// Resolves Forge-owned filesystem locations independent of any
/// single <see cref="WorkspaceOptions.Root"/> the operator supplies.
/// Per-project files (state DBs, worktrees, scratch) live under a
/// platform-appropriate user-local root by default so the orchestrator
/// can scaffold a fresh project on a brand-new machine.
/// </summary>
public static class ForgesystemPaths
{
    /// <summary>
    /// Compute the canonical data root. Caller may pass an override
    /// (absolute path) from <see cref="ForgesystemOptions.DataRoot"/>;
    /// when empty, the platform default is returned.
    /// </summary>
    public static string ResolveDataRoot(string? overrideDataRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideDataRoot))
        {
            return Path.GetFullPath(overrideDataRoot);
        }
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "Forge");
        }
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
            return Path.Combine(home, "Library", "Application Support", "Forge");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg)) return Path.Combine(xdg, "forge");
        var linuxHome = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
        return Path.Combine(linuxHome, ".local", "share", "forge");
    }

    public static string ProjectsDir(string dataRoot)
        => Path.Combine(dataRoot, "projects");

    public static string ProjectDir(string dataRoot, string projectId)
        => Path.Combine(ProjectsDir(dataRoot), projectId);

    public static string StateDir(string dataRoot, string projectId)
        => Path.Combine(ProjectDir(dataRoot, projectId), ".forge", "state");

    public static string IssuesDb(string dataRoot, string projectId)
        => Path.Combine(StateDir(dataRoot, projectId), "issues.db");

    public static string MemoryDb(string dataRoot, string projectId)
        => Path.Combine(StateDir(dataRoot, projectId), "memory.db");

    public static string StateJsonl(string dataRoot, string projectId)
        => Path.Combine(StateDir(dataRoot, projectId), "issues.jsonl");

    public static string WorktreeDir(string dataRoot, string projectId)
        => Path.Combine(ProjectDir(dataRoot, projectId), ".forge", "worktrees");

    public static string ArtOutputDir(string dataRoot, string projectId)
        => Path.Combine(ProjectDir(dataRoot, projectId), ".forge", "art-output");
}
