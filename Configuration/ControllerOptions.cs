namespace Forge.Configuration;

public sealed class ControllerOptions
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "standby";
    public string DatasetId { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public string TokenFile { get; set; } = "/run/forge-control/controller-token";
    public string KeyringPath { get; set; } = "/var/lib/forge/keyring";
    public string ListenUrl { get; set; } = "http://0.0.0.0:9090";
    public int HeartbeatSeconds { get; set; } = 5;
}

public sealed class WorkerOptions
{
    public bool Enabled { get; set; }
    public string Image { get; set; } = "";
    public string Namespace { get; set; } = "forge";
    public string AttemptId { get; set; } = "";
    public string PodUid { get; set; } = "";
    public int DeadlineSeconds { get; set; } = 3600;
    public string CpuRequest { get; set; } = "500m";
    public string CpuLimit { get; set; } = "4";
    public string MemoryRequest { get; set; } = "1Gi";
    public string MemoryLimit { get; set; } = "8Gi";
}

public sealed class BootstrapOptions
{
    public bool ReviewAuthorityOnly { get; set; }
    public bool ProjectProvisionOnly { get; set; }
    public string ProjectId { get; set; } = "forge";
    public string RepoUrl { get; set; } = "https://github.com/Xyrces/Forge.git";
    public string DefaultBranch { get; set; } = "main";
    public string WorkerPrincipal { get; set; } = "forge-k3s-worker";
    public string ControllerPrincipal { get; set; } = "";
    // Only the initializer Job receives these immutable upgrade inputs.
    public int? MigrationFromVersion { get; set; }
    public int? MigrationToVersion { get; set; }
}

public sealed class BudgetOptions
{
    public bool PaidCallsEnabled { get; set; }
    public decimal LimitUsd { get; set; } = 25m;
    public decimal MaximumRequestUsd { get; set; } = 2m;
    public Dictionary<string, ModelPriceOptions> Models { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ModelPriceOptions
{
    public decimal InputUsdPerMillion { get; set; }
    public decimal OutputUsdPerMillion { get; set; }
    public int MaximumInputTokens { get; set; }
}
