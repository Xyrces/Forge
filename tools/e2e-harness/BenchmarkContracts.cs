using System.Text.Json.Serialization;

namespace Forge.Tools.E2E;

internal sealed record BenchmarkCheck(string Name, bool Passed, string Detail);

internal sealed record BenchmarkResult
{
    public int Version { get; init; } = 1;
    public string CaseId { get; init; } = "unknown";
    public string Mode { get; init; } = "unknown";
    public bool Success { get; set; }
    public string Outcome { get; set; } = "failed";
    public double ElapsedSeconds { get; set; }
    public string? TaskStatus { get; set; }
    public int ReworkAttempts { get; set; }
    public bool? GateFailed { get; set; }
    public List<BenchmarkCheck> Checks { get; } = [];
    public object? Usage { get; set; }
    public string Scope { get; init; } = "engineering-with-simulated-review";
    public string? Model { get; init; }
    public string? Provider { get; init; }
    public string? PolicyId { get; init; }
    public Dictionary<string, BenchmarkModelUsage> ModelUsage { get; } = new(StringComparer.Ordinal);
    public List<BenchmarkStageAttempt> Attempts { get; } = [];
    public string? ReviewVerdict { get; set; }
    public bool Escalated { get; set; }
    public bool GenerationSuccess { get; set; }
    public string? ExternalEvaluation { get; set; }
    public string? SourceBaseCommit { get; set; }
    public string? ProducedHeadSha { get; set; }
    public string? PatchPath { get; set; }
    public string? PatchSha256 { get; set; }
    public string? Error { get; set; }

    [JsonIgnore]
    public BenchmarkMeteringFactory? Meter { get; set; }

    [JsonIgnore]
    public BenchmarkPolicyRuntime? PolicyRuntime { get; set; }
}

internal sealed record GraderReport(IReadOnlyList<BenchmarkCheck> Checks);

internal sealed record BenchmarkResultUsage(
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
    bool AccountingComplete)
{
    public static BenchmarkResultUsage From(BenchmarkUsageSnapshot snapshot) => new(
        snapshot.Calls,
        snapshot.CompletedCalls,
        snapshot.FailedCalls,
        snapshot.InFlightCalls,
        snapshot.MissingUsageCalls,
        snapshot.InputTokens,
        snapshot.OutputTokens,
        snapshot.CachedInputTokens,
        snapshot.CacheWriteInputTokens,
        snapshot.KnownUsageEstimatedUsd,
        snapshot.EstimatedCostUsd,
        snapshot.AccountingComplete);
}

internal sealed record BenchmarkModelUsage(
    string Provider,
    string Model,
    BenchmarkResultUsage Usage);

internal sealed record BenchmarkStageAttempt(
    int Attempt,
    string Stage,
    string? ModelId,
    string? Provider,
    string? Model,
    bool? Success,
    string Reason,
    string? HeadSha,
    string? ReviewVerdict,
    bool Escalated,
    IReadOnlyList<BenchmarkCheck>? Checks = null);

internal sealed record BenchmarkPlanCriticAudit(
    bool? Success,
    string Outcome,
    string Detail,
    bool Observed);

[JsonSerializable(typeof(BenchmarkResult))]
[JsonSerializable(typeof(GraderReport))]
[JsonSerializable(typeof(BenchmarkResultUsage))]
[JsonSerializable(typeof(BenchmarkModelUsage))]
[JsonSerializable(typeof(BenchmarkStageAttempt))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
