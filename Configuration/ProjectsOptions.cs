namespace Forge.Configuration;

public sealed record ProjectsOptions
{
    public List<ProjectOptions> Projects { get; set; } = new();
}

public sealed record ProjectOptions
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    // Local path to the project's git working copy. When RepoUrl is
    // set, Forge clones into this path on bootstrap (managed mode).
    // When RepoUrl is empty, the operator owns the directory and
    // Forge just verifies it has a .git/ (operator-managed mode).
    //
    // DEPRECATED for the bootstrap-managed case: leave Root empty
    // and set RepoUrl — Forge will derive LocalPath = <dataRoot>/projects/<id>/
    // and clone into it. Existing operator-managed projects can
    // continue to set Root explicitly; the bootstrap honors it.
    public string Root { get; set; } = string.Empty;

    // Git URL Forge clones on project add. HTTPS with a Personal
    // Access Token is the primary mode (PAT is read from
    // GITHUB_TOKEN env var or github.token in appsettings.json).
    // SSH URLs work too — the credential helper installed by
    // ProjectCloner uses the user's existing ~/.ssh/config.
    public string RepoUrl { get; set; } = string.Empty;

    // Default branch for worktree base (main, master, develop, ...).
    // Forge clones with --branch <DefaultBranch> and uses this as
    // the merge base for agent branches.
    public string DefaultBranch { get; set; } = "main";

    public string? SkillPlaybookUrl { get; set; }
    public Dictionary<string, int> Roles { get; set; } = new();

    // P8: optional deployment pipeline config. Null/omitted means the
    // project has no configured deployment action -- it can still be
    // enqueued for build-verification, but "approve" has nothing to
    // execute. Each project can wire a totally different deployment
    // strategy (a one-line git-tag script, a Docker build+push, or --
    // Forge's own case -- a systemd unit bounce); see DeploymentKind.
    public DeploymentOptions? Deployment { get; set; }
}

public static class DefaultProjectRoles
{
    public static readonly Dictionary<string, int> Default = new(StringComparer.OrdinalIgnoreCase)
    {
        ["coredev"] = 2,
        ["clientdev"] = 2,
        ["reviewer"] = 2,
        ["intake"] = 1,
        ["designer"] = 1,
        ["artist"] = 1,
        ["groomer"] = 1,
        ["orchestrator"] = 1,
    };

    public static int MaxFor(Dictionary<string, int> roles, string role)
    {
        foreach (var kv in roles)
            if (string.Equals(kv.Key, role, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return Default.TryGetValue(role, out var d) ? d : 1;
    }
}
