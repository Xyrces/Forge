namespace Forge.Core.Execution;

/// <summary>Measured boundaries; model time includes tools and is not inference-only latency.</summary>
public sealed record WorkerPhaseTiming(DateTimeOffset? ModelStartedAt, DateTimeOffset? ModelCompletedAt,
    DateTimeOffset? VerificationStartedAt = null, DateTimeOffset? VerificationCompletedAt = null,
    DateTimeOffset? EligibleAt = null, DateTimeOffset? WorkerStartedAt = null,
    string? ModelOutcome = null, string? VerificationOutcome = null,
    DateTimeOffset? WorkerCompletedAt = null, string? WorkerOutcome = null,
    DispatchWakeupTiming? QueueWakeup = null);
