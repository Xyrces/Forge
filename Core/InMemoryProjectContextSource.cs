namespace PortHorizon.Agents.Core;

/// <summary>
/// Test-only <see cref="IProjectContextSource"/> that returns a
/// pre-built <see cref="ProjectContext"/>. Avoids filesystem +
/// database setup in unit tests.
/// </summary>
public sealed class InMemoryProjectContextSource : IProjectContextSource
{
    private readonly ProjectContext _context;
    public InMemoryProjectContextSource(ProjectContext context) { _context = context; }
    public Task<ProjectContext> BuildAsync(string projectId, CancellationToken ct = default)
        => Task.FromResult(_context with { ProjectId = projectId });
}