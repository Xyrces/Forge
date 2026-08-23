namespace Forge.Core.Messaging;

/// <summary>
/// Internal coordination event contracts. Pure records, no dependencies.
/// Messages are HINTS, not truth: every consumer re-reads DB state and
/// is idempotent. MessageId is deterministic (natural key + occurrence
/// anchor) so transport-level idempotency dedupes double-publication.
/// Natural keys are ALWAYS project-qualified: task ids, follow-up rowids
/// and PR numbers are per-project sequences and collide across projects
/// on the shared topic.
/// </summary>
public sealed record TaskEnqueued : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public required string TaskId { get; init; }
    public string? TaskType { get; init; }
    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(string projectId, string taskId, DateTimeOffset enqueuedAt)
        => $"enqueued:{projectId}:{taskId}:{enqueuedAt:O}";
}

public sealed record TaskTransitioned : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public required string TaskId { get; init; }
    public required TaskLifecycleState FromState { get; init; }
    public required TaskLifecycleState ToState { get; init; }
    public DateTimeOffset StateEnteredAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(string projectId, string taskId, TaskLifecycleState toState, DateTimeOffset stateEnteredAt)
        => $"transition:{projectId}:{taskId}:{toState}:{stateEnteredAt:O}";
}

public sealed record PrOpened : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public required string TaskId { get; init; }
    public required int PrNumber { get; init; }
    public string? Branch { get; init; }
    public DateTimeOffset OpenedAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(string projectId, string taskId, int prNumber, string? headSha = null)
        => $"pr-opened:{projectId}:{taskId}:{prNumber}:{headSha ?? "none"}";
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

    public static string IdFor(string projectId, string taskId, int prNumber, string? reviewSha, int reviewRound)
        => $"review-verdict:{projectId}:{taskId}:{prNumber}:{reviewSha ?? "none"}:{reviewRound}";
}

public sealed record SpecStatusChanged : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public required string SpecId { get; init; }
    public required string FromStatus { get; init; }
    public required string ToStatus { get; init; }
    public DateTimeOffset ChangedAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(string projectId, string specId, string toStatus, DateTimeOffset changedAt)
        => $"spec-status:{projectId}:{specId}:{toStatus}:{changedAt:O}";
}

public sealed record SprintStatusChanged : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public required string SprintId { get; init; }
    public required string FromStatus { get; init; }
    public required string ToStatus { get; init; }
    public DateTimeOffset ChangedAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(string projectId, string sprintId, string toStatus, DateTimeOffset changedAt)
        => $"sprint-status:{projectId}:{sprintId}:{toStatus}:{changedAt:O}";
}

public sealed record FollowUpFiled : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public required long FollowUpId { get; init; }
    public string? FollowUpOfTaskId { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset FiledAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(string projectId, long followUpId)
        => $"followup:{projectId}:{followUpId}";
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

/// <summary>
/// Operator changed a project's role caps (PUT /api/projects/{id}/roles
/// applies them live to the SlotTable). The dispatch loop must wake NOW
/// to exploit freed capacity — no store mutation fires otherwise.
/// </summary>
public sealed record ProjectRolesChanged : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public DateTimeOffset ChangedAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(string projectId, DateTimeOffset changedAt)
        => $"roles-changed:{projectId}:{changedAt:O}";
}

/// <summary>Which ledger transition a <see cref="TaskFailureSignal"/> hints at.</summary>
public enum FailureSignalKind
{
    /// <summary>Task entered Failed or Blocked.</summary>
    Failure,
    /// <summary>Task left Failed/Blocked for Pending/InProgress/Closed.</summary>
    Clearance,
    /// <summary>Task reached a dispatch-success lifecycle state (PROpen
    /// or later) — resolves a cleared row's pending outcome.</summary>
    SuccessCandidate,
}

/// <summary>
/// Failure-ledger hint (triage phase 1): published from the IssueStore
/// transition choke points when a task enters/leaves a failure status or
/// reaches a dispatch-success lifecycle state. Own topic — the in-memory
/// transport is competing-consumer, so this cannot ride TaskTransitioned.
/// The consumer re-reads the task + ledger rows (DB truth) and is
/// idempotent; the signal only routes the handler branch.
/// </summary>
public sealed record TaskFailureSignal : IForgeEvent
{
    public required string MessageId { get; init; }
    public required string ProjectId { get; init; }
    public required string TaskId { get; init; }
    public required FailureSignalKind Kind { get; init; }
    public required string FromStatus { get; init; }
    public required string ToStatus { get; init; }
    /// <summary>The transition's error/reason text (≤300 chars) — the
    /// freshest description of THIS failure; the classifier prefers it
    /// over the task's persisted lastError.</summary>
    public string? ErrorExcerpt { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    public static string IdFor(string projectId, string taskId, FailureSignalKind kind, DateTimeOffset occurredAt)
        => $"failure-signal:{projectId}:{taskId}:{kind}:{occurredAt:O}";
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
