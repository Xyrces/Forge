namespace Forge.Core.Execution;

/// <summary>Measured boundaries; model time includes tools and is not inference-only latency.</summary>
public sealed record WorkerPhaseTiming(DateTimeOffset ModelStartedAt, DateTimeOffset ModelCompletedAt,
    DateTimeOffset? VerificationStartedAt = null, DateTimeOffset? VerificationCompletedAt = null);
