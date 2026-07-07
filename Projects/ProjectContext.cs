using System.Collections.Concurrent;
using Forge.Configuration;
using Forge.Core;

namespace Forge.Projects;

/// <summary>
/// Read-only services bundle for a single project. v1 builds these lazily
/// on first dashboard request. The orchestrator dispatch loop still uses
/// the legacy single-workspace path; the multi-project surface is for
/// dashboard introspection + slot accounting.
/// </summary>
public sealed class ProjectContext : IAsyncDisposable
{
    public ProjectOptions Options { get; }

    private readonly IssueStore _issues;

    public ProjectContext(ProjectOptions options, IssueStore issues)
    {
        Options = options;
        _issues = issues;
        _issues.EnsureSchema();
    }

    public IIssueStore Issues => _issues;

    public async Task<int> CountByStatusAsync(IssueStatus status, CancellationToken ct)
    {
        var rows = await _issues.ListAsync(new IssueFilter { Status = status }, ct);
        return rows.Count;
    }

    public ValueTask DisposeAsync()
    {
        if (_issues is IAsyncDisposable d) return d.DisposeAsync();
        _issues.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Builds <see cref="ProjectContext"/> instances lazily and caches them
/// for the lifetime of the dashboard host. One process can hold many
/// projects in memory simultaneously; each owns its own
/// <see cref="IssueStore"/> backed by its own SQLite file. Accepts the
/// full bootstrap result list so the SQLite path is whatever the
/// bootstrap allocated (not re-derived from the operator's input).
/// </summary>
public sealed class ProjectContextFactory : IAsyncDisposable
{
    private readonly IReadOnlyList<ProjectOptions> _projects;
    private readonly IReadOnlyDictionary<string, string> _issuesDbByProject;
    private readonly ConcurrentDictionary<string, ProjectContext> _cache = new();
    private bool _disposed;

    public ProjectContextFactory(
        IReadOnlyList<ProjectOptions> projects,
        IReadOnlyDictionary<string, string>? issuesDbByProject = null)
    {
        _projects = projects;
        _issuesDbByProject = issuesDbByProject
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ProjectOptions> KnownProjects => _projects;

    public ProjectContext? Find(string projectId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProjectContextFactory));
        if (_cache.TryGetValue(projectId, out var ctx)) return ctx;
        var opts = _projects.FirstOrDefault(p =>
            string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
        if (opts is null) return null;
        var dbPath = _issuesDbByProject.TryGetValue(projectId, out var assigned)
            ? assigned
            : ProjectStateDirs.IssuesDbFor(opts);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var ctx2 = new ProjectContext(opts, new IssueStore(dbPath));
        return _cache.GetOrAdd(projectId, ctx2);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var ctx in _cache.Values) await ctx.DisposeAsync();
        _cache.Clear();
    }
}

