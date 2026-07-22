using Microsoft.Extensions.Configuration;

namespace Forge.Configuration;

public static class OptionsLoader
{
    public static AgentOptions Load(string? configPath = null)
    {
        var builder = new ConfigurationBuilder();

        if (configPath is not null && File.Exists(configPath))
            builder.AddJsonFile(configPath, optional: true, reloadOnChange: false);

        // Resolve appsettings.json against the current working directory,
        // not the host's base directory. AddJsonFile's default base
        // directory is the AppContext.BaseDirectory (the bin/ folder when
        // launched via `dotnet run`), so a relative path silently misses
        // the project's source-side appsettings.json. Use the absolute
        // path so the file is found regardless of how the host is launched.
        var appsettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        builder.AddJsonFile(appsettingsPath, optional: true, reloadOnChange: false);
        builder.AddEnvironmentVariables();

        var config = builder.Build();
        var options = new AgentOptions();
        config.Bind(options);

        ApplyEnvOverrides(options, config);
        Validate(options);
        return options;
    }

    private static void ApplyEnvOverrides(AgentOptions options, IConfiguration config)
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
            var newProvider = new LlmProviderOptions
            {
                Name = llmProviderName,
                BaseUrl = !string.IsNullOrEmpty(llmBaseUrl) ? llmBaseUrl : "http://127.0.0.1:4096",
                ApiKey = llmApiKey,
                OrgId = Environment.GetEnvironmentVariable("LLM_ORG_ID") ?? string.Empty,
                DefaultModel = !string.IsNullOrEmpty(llmModel) ? llmModel : "stub-model",
            };
            if (idx >= 0) existing[idx] = newProvider;
            else existing.Add(newProvider);
            options.Llm.Providers = existing;
            if (string.IsNullOrEmpty(options.Llm.DefaultProvider))
                options.Llm.DefaultProvider = llmProviderName;
        }

        // Config-file github.token wins over GITHUB_TOKEN / GitHub__Token
        // env vars. Tests load OptionsLoader from a temp appsettings.json
        // (e.g. github.token = "gh-test"); the host shell may also export
        // GITHUB_TOKEN for real GitHub access. Without this precedence,
        // the env var silently clobbers the test fixture and the test
        // fails with the wrong token. ExtractToken trims + nulls-out
        // whitespace-only values so a placeholder string in appsettings
        // still defers to the env var.
        var configToken = config.GetValue<string?>("github:token");
        var ghToken = ExtractToken(configToken);
        ghToken ??= Environment.GetEnvironmentVariable("GitHub__Token")
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(ghToken))
            options.GitHub.Token = ghToken;
    }

    private static string? ExtractToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var trimmed = token.Trim();
        return trimmed.Length == 0 ? null : trimmed;
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
