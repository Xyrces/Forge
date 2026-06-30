namespace PortHorizon.Agents.Configuration;

// Note: properties use { get; set; } (not init) so the Microsoft
// Extensions Configuration binder can populate them from appsettings.json.
// Records still give us value-equality; the mutable setters are only
// touched during config load.
public sealed record AgentOptions
{
    public GitHubOptions GitHub { get; set; } = new();
    public WorkspaceOptions Workspace { get; set; } = new();
    public SpawnerOptions Spawner { get; set; } = new();
    public DashboardOptions Dashboard { get; set; } = new();
    public LlmOptions Llm { get; set; } = new();
}

public sealed record GitHubOptions
{
    public string Owner { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}

public sealed record WorkspaceOptions
{
    public string Root { get; set; } = string.Empty;
    public string WorktreeRoot { get; set; } = ".portHorizon/worktrees";
    public string DefaultBranch { get; set; } = "main";
}

public sealed record SpawnerOptions
{
    public int MaxConcurrentSessions { get; set; } = 4;
    public int PollIntervalSeconds { get; set; } = 3;
    public int StaleMinutes { get; set; } = 30;
}

public sealed record DashboardOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 4097;
    public string Hostname { get; set; } = "127.0.0.1";
}

public sealed record LlmOptions
{
    // Note: the binder does not support IReadOnlyList / IReadOnlyDictionary
    // on records, so we expose the underlying mutable types. The runtime
    // LlmConfig layer wraps these into read-only types.
    public List<LlmProviderOptions> Providers { get; set; } = new();
    public string DefaultProvider { get; set; } = string.Empty;
    public Dictionary<string, LlmRoleModelOptions> Roles { get; set; } = new();
}

public sealed record LlmProviderOptions
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;
    public string DefaultModel { get; set; } = string.Empty;
}

public sealed record LlmRoleModelOptions
{
    public string ProviderName { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}
