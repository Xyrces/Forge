using Forge.Core.Messaging;

namespace Forge.Messaging;

/// <summary>
/// Topic-per-event-type naming. Transport-agnostic constants shared by
/// the publisher and every consumer; DLQ topics are the transport's
/// own (<c>{topic}.dlq</c> for the in-memory transport).
/// </summary>
public static class Topics
{
    public const string TaskEnqueued = "forge.task-enqueued";
    public const string TaskTransitioned = "forge.task-transitioned";
    public const string PrOpened = "forge.pr-opened";
    public const string ReviewVerdictRecorded = "forge.review-verdict";
    public const string SpecStatusChanged = "forge.spec-status-changed";
    public const string FollowUpFiled = "forge.followup-filed";
    public const string GroomRequested = "forge.groom-requested";
    public const string SweepTick = "forge.sweep-tick";

    public static string For<T>() where T : IForgeEvent => typeof(T).Name switch
    {
        nameof(Core.Messaging.TaskEnqueued) => TaskEnqueued,
        nameof(Core.Messaging.TaskTransitioned) => TaskTransitioned,
        nameof(Core.Messaging.PrOpened) => PrOpened,
        nameof(Core.Messaging.ReviewVerdictRecorded) => ReviewVerdictRecorded,
        nameof(Core.Messaging.SpecStatusChanged) => SpecStatusChanged,
        nameof(Core.Messaging.FollowUpFiled) => FollowUpFiled,
        nameof(Core.Messaging.GroomRequested) => GroomRequested,
        nameof(Core.Messaging.SweepTick) => SweepTick,
        var name => throw new ArgumentException($"No topic mapped for event type {name}", nameof(T)),
    };
}
