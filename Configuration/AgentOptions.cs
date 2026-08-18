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
    /// Quality-gate configuration (ordered gate names per
    /// checkpoint). DB overrides (memory keys gates/run/*) win over
    /// this config; built-in defaults apply when both are empty.
    /// </summary>
    public GateOptions Gates { get; set; } = new();
    /// <summary>
    /// Lifecycle state machine (Phase 2). WriteAuthority=false
    /// (default): shadow mode — illegal transitions logged as
    /// warnings but allowed. true: authority mode — illegal
    /// transitions logged as errors and flagged in metadata.
    /// </summary>
    public StateOptions State { get; set; } = new();
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
    /// Application-level storage root. See <see cref="ForgesystemOptions"/>
    /// for defaults. When projects do not supply <c>workspace.root</c>,
    /// the bootstrap creates them under this location.
    /// </summary>
    public ForgesystemOptions Forgesystem { get; set; } = new();
    /// <summary>
    /// State-database backend. <c>db.provider</c> = sqlite (default)
    /// or sqlserver; <c>db.connectionString</c> carries the SQL Server
    /// target (Entra auth, no secrets). See <see cref="DbOptions"/>.
    /// </summary>
    public DbOptions Db { get; set; } = new();
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
    /// <summary>
    /// Internal messaging transport selection. <c>messaging.transport</c>
    /// = inmemory (default) or servicebus (reserved — lands when the
    /// Azure Service Bus transport ships in Talaria).
    /// </summary>
    public MessagingOptions Messaging { get; set; } = new();
}

/// <summary>Messaging transport config. See <see cref="AgentOptions.Messaging"/>.</summary>
public sealed record MessagingOptions
{
    public string Transport { get; set; } = "inmemory";
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
    /// The provider the proxy actually fronts (its
    /// <c>--provider-name</c> / upstream). Only THIS provider's
    /// baseUrl is rewritten to the proxy — the proxy speaks
    /// OpenAI chat-completions to a single upstream, so rewriting
    /// other providers misroutes them (observed live 2026-07-29:
    /// kimi chat 401/404'd through the kilo-gateway proxy).
    /// </summary>
    public string ProviderName { get; set; } = "kilo-gateway";
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
    /// <summary>
    /// Explicit root the operator already has on disk. Required for
    /// production (operator-managed) projects; the orchestrator will
    /// <c>git init</c> it if it exists but is not yet a repo, and then
    /// keep it under operator control. Used verbatim as the project's
    /// root path; never silently re-parented.
    /// </summary>
    public string Root { get; set; } = string.Empty;
    public string WorktreeRoot { get; set; } = ".portHorizon/worktrees";
    public string DefaultBranch { get; set; } = "main";
}

/// <summary>
/// Application-level root location for every Forge-owned file: state
/// DBs, worktrees, scratch output. Defaults to <c>%LOCALAPPDATA%/Forge</c>
/// on Windows and <c>$XDG_DATA_HOME/forge</c> (or
/// <c>~/.local/share/forge</c>) on Linux/macOS. Operators can override
/// with a single absolute path; useful for portable / dev machine
/// setups. When projects omit <see cref="WorkspaceOptions.Root"/>, the
/// bootstrap creates them under this root in a per-project subdirectory.
/// </summary>
public sealed record ForgesystemOptions
{
    /// <summary>
    /// Override the AppData-derived default. When empty, the bootstrap
    /// picks the platform-appropriate user-local root. When non-empty,
    /// must be an absolute path; the bootstrap uses it verbatim.
    /// </summary>
    public string DataRoot { get; set; } = string.Empty;
}

public sealed record SpawnerOptions
{
    public int MaxConcurrentSessions { get; set; } = 4;
    public int PollIntervalSeconds { get; set; } = 3;
    public int StaleMinutes { get; set; } = 30;
    /// <summary>
    /// Hard wall-clock timeout for a single agent run (LLM call +
    /// tool invocations). When exceeded, the run is cancelled and
    /// the issue is left in Pending for retry (or Failed if retries
    /// exhausted). A diagnostic entry ("agentTimeout") is recorded
    /// in issue metadata. Set to 0 or negative to disable.
    /// Default: 15 minutes.
    /// </summary>
    public double AgentRunTimeoutMinutes { get; set; } = 15.0;
}

public sealed record DashboardOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 4097;
    public string Hostname { get; set; } = "127.0.0.1";
    public KestrelOptions Kestrel { get; set; } = new();
}

public sealed record KestrelOptions
{
    public Dictionary<string, KestrelEndpoint> Endpoints { get; set; } = new();
    public KestrelHttpsOptions Https { get; set; } = new();
    public bool RedirectHttps { get; set; } = true;
}

public sealed record KestrelEndpoint
{
    public string Url { get; set; } = string.Empty;
}

public sealed record KestrelHttpsOptions
{
    public KestrelCertificateOptions Certificate { get; set; } = new();
}

public sealed record KestrelCertificateOptions
{
    public string Path { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
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

    /// <summary>
    /// Days a task may sit Failed with no operator action before the
    /// sprint assembler auto-closes it as abandoned (operator
    /// direction 2026-08-18: "fix this permanently" — a backlog held
    /// hostage by ancient failures starved sprint assembly silently).
    /// Fresh failures are never touched (the no-auto-clear rule
    /// protects active investigation). 0/null disables the sweep.
    /// </summary>
    public int? FailureAgingDays { get; set; } = 7;
}

public sealed record LlmOptions
{
    // Note: the binder does not support IReadOnlyList / IReadOnlyDictionary
    // on records, so we expose the underlying mutable types. The runtime
    // LlmConfig layer wraps these into read-only types.
    public List<LlmProviderOptions> Providers { get; set; } = new();
    public string DefaultProvider { get; set; } = string.Empty;
    public Dictionary<string, LlmRoleModelOptions> Roles { get; set; } = new();

    // Max simultaneous round-trips per provider across ALL subsystems
    // (dev agents + groomer + designer + reviewer + intake). Guards
    // account-level rate quotas against multi-agent bursts.
    public int MaxConcurrentRequests { get; set; } = 2;

    // In-place retries for transient "engine overloaded" 429s
    // (server-side capacity, NOT account quota) before the model is
    // cooled down. Exponential backoff + jitter; Retry-After honored.
    // 0 disables (every overload 429 cools immediately).
    public int OverloadRetryCount { get; set; } = 3;

    // Minimum milliseconds between admitted requests PER PROVIDER
    // (reserve-ahead pacing). Anti-herd: slots resuming after a
    // shared cooldown leave spaced instead of in the same millisecond
    // (MiniMax Token Plan dynamic throttling punishes the burst
    // shape). 0 disables.
    public int MinRequestIntervalMs { get; set; } = 500;

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

    // Wire protocol: "openai" (default) | "anthropic" (Anthropic
    // Messages API, e.g. Kimi-for-Coding).
    public string Api { get; set; } = string.Empty;

    // True when the provider's rate limits are account-level and
    // shared across ALL models (Kimi documents this): a quota 429 on
    // one model cools every model on the provider. Default false
    // (per-model cooldowns — the kilo-gateway behavior).
    public bool SharedQuota { get; set; }

    // Default max_tokens for Anthropic-protocol requests when the
    // caller doesn't set ChatOptions.MaxOutputTokens. 0 = 8192. Kimi
    // meters TPM as prompt_tokens + REQUESTED max_tokens, so a lower
    // default directly reduces TPM pressure.
    public int MaxOutputTokens { get; set; }
}

public sealed record LlmRoleModelOptions
{
    public string ProviderName { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}
