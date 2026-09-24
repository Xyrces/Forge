using System.Runtime.CompilerServices;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Forge.Agents;
using Forge.Core;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Forge.Tools.E2E;

/// <summary>
/// Explicit limits and caller-supplied rates for one live benchmark attempt.
/// Rates are estimates only; this type is not a provider billing authority.
/// </summary>
internal sealed record BenchmarkMeteringOptions(
    string AttemptId,
    string ScenarioId,
    string Provider,
    string Model,
    int MaxCalls,
    int MaxInputTokensPerCall,
    int MaxOutputTokensPerCall,
    decimal InputUsdPerMillionTokens,
    decimal OutputUsdPerMillionTokens,
    decimal? CachedInputUsdPerMillionTokens = null);

/// <summary>
/// Benchmark-only OpenAI-compatible factory with both SDK and Forge overload
/// retries absent. One wrapper call therefore maps to at most one HTTP attempt.
/// </summary>
internal sealed class BenchmarkNoRetryChatClientFactory : IChatClientFactory
{
    public IChatClient Create(
        LlmConfig config,
        AgentType role,
        string? projectId = null,
        RoleModel? modelOverride = null)
    {
        ProviderConfig provider;
        string model;
        if (modelOverride is not null)
            (provider, model) = config.ResolveExplicit(modelOverride);
        else
            (provider, model, _) = config.ResolveEffective(role, overrides: null, projectId);

        if (string.Equals(provider.Api, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "The benchmark no-retry transport supports OpenAI-compatible providers only.");
        }
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException($"Benchmark provider '{provider.Name}' has no API key.");

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.BaseUrl),
            NetworkTimeout = TimeSpan.FromMinutes(5),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };
        var client = new OpenAIClient(new ApiKeyCredential(provider.ApiKey), clientOptions)
            .GetChatClient(model);
        return client.AsIChatClient();
    }
}

/// <summary>
/// Harness-only factory wrapper that bounds calls made by every client in a
/// benchmark attempt and keeps a crash-recoverable, metadata-only ledger.
/// </summary>
internal sealed class BenchmarkMeteringFactory : IChatClientFactory
{
    private readonly IChatClientFactory _inner;
    private readonly BenchmarkMeter _meter;

    public BenchmarkMeteringFactory(
        IChatClientFactory inner,
        BenchmarkMeteringOptions options,
        string ledgerPath,
        Action<BenchmarkUsageSnapshot>? snapshotUpdated = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _meter = new BenchmarkMeter(options, ledgerPath, snapshotUpdated);
    }

    public BenchmarkUsageSnapshot Snapshot => _meter.Snapshot();
    public string? HaltReason => _meter.HaltReason;

    public IChatClient Create(
        LlmConfig config,
        AgentType role,
        string? projectId = null,
        RoleModel? modelOverride = null)
        => CreateWithRoleIdentity(config, role, role.ToString(), projectId, modelOverride);

    public IChatClient CreateWithRoleIdentity(
        LlmConfig config,
        AgentType role,
        string roleIdentity,
        string? projectId = null,
        RoleModel? modelOverride = null)
    {
        if (string.IsNullOrWhiteSpace(roleIdentity) || roleIdentity.Any(char.IsControl))
            throw new ArgumentException("Benchmark role identity must be nonempty metadata.", nameof(roleIdentity));
        return new BenchmarkMeteringChatClient(
            _inner.Create(config, role, projectId, modelOverride),
            _meter,
            roleIdentity,
            projectId);
    }

    /// <summary>
    /// Conservative whole-attempt reservation used by the driver. It assumes
    /// every permitted call consumes both declared per-call maxima and gives
    /// no refund for lower observed usage or failed calls.
    /// </summary>
    public static decimal CalculateReservedUsd(BenchmarkMeteringOptions options)
    {
        Validate(options);
        return options.MaxCalls *
            ((options.MaxInputTokensPerCall * options.InputUsdPerMillionTokens)
             + (options.MaxOutputTokensPerCall * options.OutputUsdPerMillionTokens))
            / 1_000_000m;
    }

    private static void Validate(BenchmarkMeteringOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.AttemptId))
            throw new ArgumentException("AttemptId is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ScenarioId))
            throw new ArgumentException("ScenarioId is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Provider))
            throw new ArgumentException("Provider is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (options.MaxCalls <= 0 || options.MaxInputTokensPerCall <= 0 || options.MaxOutputTokensPerCall <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "All benchmark limits must be positive.");
        if (options.InputUsdPerMillionTokens < 0 || options.OutputUsdPerMillionTokens < 0
            || options.CachedInputUsdPerMillionTokens is < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Benchmark rates cannot be negative.");
    }

    private sealed class BenchmarkMeter
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

        private readonly object _gate = new();
        private readonly BenchmarkMeteringOptions _options;
        private readonly string _ledgerPath;
        private readonly Action<BenchmarkUsageSnapshot>? _snapshotUpdated;
        private readonly List<BenchmarkCallRecord> _calls = [];
        private long _nextCallId;
        private string? _haltReason;

        public BenchmarkMeter(
            BenchmarkMeteringOptions options,
            string ledgerPath,
            Action<BenchmarkUsageSnapshot>? snapshotUpdated)
        {
            Validate(options);
            if (string.IsNullOrWhiteSpace(ledgerPath))
                throw new ArgumentException("A usage ledger path is required.", nameof(ledgerPath));
            _options = options;
            _ledgerPath = Path.GetFullPath(ledgerPath);
            _snapshotUpdated = snapshotUpdated;
            if (File.Exists(_ledgerPath))
            {
                throw new InvalidOperationException(
                    $"Benchmark usage ledger already exists: {_ledgerPath}. Refusing to overwrite crash-recovery accounting.");
            }
            Persist();
        }

        public BenchmarkUsageSnapshot Snapshot()
        {
            lock (_gate)
                return BuildSnapshot();
        }

        public string? HaltReason
        {
            get
            {
                lock (_gate) return _haltReason;
            }
        }

        public long BeginCall(IReadOnlyList<ChatMessage> messages, ChatOptions? requestedOptions, string role, string? projectId)
        {
            var estimatedInput = EstimateInputTokens(messages, requestedOptions);
            if (estimatedInput > _options.MaxInputTokensPerCall)
            {
                RejectForLimit(
                    $"Conservative input estimate {estimatedInput} exceeds the benchmark per-call limit " +
                    $"{_options.MaxInputTokensPerCall}. No provider call was made.");
            }

            lock (_gate)
            {
                if (_haltReason is not null)
                {
                    throw new BenchmarkLimitExceededException(
                        $"Benchmark metering is halted after a prior provider/accounting failure: {_haltReason}");
                }
                if (_calls.Count >= _options.MaxCalls)
                {
                    _haltReason = $"Benchmark call limit {_options.MaxCalls} has been reached.";
                    PersistLocked();
                    throw new BenchmarkLimitExceededException(
                        $"{_haltReason} No provider call was made.");
                }

                var id = ++_nextCallId;
                _calls.Add(new BenchmarkCallRecord(
                    id,
                    DateTimeOffset.UtcNow,
                    CompletedAt: null,
                    Role: role,
                    ProjectId: projectId,
                    Status: "in-flight",
                    EstimatedInputTokens: estimatedInput,
                    InputTokens: null,
                    OutputTokens: null,
                    CachedInputTokens: null,
                    CacheWriteInputTokens: null,
                    UsageMissing: true));
                // Persist the reservation before the request goes over the
                // wire. A crash can over-count an uncertain in-flight call,
                // but can never silently make that possibly-paid call free.
                PersistLocked();
                return id;
            }
        }

        private void RejectForLimit(string message)
        {
            lock (_gate)
            {
                _haltReason ??= message;
                PersistLocked();
            }
            throw new BenchmarkLimitExceededException(message);
        }

        public void CompleteCall(
            long id,
            UsageDetails? usage,
            string status,
            bool forceUsageMissing = false)
        {
            lock (_gate)
            {
                var index = _calls.FindIndex(c => c.CallId == id);
                if (index < 0) throw new InvalidOperationException($"Unknown benchmark call {id}.");
                var limitBreach = UsageLimitBreach(usage);
                var usageMissing = forceUsageMissing
                    || usage?.InputTokenCount is null
                    || usage.OutputTokenCount is null;
                _calls[index] = _calls[index] with
                {
                    CompletedAt = DateTimeOffset.UtcNow,
                    Status = limitBreach is null ? status : "limit-breached",
                    InputTokens = usage?.InputTokenCount,
                    OutputTokens = usage?.OutputTokenCount,
                    CachedInputTokens = ReadCachedInput(usage),
                    CacheWriteInputTokens = ReadAdditionalCount(usage, "cache_creation_input_tokens"),
                    UsageMissing = usageMissing,
                };
                _haltReason ??= limitBreach
                    ?? (string.Equals(status, "failed", StringComparison.Ordinal)
                        ? "provider call failed"
                        : usageMissing ? "provider usage accounting was incomplete" : null);
                PersistLocked();
                if (limitBreach is not null)
                {
                    throw new BenchmarkLimitExceededException(
                        $"{limitBreach} The provider call had already completed; later calls are blocked.");
                }
            }
        }

        private string? UsageLimitBreach(UsageDetails? usage)
        {
            if (usage?.InputTokenCount is { } input && input > _options.MaxInputTokensPerCall)
                return $"Provider reported {input} input tokens, above the benchmark limit {_options.MaxInputTokensPerCall}.";
            if (usage?.OutputTokenCount is { } output && output > _options.MaxOutputTokensPerCall)
                return $"Provider reported {output} output tokens, above the benchmark limit {_options.MaxOutputTokensPerCall}.";
            return null;
        }

        public ChatOptions CappedOptions(ChatOptions? requested)
        {
            var capped = requested?.Clone() ?? new ChatOptions();
            capped.MaxOutputTokens = Math.Min(
                requested?.MaxOutputTokens ?? _options.MaxOutputTokensPerCall,
                _options.MaxOutputTokensPerCall);
            return capped;
        }

        private void Persist()
        {
            lock (_gate) PersistLocked();
        }

        private void PersistLocked()
        {
            var directory = Path.GetDirectoryName(_ledgerPath)
                ?? throw new InvalidOperationException("Usage ledger has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = _ledgerPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(BuildLedger(), JsonOptions), Encoding.UTF8);
            File.Move(temporaryPath, _ledgerPath, overwrite: true);
            _snapshotUpdated?.Invoke(BuildSnapshot());
        }

        private BenchmarkUsageLedger BuildLedger() => new(
            SchemaVersion: 1,
            AttemptId: _options.AttemptId,
            ScenarioId: _options.ScenarioId,
            Provider: _options.Provider,
            Model: _options.Model,
            MaxCalls: _options.MaxCalls,
            MaxInputTokensPerCall: _options.MaxInputTokensPerCall,
            MaxOutputTokensPerCall: _options.MaxOutputTokensPerCall,
            InputUsdPerMillionTokens: _options.InputUsdPerMillionTokens,
            OutputUsdPerMillionTokens: _options.OutputUsdPerMillionTokens,
            CachedInputUsdPerMillionTokens: _options.CachedInputUsdPerMillionTokens,
            ReservedUsd: CalculateReservedUsd(_options),
            Snapshot: BuildSnapshot(),
            Calls: [.. _calls]);

        private BenchmarkUsageSnapshot BuildSnapshot()
        {
            var input = _calls.Sum(c => c.InputTokens ?? 0);
            var output = _calls.Sum(c => c.OutputTokens ?? 0);
            var cached = _calls.Sum(c => c.CachedInputTokens ?? 0);
            var cacheWrite = _calls.Sum(c => c.CacheWriteInputTokens ?? 0);
            var uncachedInput = Math.Max(0, input - cached);
            var cachedRate = _options.CachedInputUsdPerMillionTokens ?? _options.InputUsdPerMillionTokens;
            var knownUsageEstimatedUsd =
                ((uncachedInput * _options.InputUsdPerMillionTokens)
                 + (cached * cachedRate)
                 + (output * _options.OutputUsdPerMillionTokens)) / 1_000_000m;
            return new BenchmarkUsageSnapshot(
                Calls: _calls.Count,
                CompletedCalls: _calls.Count(c => c.Status == "completed"),
                FailedCalls: _calls.Count(c => c.Status is "failed" or "limit-breached"),
                InFlightCalls: _calls.Count(c => c.Status == "in-flight"),
                MissingUsageCalls: _calls.Count(c => c.UsageMissing),
                InputTokens: input,
                OutputTokens: output,
                CachedInputTokens: cached,
                CacheWriteInputTokens: cacheWrite,
                KnownUsageEstimatedUsd: knownUsageEstimatedUsd,
                EstimatedCostUsd: _calls.All(c => !c.UsageMissing) ? knownUsageEstimatedUsd : null,
                AccountingComplete: _calls.All(c => !c.UsageMissing));
        }

        private static int EstimateInputTokens(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
        {
            long bytes = 0;
            foreach (var message in messages)
            {
                bytes += 16; // role and wire-format framing
                bytes += Utf8Bytes(message.AuthorName);
                foreach (var content in message.Contents)
                    bytes += EstimateContentBytes(content);
            }

            if (options?.Tools is { Count: > 0 } tools)
            {
                foreach (var tool in tools)
                {
                    bytes += 32 + Utf8Bytes(tool.Name) + Utf8Bytes(tool.Description);
                    if (tool is AIFunctionDeclaration function)
                    {
                        bytes += Encoding.UTF8.GetByteCount(function.JsonSchema.GetRawText());
                        if (function.ReturnJsonSchema is { } returnSchema)
                            bytes += Encoding.UTF8.GetByteCount(returnSchema.GetRawText());
                    }
                }
            }

            // One token per UTF-8 byte is intentionally pessimistic for text
            // tokenizers. It is a local admission estimate, not billable usage.
            return checked((int)Math.Min(bytes, int.MaxValue));
        }

        private static long EstimateContentBytes(AIContent content) => content switch
        {
            TextContent text => Utf8Bytes(text.Text),
            FunctionCallContent call => 32 + Utf8Bytes(call.CallId) + Utf8Bytes(call.Name)
                + SerializedUtf8Bytes(call.Arguments),
            FunctionResultContent result => 32 + Utf8Bytes(result.CallId)
                + SerializedUtf8Bytes(result.Result),
            DataContent => throw new BenchmarkLimitExceededException(
                "Live benchmark input contains binary/media content whose provider token cost cannot be conservatively estimated."),
            UriContent => throw new BenchmarkLimitExceededException(
                "Live benchmark input contains URI content whose fetched provider token cost cannot be conservatively estimated."),
            _ => SerializedUtf8Bytes(content),
        };

        private static int Utf8Bytes(string? value) =>
            string.IsNullOrEmpty(value) ? 0 : Encoding.UTF8.GetByteCount(value);

        private static int SerializedUtf8Bytes(object? value)
        {
            if (value is null) return 0;
            try
            {
                return JsonSerializer.SerializeToUtf8Bytes(value, value.GetType()).Length;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new BenchmarkLimitExceededException(
                    $"Cannot conservatively size benchmark input content of type {value.GetType().Name}.", ex);
            }
        }

        private static long? ReadCachedInput(UsageDetails? usage) =>
            usage?.CachedInputTokenCount
            ?? ReadAdditionalCount(usage, "cache_read_input_tokens");

        private static long? ReadAdditionalCount(UsageDetails? usage, string key) =>
            usage?.AdditionalCounts?.TryGetValue(key, out var value) == true ? value : null;
    }

    private sealed class BenchmarkMeteringChatClient(
        IChatClient inner,
        BenchmarkMeter meter,
        string role,
        string? projectId) : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
            var callId = meter.BeginCall(materialized, options, role, projectId);
            ChatResponse response;
            try
            {
                response = await InnerClient.GetResponseAsync(
                    materialized, meter.CappedOptions(options), cancellationToken);
            }
            catch
            {
                meter.CompleteCall(callId, usage: null, "failed");
                throw;
            }
            meter.CompleteCall(callId, response.Usage, "completed");
            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
            var callId = meter.BeginCall(materialized, options, role, projectId);
            UsageDetails? usage = null;
            var terminalRecorded = false;
            try
            {
                await foreach (var update in InnerClient.GetStreamingResponseAsync(
                    materialized, meter.CappedOptions(options), cancellationToken))
                {
                    foreach (var usageContent in update.Contents.OfType<UsageContent>())
                    {
                        usage ??= new UsageDetails();
                        usage.Add(usageContent.Details);
                    }
                    yield return update;
                }
                terminalRecorded = true;
                meter.CompleteCall(callId, usage, "completed");
            }
            finally
            {
                // If enumeration throws or the caller abandons it, the
                // outbound request still counts. Keep any usage received
                // before the failure; absent or partial usage stays unknown.
                if (!terminalRecorded)
                    meter.CompleteCall(callId, usage, "failed", forceUsageMissing: true);
            }
        }
    }
}

internal sealed record BenchmarkUsageSnapshot(
    int Calls,
    int CompletedCalls,
    int FailedCalls,
    int InFlightCalls,
    int MissingUsageCalls,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long CacheWriteInputTokens,
    decimal KnownUsageEstimatedUsd,
    decimal? EstimatedCostUsd,
    bool AccountingComplete);

internal sealed record BenchmarkCallRecord(
    long CallId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Role,
    string? ProjectId,
    string Status,
    int EstimatedInputTokens,
    long? InputTokens,
    long? OutputTokens,
    long? CachedInputTokens,
    long? CacheWriteInputTokens,
    bool UsageMissing);

internal sealed record BenchmarkUsageLedger(
    int SchemaVersion,
    string AttemptId,
    string ScenarioId,
    string Provider,
    string Model,
    int MaxCalls,
    int MaxInputTokensPerCall,
    int MaxOutputTokensPerCall,
    decimal InputUsdPerMillionTokens,
    decimal OutputUsdPerMillionTokens,
    decimal? CachedInputUsdPerMillionTokens,
    decimal ReservedUsd,
    BenchmarkUsageSnapshot Snapshot,
    BenchmarkCallRecord[] Calls);

internal sealed class BenchmarkLimitExceededException : InvalidOperationException
{
    public BenchmarkLimitExceededException(string message) : base(message) { }
    public BenchmarkLimitExceededException(string message, Exception innerException) : base(message, innerException) { }
}
