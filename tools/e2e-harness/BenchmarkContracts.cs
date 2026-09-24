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
    public string? Error { get; set; }

    [JsonIgnore]
    public BenchmarkMeteringFactory? Meter { get; set; }
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

[JsonSerializable(typeof(BenchmarkResult))]
[JsonSerializable(typeof(GraderReport))]
[JsonSerializable(typeof(BenchmarkResultUsage))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
