namespace Forge.Core.Messaging;

/// <summary>
/// Internal coordination event contracts. Pure records, no dependencies.
/// Messages are HINTS, not truth: every consumer re-reads DB state and
/// is idempotent. MessageId is deterministic (natural key + occurrence
/// anchor) so transport-level idempotency dedupes double-publication.
/// </summary>
public sealed record TaskEnqueued : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public required string TaskId { get; init; }
    public string? TaskType { get; init; }
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(string taskId, DateTimeOffset enqueuedAt)
        => $"enqueued:{taskId}:{enqueuedAt:O}";
}

public sealed record TaskTransitioned : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public required string TaskId { get; init; }
    public required TaskLifecycleState FromState { get; init; }
    public required TaskLifecycleState ToState { get; init; }
    public DateTimeOffset StateEnteredAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(string taskId, TaskLifecycleState toState, DateTimeOffset stateEnteredAt)
        => $"transition:{taskId}:{toState}:{stateEnteredAt:O}";
}

public sealed record PrOpened : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public required string TaskId { get; init; }
    public required int PrNumber { get; init; }
    public string? Branch { get; init; }
    public DateTimeOffset OpenedAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(string taskId, int prNumber, string? headSha = null)
        => $"pr-opened:{taskId}:{prNumber}:{headSha ?? "none"}";
}

public sealed record ReviewVerdictRecorded : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public required string TaskId { get; init; }
    public required int PrNumber { get; init; }
    public required string Verdict { get; init; }
    public string? ReviewSha { get; init; }
    public int ReviewRound { get; init; }
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(string taskId, int prNumber, string? reviewSha, int reviewRound)
        => $"review-verdict:{taskId}:{prNumber}:{reviewSha ?? "none"}:{reviewRound}";
}

public sealed record SpecStatusChanged : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public required string SpecId { get; init; }
    public required string FromStatus { get; init; }
    public required string ToStatus { get; init; }
    public DateTimeOffset ChangedAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(string specId, string toStatus, DateTimeOffset changedAt)
        => $"spec-status:{specId}:{toStatus}:{changedAt:O}";
}

public sealed record SprintStatusChanged : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public required string SprintId { get; init; }
    public required string FromStatus { get; init; }
    public required string ToStatus { get; init; }
    public DateTimeOffset ChangedAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(string sprintId, string toStatus, DateTimeOffset changedAt)
        => $"sprint-status:{sprintId}:{toStatus}:{changedAt:O}";
}

public sealed record FollowUpFiled : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public required long FollowUpId { get; init; }
    public string? FollowUpOfTaskId { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset FiledAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(long followUpId)
        => $"followup:{followUpId}";
}

public sealed record GroomRequested : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public string? SpecId { get; init; }
    public string? TaskId { get; init; }
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(string projectId, string? specId, string? taskId, DateTimeOffset requestedAt)
        => $"groom:{projectId}:{specId ?? "-"}:{taskId ?? "-"}:{requestedAt:O}";
}

/// <summary>Which scheduler a <see cref="SweepTick"/> backstop is for.</summary>
public enum SweepKind
{
    Watch,
    Groom,
    Design,
    Artist,
    Assemble,
}

/// <summary>
/// Periodic backstop tick (15m). Triggered work stays correct if every
/// hint event is lost — ticks re-derive everything from DB/GitHub truth.
/// </summary>
public sealed record SweepTick : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public required SweepKind Kind { get; init; }
    public DateTimeOffset TickAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(SweepKind kind, string projectId, DateTimeOffset tickAt)
        => $"sweep:{kind}:{projectId}:{tickAt:O}";
}
