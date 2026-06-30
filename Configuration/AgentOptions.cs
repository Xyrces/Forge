namespace PortHorizon.Agents.Configuration;

public sealed record AgentOptions
{
    public KiloOptions Kilo { get; init; } = new();
    public GitHubOptions GitHub { get; init; } = new();
    public WorkspaceOptions Workspace { get; init; } = new();
    public AcpServerOptions AcpServer { get; init; } = new();
    public SpawnerOptions Spawner { get; init; } = new();
    public DashboardOptions Dashboard { get; init; } = new();
    public OrchestratorOptions Orchestrator { get; init; } = new();
    public LlmOptions Llm { get; init; } = new();
}

public sealed record KiloOptions
{
    public string Provider { get; init; } = "kilocode";
    public string Model { get; init; } = "kilocode/minimax-m3";
    public string OrgId { get; init; } = string.Empty;
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

public sealed record AcpServerOptions
{
    public string ExecutablePath { get; init; } = "kilo";
    public int Port { get; init; } = 4096;
    public string Hostname { get; init; } = "127.0.0.1";
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

public sealed record OrchestratorOptions
{
    /// <summary>
    /// Runtime for the agent step.
    /// <c>Acp</c> = legacy kilo/ACP path (default; staged removal).
    /// <c>Maf</c> = Microsoft Agent Framework runner.
    /// </summary>
    public string Runtime { get; init; } = "Acp";
}

public sealed record LlmOptions
{
    public string Provider { get; init; } = "Stub";
    public string Model { get; init; } = "stub-model";
    public string ApiKey { get; init; } = string.Empty;
    public string OrgId { get; init; } = string.Empty;
}
