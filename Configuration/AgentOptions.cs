namespace PortHorizon.Agents.Configuration;

public sealed record AgentOptions
{
    public GitHubOptions GitHub { get; init; } = new();
    public WorkspaceOptions Workspace { get; init; } = new();
    public SpawnerOptions Spawner { get; init; } = new();
    public DashboardOptions Dashboard { get; init; } = new();
    public LlmOptions Llm { get; init; } = new();
}

public sealed record GitHubOptions
{
    public string Owner { get; init; } = string.Empty;
    public string Repo { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
}

public sealed record WorkspaceOptions
{
    public string Root { get; init; } = string.Empty;
    public string WorktreeRoot { get; init; } = ".portHorizon/worktrees";
    public string DefaultBranch { get; init; } = "main";
}

public sealed record SpawnerOptions
{
    public int MaxConcurrentSessions { get; init; } = 4;
    public int PollIntervalSeconds { get; init; } = 3;
    public int StaleMinutes { get; init; } = 30;
}

public sealed record DashboardOptions
{
    public bool Enabled { get; init; } = true;
    public int Port { get; init; } = 4097;
    public string Hostname { get; init; } = "127.0.0.1";
}

public sealed record LlmOptions
{
    /// <summary>
    /// List of LLM providers. The orchestrator picks one per role based on
    /// the <c>Roles</c> dict; falls back to <see cref="DefaultProvider"/>.
    /// </summary>
    public IReadOnlyList<LlmProviderOptions> Providers { get; init; } = Array.Empty<LlmProviderOptions>();

    /// <summary>
    /// Name of the provider in <see cref="Providers"/> to use when a role
    /// has no explicit entry in <see cref="Roles"/>.
    /// </summary>
    public string DefaultProvider { get; init; } = string.Empty;

    /// <summary>
    /// Per-role model assignment. Key is the role (CoreDev, ClientDev, QA,
    /// Reviewer); value is the (provider, model) pair to use.
    /// </summary>
    public IReadOnlyDictionary<string, LlmRoleModelOptions> Roles { get; init; } =
        new Dictionary<string, LlmRoleModelOptions>();
}

public sealed record LlmProviderOptions
{
    public string Name { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string OrgId { get; init; } = string.Empty;
    public string DefaultModel { get; init; } = string.Empty;
}

public sealed record LlmRoleModelOptions
{
    public string ProviderName { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
}
