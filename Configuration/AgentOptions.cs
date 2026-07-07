namespace Forge.Configuration;

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
    /// <summary>
    /// v1 multi-project registry. When non-empty, the dashboard
    /// lists and exposes each project; when empty the legacy
    /// <see cref="WorkspaceOptions.Root"/> is shimmed as a single
    /// synthetic project id="default". The orchestrator dispatch
    /// loop still runs against the legacy workspace in v1; the
    /// multi-project surface is read-only for the dashboard.
    /// </summary>
    public ProjectsOptions Projects { get; set; } = new();
    /// <summary>
    /// P4 Stage B — runtime selection. "InProcess" (default)
    /// uses Microsoft.Agents.AI.Workflows InProcessExecution;
    /// "Durable" uses Microsoft.Agents.AI.DurableTask +
    /// DurableTask.Worker (DTS sidecar). The Durable runtime
    /// requires <see cref="OrchestratorOptions.DtsConnectionString"/>
    /// + a reachable DTS at the encoded endpoint.
    /// </summary>
    public OrchestratorOptions Orchestrator { get; set; } = new();
    /// <summary>
    /// Headroom proxy config. When <see cref="HeadroomOptions.Enabled"/>
    /// is true, the chat-client factory rewrites the LLM baseUrl
    /// to point at the local Headroom sidecar (default
    /// http://127.0.0.1:8787) and the sidecar forwards compressed
    /// requests to the upstream provider. See
    /// <c>docs/headroom.md</c> for the operator guide.
    /// </summary>
    public HeadroomOptions Headroom { get; set; } = new();
}

/// <summary>
/// Headroom proxy config. See <c>docs/headroom.md</c>.
/// </summary>
public sealed record HeadroomOptions
{
    /// <summary>
    /// When true, the chat-client factory rewrites the LLM
    /// baseUrl to <see cref="ProxyBaseUrl"/>. Default false
    /// (no rewriting; orchestrator talks to the upstream
    /// provider directly).
    /// </summary>
    public bool Enabled { get; set; } = false;
    /// <summary>
    /// Local URL of the Headroom sidecar proxy.
    /// </summary>
    public string ProxyBaseUrl { get; set; } = "http://127.0.0.1:8787";
    /// <summary>
    /// Mode passed to the proxy at boot. <c>token</c>
    /// (default): maximize compression. <c>cache</c>: freeze
    /// prior turns for provider KV-cache reuse.
    /// </summary>
    public string Mode { get; set; } = "token";
    /// <summary>
    /// When true, the proxy enables CCR — the
    /// <c>headroom_retrieve</c> tool is injected. Default
    /// true (we want reversibility; CCR cost is sub-millisecond).
    /// </summary>
    public bool CcrEnabled { get; set; } = true;
    /// <summary>
    /// Optional daily budget in USD. The proxy enforces this
    /// (returns 429 when exceeded). Set 0 = no limit.
    /// </summary>
    public double BudgetUsd { get; set; } = 0;
    /// <summary>
    /// When true, the orchestrator logs per-call token
    /// counts to a rolling <see cref="Core.CostTracker"/> +
    /// exposes them at <c>GET /api/cost/stats</c>. Default true.
    /// </summary>
    public bool TrackUsage { get; set; } = true;
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
    /// <summary>
    /// P4 e2e-harness switch. <c>Remote</c> (default) uses the
    /// real Octokit-backed <see cref="GitHubService"/>;
    /// <c>Local</c> uses <see cref="LocalGitHubService"/> which
    /// records PRs in-process against a local bare git. Only
    /// the e2e harness binary uses <c>Local</c>.
    /// </summary>
    public string Mode { get; set; } = "Remote";
    /// <summary>
    /// When <see cref="Mode"/> = <c>Local</c>, this is the path
    /// to the bare git repository the harness created. The
    /// GitHubService subclass pushes branches to it as if it
    /// were a remote.
    /// </summary>
    public string LocalRemotePath { get; set; } = string.Empty;
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

/// <summary>
/// P4 Stage B — runtime selection + Durable Task Scheduler
/// connection.
/// </summary>
public sealed record OrchestratorOptions
{
    /// <summary>
    /// Runtime: <c>InProcess</c> (default) or <c>Durable</c>.
    /// </summary>
    public string Execution { get; set; } = "InProcess";

    /// <summary>
    /// DTS connection string used when <see cref="Execution"/>
    /// is <c>Durable</c>. Default matches the DTS emulator
    /// image published by Microsoft.
    /// </summary>
    public string DtsConnectionString { get; set; } =
        "Endpoint=http://localhost:8080;TaskHub=default;Authentication=None";
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
