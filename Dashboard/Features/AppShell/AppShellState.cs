using Fluxor;

namespace Forge.Dashboard.Features.AppShell;

[FeatureState]
public sealed record AppShellState
{
    public string? ActiveSprintId { get; init; }
    public string? ActiveSprintName { get; init; }
    public string HeartbeatStatus { get; init; } = "unknown";
    public DateTime? LastHeartbeatAt { get; init; }
    public bool LiveFeedOpen { get; init; }
    public string? CurrentRoute { get; init; }
    public IReadOnlyList<LiveFeedEntry> LiveFeed { get; init; } = Array.Empty<LiveFeedEntry>();
}

public sealed record LiveFeedEntry(DateTime At, string Kind, string? TaskId, string? Detail);

public static class AppShellActions
{
    public sealed record LoadActiveSprintAction();
    public sealed record ActiveSprintLoadedAction(string? Id, string? Name);

    public sealed record PollHeartbeatAction();
    public sealed record HeartbeatUpdatedAction(string Status, DateTime At);

    public sealed record ToggleLiveFeedAction();
    public sealed record NavigateAction(string Route);

    public sealed record PushLiveFeedEventAction(LiveFeedEntry Entry);
}