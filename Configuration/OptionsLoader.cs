using Microsoft.Extensions.Configuration;

namespace PortHorizon.Agents.Configuration;

public static class OptionsLoader
{
    public static AgentOptions Load(string? configPath = null)
    {
        var builder = new ConfigurationBuilder();

        if (configPath is not null && File.Exists(configPath))
            builder.AddJsonFile(configPath, optional: true, reloadOnChange: false);

        builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        builder.AddEnvironmentVariables();

        var config = builder.Build();
        var options = new AgentOptions();
        config.Bind(options);

        ApplyEnvOverrides(options);
        Validate(options);
        return options;
    }

    private static void ApplyEnvOverrides(AgentOptions options)
    {
        var kiloProvider = Environment.GetEnvironmentVariable("KILO_PROVIDER");
        if (!string.IsNullOrEmpty(kiloProvider))
            options = options with { Kilo = options.Kilo with { Provider = kiloProvider } };

        var kiloModel = Environment.GetEnvironmentVariable("KILO_MODEL");
        if (!string.IsNullOrEmpty(kiloModel))
            options = options with { Kilo = options.Kilo with { Model = kiloModel } };

        var kiloOrg = Environment.GetEnvironmentVariable("KILO_ORG_ID");
        if (!string.IsNullOrEmpty(kiloOrg))
            options = options with { Kilo = options.Kilo with { OrgId = kiloOrg } };

        var ghToken = Environment.GetEnvironmentVariable("GitHub__Token")
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(ghToken))
            options = options with { GitHub = options.GitHub with { Token = ghToken } };
    }

    private static void Validate(AgentOptions options)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Workspace.Root))
            errors.Add("Workspace.Root is required");
        if (string.IsNullOrWhiteSpace(options.GitHub.Owner))
            errors.Add("GitHub.Owner is required");
        if (string.IsNullOrWhiteSpace(options.GitHub.Repo))
            errors.Add("GitHub.Repo is required");
        if (options.Spawner.MaxConcurrentSessions <= 0)
            errors.Add("Spawner.MaxConcurrentSessions must be > 0");
        if (options.AcpServer.Port <= 0 || options.AcpServer.Port > 65535)
            errors.Add($"AcpServer.Port {options.AcpServer.Port} is out of range");

        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid agent configuration: " + string.Join("; ", errors));
    }
}
