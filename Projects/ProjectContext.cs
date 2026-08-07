using System.Collections.Concurrent;
using Forge.Configuration;
using Forge.Core;
using Forge.Deploy;

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
    private readonly Lazy<DeploymentStore> _deployments;
    private readonly Lazy<SpecStore> _specs;
    private readonly Lazy<SprintStore> _sprints;

    public ProjectContext(ProjectOptions options, IssueStore issues)
    {
        Options = options;
        _issues = issues;
        _issues.EnsureSchema();
        // Deployment rows live in the same store/schema as issues
        // (v15 migration, applied above by EnsureSchema); build the
        // typed layer over the SAME connection factory — DbPath is
        // empty under the SQL Server provider, so a path-based
        // construction would silently fabricate a SQLite store.
        _deployments = new Lazy<DeploymentStore>(() => new DeploymentStore(_issues.Db));
        // Spec + sprint rows also live in the issues sqlite file
        // (created by EnsureSchema); both stores are typed layers
        // over the same IssueStore instance.
        _specs = new Lazy<SpecStore>(() => new SpecStore(_issues));
        _sprints = new Lazy<SprintStore>(() => new SprintStore(_issues));
    }

    public IIssueStore Issues => _issues;
    public DeploymentStore Deployments => _deployments.Value;
    public ISpecStore Specs => _specs.Value;
    public ISprintStore Sprints => _sprints.Value;

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
/// <see cref="IssueStore"/> backed by its own SQLite file.
///
/// <para>
/// Two modes:
/// <list type="bullet">
///   <item><b>Static mode</b> (legacy): pass an in-memory project list at
///   construction. <see cref="KnownProjects"/> is fixed for the
///   process lifetime.</item>
///   <item><b>Live mode</b>: pass an <see cref="IProjectStore"/>. Every
///   <see cref="KnownProjects"/> call re-reads from the store, so
///   <c>POST /api/projects</c> + <c>DELETE /api/projects/{id}</c> are
///   reflected on the next request without a restart.</item>
/// </list>
/// The orchestrator's dispatch loop still picks a single primary
/// project at startup (the first row); runtime adds affect dashboard
/// introspection (slot table counters, <c>/api/board</c>,
/// <c>/api/projects</c>) but NOT dispatch routing — which is a
/// deliberate v1 limitation. For the "build forge with forge" first
/// goal, the <c>forge</c> project should be listed in
/// <c>appsettings.json</c> so it's wired into dispatch at startup.
/// </para>
/// </summary>
public sealed class ProjectContextFactory : IAsyncDisposable
{
    private readonly IReadOnlyList<ProjectOptions>? _staticProjects;
    private readonly IProjectStore? _store;
    private readonly IReadOnlyDictionary<string, string> _issuesDbByProject;
    private readonly string _dataRoot;
    private readonly Func<string, string, Core.Db.IDbConnectionFactory>? _dbResolver;
    private readonly ConcurrentDictionary<string, ProjectContext> _cache = new();
    private bool _disposed;

    /// <summary>Static (legacy) construction. <see cref="KnownProjects"/> is fixed.</summary>
    public ProjectContextFactory(
        IReadOnlyList<ProjectOptions> projects,
        IReadOnlyDictionary<string, string>? issuesDbByProject = null,
        Func<string, string, Core.Db.IDbConnectionFactory>? dbResolver = null)
        : this(projects, store: null, dataRoot: null, issuesDbByProject, dbResolver)
    {
    }

    /// <summary>Live construction. <see cref="KnownProjects"/> re-reads the store on each call.</summary>
    public ProjectContextFactory(
        IProjectStore store,
        string dataRoot,
        IReadOnlyDictionary<string, string>? issuesDbByProject = null,
        Func<string, string, Core.Db.IDbConnectionFactory>? dbResolver = null)
        : this(projects: null, store: store, dataRoot: dataRoot, issuesDbByProject, dbResolver)
    {
    }

    private ProjectContextFactory(
        IReadOnlyList<ProjectOptions>? projects,
        IProjectStore? store,
        string? dataRoot,
        IReadOnlyDictionary<string, string>? issuesDbByProject,
        Func<string, string, Core.Db.IDbConnectionFactory>? dbResolver = null)
    {
        if (projects is null && store is null)
            throw new ArgumentException("Either projects (static) or store (live) must be supplied.");
        _staticProjects = projects;
        _store = store;
        _dataRoot = dataRoot ?? ForgesystemPaths.ResolveDataRoot();
        _issuesDbByProject = issuesDbByProject
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _dbResolver = dbResolver;
    }

    public IReadOnlyList<ProjectOptions> KnownProjects
    {
        get
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ProjectContextFactory));
            if (_staticProjects is not null) return _staticProjects;
            // Live mode: re-read the store on each access so POST /
            // DELETE show up immediately. ListAsync is a single
            // SELECT + materialization, cheap enough for the dashboard
            // cadence. Cache is per-project IssueStore.
            var rows = _store!.ListAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            var list = new List<ProjectOptions>(rows.Count);
            foreach (var r in rows)
            {
                // Root: the clone path recorded at registration
                // (local_path), else the canonical clone location
                // <dataRoot>/projects/<id> that ProjectBootstrap uses.
                var root = !string.IsNullOrWhiteSpace(r.LocalPath)
                    ? r.LocalPath!
                    : ForgesystemPaths.ProjectDir(_dataRoot, r.Id);
                list.Add(new ProjectOptions
                {
                    Id = r.Id,
                    Name = r.Name,
                    RepoUrl = r.RepoUrl,
                    DefaultBranch = r.DefaultBranch,
                    Root = root,
                    Roles = new Dictionary<string, int>(r.Roles, StringComparer.OrdinalIgnoreCase),
                    Territories = new Dictionary<string, Core.RoleTerritory>(r.Territories, StringComparer.OrdinalIgnoreCase),
                    VerifyCommands = r.VerifyCommands?.ToList(),
                });
            }
            return list;
        }
    }

    public ProjectContext? Find(string projectId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProjectContextFactory));
        if (_cache.TryGetValue(projectId, out var ctx)) return ctx;
        var opts = KnownProjects.FirstOrDefault(p =>
            string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase));
        if (opts is null) return null;
        var dbPath = _issuesDbByProject.TryGetValue(projectId, out var assigned)
            ? assigned
            : ProjectStateDirs.IssuesDbFor(opts, _dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var store = _dbResolver is not null
            ? new IssueStore(_dbResolver(projectId, dbPath))
            : new IssueStore(dbPath);
        var ctx2 = new ProjectContext(opts, store);
        return _cache.GetOrAdd(projectId, ctx2);
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var ctx in _cache.Values) await ctx.DisposeAsync();
        _cache.Clear();
    }
}

