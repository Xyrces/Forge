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
    public VisionOptions Vision { get; set; } = new();
}

/// <summary>
/// P0.5: vision.md import. The orchestrator reads this file from
/// the workspace at startup, surfaces it on a Vision tab in the
/// dashboard, and injects it into every agent prompt.
/// </summary>
public sealed record VisionOptions
{
    /// <summary>
    /// Path to the vision file, relative to <see cref="WorkspaceOptions.Root"/>.
    /// Default: <c>docs/MASTER_DESIGN.md</c>. The file is read once
    /// at startup; the operator can update it and restart the
    /// orchestrator to pick up the new content.
    /// </summary>
    public string Path { get; set; } = "docs/MASTER_DESIGN.md";
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

    // P2.b: Meshy config. The Meshy REST API is a different
    // service from the LLM providers; keep its config
    // top-level under llm.* for ease of operator editing.
    // Empty key is allowed (the Artist pipeline becomes a
    // no-op against Meshy; the agent can still set
    // AssetReady for non-visual specs).
    public string MeshyApiKey { get; set; } = string.Empty;
    public string MeshyBaseUrl { get; set; } = "https://api.meshy.ai";
    public int MeshyPollIntervalSeconds { get; set; } = 5;
    public int MeshyMaxWaitSeconds { get; set; } = 600;
    public int MeshyMaxConcurrentJobs { get; set; } = 4;
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
