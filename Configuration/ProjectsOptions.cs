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

    // Per-project role-territory overrides (role -> prefixes + root-file
    // allowance), persisted in project.roles_json under "$territory".
    // Empty = the built-in RoleAgentRegistry territory applies.
    public Dictionary<string, Core.RoleTerritory> Territories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Pre-push verification commands (the "$verify" roles_json key):
    // shell commands run in the worktree before a dev run's branch is
    // pushed. Null = auto-detect (dotnet build+test for dotnet repos);
    // empty = verification disabled.
    public List<string>? VerifyCommands { get; set; }

    // Failure-triage agent opt-in (the "$triage" roles_json key, phase
    // 2). Off by default; while off, no TriageRequested events are
    // published for the project and the triage consumer drops hints.
    public bool TriageEnabled { get; set; }

    // Watch-lane QA stage opt-in (the "$qa" roles_json key). On: every
    // PR gets a QA playthrough at the head before the reviewer runs,
    // and the merge gate requires qaVerdict=pass at the current head.
    public bool QaEnabled { get; set; }

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
        ["triage"] = 1,
    };

    public static int MaxFor(Dictionary<string, int> roles, string role)
    {
        foreach (var kv in roles)
            if (string.Equals(kv.Key, role, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return Default.TryGetValue(role, out var d) ? d : 1;
    }
}

public static class ProjectRecordOptionsMapping
{
    /// <summary>
    /// The single <see cref="Core.ProjectRecord"/> -&gt; <see cref="ProjectOptions"/>
    /// mapping, shared by the dashboard-side KnownProjects mapper and the
    /// watch lane's bundle mapping. A second hand-maintained copy dropping
    /// newly added roles_json keys ($triage/$qa) caused the 2026-08-23
    /// stale-flag incident — add new keys HERE, once.
    /// </summary>
    public static ProjectOptions ToProjectOptions(this Core.ProjectRecord r, string root) => new()
    {
        Id = r.Id,
        Name = r.Name,
        RepoUrl = r.RepoUrl,
        DefaultBranch = r.DefaultBranch,
        Root = root,
        Roles = new Dictionary<string, int>(r.Roles, StringComparer.OrdinalIgnoreCase),
        Territories = new Dictionary<string, Core.RoleTerritory>(r.Territories, StringComparer.OrdinalIgnoreCase),
        VerifyCommands = r.VerifyCommands?.ToList(),
        TriageEnabled = r.TriageEnabled,
        QaEnabled = r.QaEnabled,
    };
}
