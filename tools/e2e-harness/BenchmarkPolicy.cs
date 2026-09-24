using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Agents;
using Forge.Core;
using Microsoft.Extensions.AI;

namespace Forge.Tools.E2E;

/// <summary>Validated benchmark-only routing and model budget policy.</summary>
internal sealed class BenchmarkPolicy
{
    private static readonly Regex SimpleId = new(
        "^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex EnvironmentName = new(
        "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private const string CredentialPrefix = "BENCHMARK_";

    private BenchmarkPolicy(
        string id,
        IReadOnlyDictionary<string, BenchmarkPolicyModel> models,
        BenchmarkPolicyRoles roles,
        int maxEngineeringAttempts)
    {
        Id = id;
        Models = models;
        Roles = roles;
        MaxEngineeringAttempts = maxEngineeringAttempts;
    }

    public string Id { get; }
    public IReadOnlyDictionary<string, BenchmarkPolicyModel> Models { get; }
    public BenchmarkPolicyRoles Roles { get; }
    public int MaxEngineeringAttempts { get; }
    public IReadOnlyCollection<string> CredentialEnvironmentNames =>
        Models.Values.Select(model => model.ApiKeyEnv).Distinct(StringComparer.Ordinal).ToArray();
    public IReadOnlyCollection<string> CredentialValues =>
        Models.Values.Select(model => model.ApiKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static BenchmarkPolicy Load(string path, bool requireCredentials)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Benchmark policy path must be absolute.", nameof(path));
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        var root = RequireObject(document.RootElement, "policy");
        RequireExactProperties(root, "policy", "id", "models", "roles", "maxEngineeringAttempts");
        var id = RequireSimpleId(root, "id", "policy.id");
        var maxAttempts = RequireInt(root, "maxEngineeringAttempts", "policy.maxEngineeringAttempts");
        if (maxAttempts is < 1 or > 3)
            throw new InvalidDataException("policy.maxEngineeringAttempts must be between 1 and 3.");

        if (!root.TryGetProperty("models", out var modelArray)
            || modelArray.ValueKind != JsonValueKind.Array
            || modelArray.GetArrayLength() == 0)
            throw new InvalidDataException("policy.models must be a nonempty array.");
        var models = new Dictionary<string, BenchmarkPolicyModel>(StringComparer.Ordinal);
        var credentialTargets = new Dictionary<string, (string Provider, Uri BaseUrl)>(StringComparer.Ordinal);
        foreach (var item in modelArray.EnumerateArray())
        {
            var modelObject = RequireObject(item, "policy.models[]");
            RequireExactProperties(modelObject, "policy.models[]",
                "id", "provider", "model", "baseUrl", "apiKeyEnv", "maxCalls",
                "maxInputTokens", "maxOutputTokens", "inputUsdPerMillion", "outputUsdPerMillion");
            var modelId = RequireSimpleId(modelObject, "id", "policy.models[].id");
            if (models.ContainsKey(modelId))
                throw new InvalidDataException($"Duplicate policy model id '{modelId}'.");
            var provider = RequireNonemptyString(modelObject, "provider", $"model '{modelId}' provider");
            var modelName = RequireNonemptyString(modelObject, "model", $"model '{modelId}' model");
            var baseUrlText = RequireNonemptyString(modelObject, "baseUrl", $"model '{modelId}' baseUrl");
            var baseUrl = ValidateBaseUrl(baseUrlText, modelId);
            var apiKeyEnv = RequireNonemptyString(modelObject, "apiKeyEnv", $"model '{modelId}' apiKeyEnv");
            if (!EnvironmentName.IsMatch(apiKeyEnv))
                throw new InvalidDataException($"Model '{modelId}' apiKeyEnv is not a valid environment name.");
            if (!apiKeyEnv.StartsWith(CredentialPrefix, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Model '{modelId}' apiKeyEnv must start with '{CredentialPrefix}' to avoid control-environment collisions.");
            if (credentialTargets.TryGetValue(apiKeyEnv, out var existingTarget)
                && (!string.Equals(existingTarget.Provider, provider, StringComparison.Ordinal)
                    || existingTarget.BaseUrl != baseUrl))
            {
                throw new InvalidDataException(
                    $"Credential environment variable '{apiKeyEnv}' cannot target different providers or endpoints.");
            }
            credentialTargets[apiKeyEnv] = (provider, baseUrl);
            var apiKey = Environment.GetEnvironmentVariable(apiKeyEnv);
            if (requireCredentials && string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidDataException($"Credential environment variable '{apiKeyEnv}' is missing.");
            var maxCalls = RequirePositiveInt(modelObject, "maxCalls", modelId);
            var maxInput = RequirePositiveInt(modelObject, "maxInputTokens", modelId);
            var maxOutput = RequirePositiveInt(modelObject, "maxOutputTokens", modelId);
            var inputRate = RequirePositiveDecimal(modelObject, "inputUsdPerMillion", modelId);
            var outputRate = RequirePositiveDecimal(modelObject, "outputUsdPerMillion", modelId);
            models.Add(modelId, new BenchmarkPolicyModel(
                modelId, provider, modelName, baseUrl, apiKeyEnv, apiKey,
                maxCalls, maxInput, maxOutput, inputRate, outputRate));
        }

        if (!root.TryGetProperty("roles", out var rolesElement))
            throw new InvalidDataException("policy.roles is required.");
        var rolesObject = RequireObject(rolesElement, "policy.roles");
        RequireProperties(rolesObject, "policy.roles", ["engineer", "critic", "reviewer"], ["escalation"]);
        var roles = new BenchmarkPolicyRoles(
            RequireModelReference(rolesObject, "engineer", models),
            RequireModelReference(rolesObject, "critic", models),
            RequireModelReference(rolesObject, "reviewer", models),
            rolesObject.TryGetProperty("escalation", out var escalation)
                ? RequireModelReferenceValue(escalation, "policy.roles.escalation", models)
                : null);
        var referencedModels = new HashSet<string>(StringComparer.Ordinal)
        {
            roles.Engineer,
            roles.Critic,
            roles.Reviewer,
        };
        if (roles.Escalation is { } escalationId)
            referencedModels.Add(escalationId);
        var unusedModels = models.Keys.Where(id => !referencedModels.Contains(id)).ToArray();
        if (unusedModels.Length > 0)
            throw new InvalidDataException(
                $"Every policy model must be assigned to a role; unused: {string.Join(", ", unusedModels)}.");
        return new BenchmarkPolicy(
            id,
            new ReadOnlyDictionary<string, BenchmarkPolicyModel>(models),
            roles,
            maxAttempts);
    }

    public BenchmarkPolicyRuntime CreateRuntime(
        string workspaceRoot,
        IChatClientFactory? innerFactory = null)
        => new(this, workspaceRoot, innerFactory ?? new BenchmarkNoRetryChatClientFactory());

    private static JsonElement RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{name} must be an object.");
        return value;
    }

    private static void RequireExactProperties(JsonElement value, string name, params string[] properties) =>
        RequireProperties(value, name, properties, []);

    private static void RequireProperties(
        JsonElement value,
        string name,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> optional)
    {
        var allowed = new HashSet<string>(required, StringComparer.Ordinal);
        allowed.UnionWith(optional);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new InvalidDataException($"{name} contains unknown property '{property.Name}'.");
            if (!seen.Add(property.Name))
                throw new InvalidDataException($"{name} contains duplicate property '{property.Name}'.");
        }
        foreach (var property in required)
        {
            if (!seen.Contains(property))
                throw new InvalidDataException($"{name} is missing required property '{property}'.");
        }
    }

    private static string RequireSimpleId(JsonElement value, string property, string name)
    {
        var id = RequireNonemptyString(value, property, name);
        if (!SimpleId.IsMatch(id))
            throw new InvalidDataException($"{name} must contain only letters, digits, underscore, or hyphen.");
        return id;
    }

    private static string RequireNonemptyString(JsonElement value, string property, string name)
    {
        if (!value.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
            throw new InvalidDataException($"{name} must be a nonempty string.");
        var result = element.GetString()!;
        if (result.Any(char.IsControl))
            throw new InvalidDataException($"{name} cannot contain control characters.");
        return result;
    }

    private static int RequireInt(JsonElement value, string property, string name)
    {
        if (!value.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var result))
            throw new InvalidDataException($"{name} must be an integer.");
        return result;
    }

    private static int RequirePositiveInt(JsonElement value, string property, string modelId)
    {
        var result = RequireInt(value, property, $"Model '{modelId}' {property}");
        if (result <= 0)
            throw new InvalidDataException($"Model '{modelId}' {property} must be positive.");
        return result;
    }

    private static decimal RequirePositiveDecimal(JsonElement value, string property, string modelId)
    {
        if (!value.TryGetProperty(property, out var element)
            || !TryReadDecimal(element, out var result)
            || result <= 0)
            throw new InvalidDataException($"Model '{modelId}' {property} must be a finite positive decimal.");
        return result;
    }

    private static bool TryReadDecimal(JsonElement element, out decimal result)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetDecimal(out result);
        if (element.ValueKind == JsonValueKind.String)
        {
            return decimal.TryParse(
                element.GetString(),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out result);
        }
        result = default;
        return false;
    }

    private static Uri ValidateBaseUrl(string value, string modelId)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.IsDefaultPort && uri.Port != 443
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host, out _))
            throw new InvalidDataException($"Model '{modelId}' baseUrl must be a public HTTPS hostname on port 443 without credentials, query, or fragment.");
        return uri;
    }

    private static string RequireModelReference(
        JsonElement roles,
        string property,
        IReadOnlyDictionary<string, BenchmarkPolicyModel> models)
    {
        var id = RequireNonemptyString(roles, property, $"policy.roles.{property}");
        if (!models.ContainsKey(id))
            throw new InvalidDataException($"policy.roles.{property} references unknown model id '{id}'.");
        return id;
    }

    private static string RequireModelReferenceValue(
        JsonElement element,
        string name,
        IReadOnlyDictionary<string, BenchmarkPolicyModel> models)
    {
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            throw new InvalidDataException($"{name} must be a model id string.");
        var id = element.GetString()!;
        if (!models.ContainsKey(id))
            throw new InvalidDataException($"{name} references unknown model id '{id}'.");
        return id;
    }
}

internal sealed record BenchmarkPolicyModel(
    string Id,
    string Provider,
    string Model,
    Uri BaseUrl,
    string ApiKeyEnv,
    string? ApiKey,
    int MaxCalls,
    int MaxInputTokens,
    int MaxOutputTokens,
    decimal InputUsdPerMillion,
    decimal OutputUsdPerMillion);

internal sealed record BenchmarkPolicyRoles(
    string Engineer,
    string Critic,
    string Reviewer,
    string? Escalation);

/// <summary>Shared meters plus deterministic role routing for one policy run.</summary>
internal sealed class BenchmarkPolicyRuntime
{
    private readonly BenchmarkPolicy _policy;
    private readonly IReadOnlyDictionary<string, ModelRuntime> _models;
    private int _useEscalation;

    internal BenchmarkPolicyRuntime(
        BenchmarkPolicy policy,
        string workspaceRoot,
        IChatClientFactory innerFactory)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Path.IsPathFullyQualified(workspaceRoot))
            throw new ArgumentException("Benchmark workspace root must be absolute.", nameof(workspaceRoot));
        _policy = policy;
        MaxEngineeringAttempts = policy.MaxEngineeringAttempts;
        var attemptId = Guid.NewGuid().ToString("N");
        var models = new Dictionary<string, ModelRuntime>(StringComparer.Ordinal);
        foreach (var definition in policy.Models.Values)
        {
            var config = new LlmConfig(new ProviderConfig(
                definition.Provider,
                definition.BaseUrl.AbsoluteUri,
                definition.ApiKey,
                OrgId: null,
                DefaultModel: definition.Model));
            var meter = new BenchmarkMeteringFactory(
                innerFactory,
                new BenchmarkMeteringOptions(
                    attemptId,
                    policy.Id,
                    definition.Provider,
                    definition.Model,
                    definition.MaxCalls,
                    definition.MaxInputTokens,
                    definition.MaxOutputTokens,
                    definition.InputUsdPerMillion,
                    definition.OutputUsdPerMillion),
                Path.Combine(workspaceRoot, "state", $"usage-{definition.Id}.json"));
            models.Add(definition.Id, new ModelRuntime(definition, config, meter));
        }
        _models = new ReadOnlyDictionary<string, ModelRuntime>(models);
        Factory = new RoutingFactory(this);
    }

    public IChatClientFactory Factory { get; }
    public int MaxEngineeringAttempts { get; }
    public BenchmarkPolicyModelLabel EngineerModel => Label(_policy.Roles.Engineer);
    public BenchmarkPolicyModelLabel CriticModel => Label(_policy.Roles.Critic);
    public BenchmarkPolicyModelLabel ReviewerModel => Label(_policy.Roles.Reviewer);
    public BenchmarkPolicyModelLabel? EscalationModel =>
        _policy.Roles.Escalation is { } id ? Label(id) : null;
    public BenchmarkPolicyModelLabel ActiveEngineerModel => Label(EngineeringModel);

    public IReadOnlyDictionary<string, BenchmarkUsageSnapshot> Snapshots =>
        new ReadOnlyDictionary<string, BenchmarkUsageSnapshot>(
            _models.ToDictionary(pair => pair.Key, pair => pair.Value.Meter.Snapshot, StringComparer.Ordinal));

    public BenchmarkUsageSnapshot AggregateSnapshot
    {
        get
        {
            var snapshots = _models.Values.Select(value => value.Meter.Snapshot).ToArray();
            var accountingComplete = snapshots.All(value => value.AccountingComplete);
            return new BenchmarkUsageSnapshot(
                snapshots.Sum(value => value.Calls),
                snapshots.Sum(value => value.CompletedCalls),
                snapshots.Sum(value => value.FailedCalls),
                snapshots.Sum(value => value.InFlightCalls),
                snapshots.Sum(value => value.MissingUsageCalls),
                snapshots.Sum(value => value.InputTokens),
                snapshots.Sum(value => value.OutputTokens),
                snapshots.Sum(value => value.CachedInputTokens),
                snapshots.Sum(value => value.CacheWriteInputTokens),
                snapshots.Sum(value => value.KnownUsageEstimatedUsd),
                accountingComplete ? snapshots.Sum(value => value.EstimatedCostUsd ?? 0m) : null,
                accountingComplete);
        }
    }

    public bool HasProviderOrAccountingFailure
    {
        get
        {
            var snapshot = AggregateSnapshot;
            return snapshot.FailedCalls > 0
                || snapshot.MissingUsageCalls > 0
                || snapshot.InFlightCalls > 0
                || !snapshot.AccountingComplete
                || _models.Values.Any(model => model.Meter.HaltReason is not null);
        }
    }

    public void UseEscalation()
    {
        if (_policy.Roles.Escalation is null)
            throw new InvalidOperationException("Benchmark policy has no escalation model.");
        Interlocked.Exchange(ref _useEscalation, 1);
    }

    public IChatClient CreateReviewerClient()
    {
        ThrowIfPermanentlyHalted();
        var runtime = _models[_policy.Roles.Reviewer];
        return new PolicyGuardChatClient(
            this,
            runtime.Meter.CreateWithRoleIdentity(
                runtime.Config,
                AgentType.Reviewer,
                "FinalReviewer"));
    }

    private string EngineeringModel =>
        Volatile.Read(ref _useEscalation) == 1
            ? _policy.Roles.Escalation!
            : _policy.Roles.Engineer;

    private BenchmarkPolicyModelLabel Label(string modelId)
    {
        var model = _models[modelId].Definition;
        return new BenchmarkPolicyModelLabel(model.Id, model.Provider, model.Model);
    }

    private IChatClient Create(string modelId, AgentType role, string? projectId)
    {
        var runtime = _models[modelId];
        return new PolicyGuardChatClient(
            this,
            runtime.Meter.Create(runtime.Config, role, projectId));
    }

    private bool HasPermanentFailure
        => _models.Values.Any(model => model.Meter.HaltReason is not null);

    private void ThrowIfPermanentlyHalted()
    {
        if (HasPermanentFailure)
            throw new InvalidOperationException("Benchmark policy is halted after a provider or accounting failure.");
    }

    private sealed class RoutingFactory(BenchmarkPolicyRuntime runtime) : IChatClientFactory
    {
        public IChatClient Create(
            LlmConfig config,
            AgentType role,
            string? projectId = null,
            RoleModel? modelOverride = null)
        {
            _ = config;
            if (modelOverride is not null)
                throw new InvalidOperationException("Benchmark policy routing does not accept external model overrides.");
            runtime.ThrowIfPermanentlyHalted();
            var modelId = role switch
            {
                AgentType.CoreDev => runtime.EngineeringModel,
                AgentType.Reviewer => runtime._policy.Roles.Critic,
                _ => throw new InvalidOperationException($"Benchmark policy has no route for role {role}."),
            };
            return runtime.Create(modelId, role, projectId);
        }
    }

    private sealed class PolicyGuardChatClient(
        BenchmarkPolicyRuntime runtime,
        IChatClient inner) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            runtime.ThrowIfPermanentlyHalted();
            return InnerClient.GetResponseAsync(messages, options, cancellationToken);
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            runtime.ThrowIfPermanentlyHalted();
            await foreach (var update in InnerClient.GetStreamingResponseAsync(
                messages, options, cancellationToken))
            {
                yield return update;
            }
        }
    }

    private sealed record ModelRuntime(
        BenchmarkPolicyModel Definition,
        LlmConfig Config,
        BenchmarkMeteringFactory Meter);
}

internal sealed record BenchmarkPolicyModelLabel(string Id, string Provider, string Model);
