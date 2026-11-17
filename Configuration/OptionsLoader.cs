using Microsoft.Extensions.Configuration;

namespace Forge.Configuration;

public static class OptionsLoader
{
    public static AgentOptions Load(string? configPath = null)
    {
        var builder = new ConfigurationBuilder();

        // Resolve appsettings.json against the current working directory,
        // not the host's base directory. AddJsonFile's default base
        // directory is the AppContext.BaseDirectory (the bin/ folder when
        // launched via `dotnet run`), so a relative path silently misses
        // the project's source-side appsettings.json. Use the absolute
        // path so the file is found regardless of how the host is launched.
        var appsettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        builder.AddJsonFile(appsettingsPath, optional: true, reloadOnChange: false);
        if (configPath is not null)
            builder.AddJsonFile(Path.GetFullPath(configPath), optional: false, reloadOnChange: false);
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
        // LLM provider env-var override. The kilo gateway (and OpenAI,
        // Anthropic, etc.) need an API key. We inject a single-provider
        // entry so tests can run against the real gateway without
        // committing secrets to appsettings.json.
        var llmBaseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL");
        var llmApiKey = Environment.GetEnvironmentVariable("LLM_API_KEY");
        var llmModel = Environment.GetEnvironmentVariable("LLM_MODEL");
        var llmProviderName = Environment.GetEnvironmentVariable("LLM_PROVIDER_NAME")
            ?? Agents.LlmProviders.KiloGateway;

        if (!string.IsNullOrEmpty(llmApiKey))
        {
            var existing = options.Llm.Providers.ToList();
            var idx = existing.FindIndex(p => string.Equals(p.Name, llmProviderName, StringComparison.OrdinalIgnoreCase));
            var template = idx >= 0 ? existing[idx] : new LlmProviderOptions();
            var newProvider = template with
            {
                Name = llmProviderName,
                BaseUrl = !string.IsNullOrEmpty(llmBaseUrl) ? llmBaseUrl : (string.IsNullOrEmpty(template.BaseUrl) ? "http://127.0.0.1:4096" : template.BaseUrl),
                ApiKey = llmApiKey,
                Api = Environment.GetEnvironmentVariable("LLM_API") ?? template.Api,
                ProjectId = Environment.GetEnvironmentVariable("LLM_PROJECT_ID") ?? template.ProjectId,
                ReasoningEffort = Environment.GetEnvironmentVariable("LLM_REASONING_EFFORT") ?? template.ReasoningEffort,
                OrgId = Environment.GetEnvironmentVariable("LLM_ORG_ID") ?? template.OrgId,
                DefaultModel = !string.IsNullOrEmpty(llmModel) ? llmModel : (string.IsNullOrEmpty(template.DefaultModel) ? "stub-model" : template.DefaultModel),
            };
            if (idx >= 0) existing[idx] = newProvider;
            else existing.Add(newProvider);
            options.Llm.Providers = existing;
            if (string.IsNullOrEmpty(options.Llm.DefaultProvider))
                options.Llm.DefaultProvider = llmProviderName;
        }

        var ghToken = Environment.GetEnvironmentVariable("GitHub__Token")
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(ghToken))
            options.GitHub.Token = ghToken;
    }

    private static void Validate(AgentOptions options)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.GitHub.Owner) || string.IsNullOrWhiteSpace(options.GitHub.Repo))
        {
            // GitHub.Owner/Repo are only required when the orchestrator
            // actually opens PRs. The bootstrap layer creates the local
            // workspace without them; downstream code that wants to
            // create a PR throws a focused error. Keep the check
            // advisory for now (no failure, just visibility).
        }
        if (options.Spawner.MaxConcurrentSessions <= 0)
            errors.Add("Spawner.MaxConcurrentSessions must be > 0");

        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid agent configuration: " + string.Join("; ", errors));
    }
}
