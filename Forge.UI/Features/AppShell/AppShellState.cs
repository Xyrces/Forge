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

    /// <summary>Registered projects, loaded from /api/projects.</summary>
    public IReadOnlyList<ProjectListEntry> Projects { get; init; } = Array.Empty<ProjectListEntry>();

    /// <summary>
    /// The project the dashboard is currently scoped to. Every
    /// project-aware page (Tasks, Specs, Designs, Art, Backlog,
    /// the sprint pill) reads through this lens. Persisted in
    /// localStorage so a refresh keeps the selection.
    /// </summary>
    public string? CurrentProjectId { get; init; }
}

/// <summary>Lean project entry for the topbar switcher.</summary>
public sealed record ProjectListEntry(string Id, string Name);

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

    /// <summary>Load the project list + resolve the current selection (persisted &gt; first).</summary>
    public sealed record LoadProjectsAction();
    public sealed record ProjectsLoadedAction(IReadOnlyList<ProjectListEntry> Projects, string? CurrentProjectId);

    /// <summary>User picked a project in the topbar switcher.</summary>
    public sealed record SelectProjectAction(string ProjectId);
}