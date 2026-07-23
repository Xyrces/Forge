using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Reviewer;
using Forge.Projects;
using Microsoft.Extensions.Logging;

namespace Forge.Orchestrator;

/// <summary>
/// Per-project dispatch bundle. Holds the SQLite + git + GitHub
/// services scoped to a single registered project. The dispatch
/// loop maintains one of these per project ID, lazy-constructed on
/// first dispatch cycle that sees the project.
///
/// <para>
/// All fields are per-project — never share a bundle across
/// projects. The shared dispatch infrastructure (MafAgentRunner,
/// RoleAgentRegistry, IWorkflowDispatcher, IDashboardEventBus,
/// AgentMessageBus) lives in <see cref="OrchestratorAgent"/> and is
/// passed in to <see cref="OrchestratorAgent.DispatchSingleTaskAsync"/>
/// as the caller.
/// </para>
/// </summary>
public sealed class ProjectDispatchBundle : IAsyncDisposable
{
    public ProjectOptions Project { get; }
    public IssueStore IssueStore { get; }
    public IAgentStore Agents { get; }
    public ISprintStore Sprints { get; }
    public DesignArtifactStore DesignArtifacts { get; }
    public ArtOutputStore ArtOutputs { get; }
    public GitWorktreeService Worktrees { get; }
    public GitHubService GitHub { get; }
    public PRWatcher PrWatcher { get; }
    public IDashboardEventBus Events { get; }
    public ILogger Logger { get; }
    public bool Disposed { get; private set; }

    public ProjectDispatchBundle(
        ProjectOptions project,
        IssueStore issueStore,
        IAgentStore agents,
        ISprintStore sprints,
        DesignArtifactStore designArtifacts,
        ArtOutputStore artOutputs,
        GitWorktreeService worktrees,
        GitHubService gitHub,
        PRWatcher prWatcher,
        IDashboardEventBus events,
        ILogger logger)
    {
        Project = project;
        IssueStore = issueStore;
        Agents = agents;
        Sprints = sprints;
        DesignArtifacts = designArtifacts;
        ArtOutputs = artOutputs;
        Worktrees = worktrees;
        GitHub = gitHub;
        PrWatcher = prWatcher;
        Events = events;
        Logger = logger;
    }

    public ValueTask DisposeAsync()
    {
        if (Disposed) return ValueTask.CompletedTask;
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Abstraction over bundle construction so <see cref="OrchestratorAgent"/>
/// can be tested without spinning up a real git repo / cloner / dispatch
/// pipeline per test. Production wires
/// <see cref="ProjectDispatchBundleFactory"/>; tests can substitute a
/// stub.
/// </summary>
public interface IProjectDispatchBundleFactory
{
    ProjectDispatchBundle Build(ProjectOptions project);
}

/// <summary>
/// Builds a <see cref="ProjectDispatchBundle"/> on demand for a
/// registered project. Constructed once at orchestrator startup and
/// passed to <see cref="OrchestratorAgent"/>. The bundle cache lives
/// on the agent itself; the factory only knows how to construct.
/// </summary>
public sealed class ProjectDispatchBundleFactory : IProjectDispatchBundleFactory
{
    private readonly AgentOptions _options;
    private readonly string _dataRoot;
    private readonly IProjectStore _projectStore;
    private readonly ProjectCloner _cloner;
    private readonly IAgentRunner _runner;
    private readonly RoleAgentRegistry _roleRegistry;
    private readonly IWorkflowDispatcher _dispatcher;
    private readonly AgentMessageBus _messageBus;
    private readonly IDashboardEventBus _events;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISecretStore? _secrets;

    public ProjectDispatchBundleFactory(
        AgentOptions options,
        string dataRoot,
        IProjectStore projectStore,
        ProjectCloner cloner,
        IAgentRunner runner,
        RoleAgentRegistry roleRegistry,
        IWorkflowDispatcher dispatcher,
        AgentMessageBus messageBus,
        IDashboardEventBus events,
        ILoggerFactory loggerFactory,
        ISecretStore? secrets = null)
    {
        _options = options;
        _dataRoot = dataRoot;
        _projectStore = projectStore;
        _cloner = cloner;
        _runner = runner;
        _roleRegistry = roleRegistry;
        _dispatcher = dispatcher;
        _messageBus = messageBus;
        _events = events;
        _loggerFactory = loggerFactory;
        _secrets = secrets;
    }

    /// <summary>
    /// Per-project github_token secret overrides the global
    /// GITHUB_TOKEN / github.token config. Build() is sync, so the
    /// secret read blocks — same pattern as ProjectContextFactory's
    /// live KnownProjects. A decrypt failure (keyring rotation)
    /// falls back to the global token.
    /// </summary>
    private string? ResolveGitHubToken(string projectId)
    {
        if (_secrets is null) return _options.GitHub?.Token;
        try
        {
            var perProject = _secrets.GetPlaintextAsync(projectId, SecretKinds.GitHubToken)
                .GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(perProject)) return perProject;
        }
        catch
        {
            // fall through to the global token
        }
        return _options.GitHub?.Token;
    }

    private GitHubOptions BuildGitHubOptions(ProjectOptions project)
    {
        var global = _options.GitHub ?? new GitHubOptions();
        var token = ResolveGitHubToken(project.Id);
        var (owner, repo) = ParseGitHubOwnerRepo(project.RepoUrl) ?? (global.Owner, global.Repo);
        if (string.IsNullOrEmpty(token) && string.Equals(owner, global.Owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(repo, global.Repo, StringComparison.OrdinalIgnoreCase))
            return global;
        return new GitHubOptions
        {
            Owner = owner,
            Repo = repo,
            Token = token ?? global.Token,
        };
    }

    /// <summary>
    /// Parse owner/repo from common GitHub URL shapes:
    /// https://github.com/Owner/Repo(.git), ssh git@github.com:Owner/Repo(.git).
    /// Returns null for non-GitHub or unparseable URLs (PR ops then
    /// fall back to the global options).
    /// </summary>
    internal static (string Owner, string Repo)? ParseGitHubOwnerRepo(string? repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl)) return null;
        string path;
        if (repoUrl.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            path = repoUrl["git@github.com:".Length..];
        }
        else if (Uri.TryCreate(repoUrl, UriKind.Absolute, out var uri)
                 && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            path = uri.AbsolutePath.TrimStart('/');
        }
        else
        {
            return null;
        }
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? (parts[0], parts[1]) : null;
    }

    /// <summary>
    /// Construct a bundle for the given project. The bootstrap is
    /// idempotent (runs `git clone` if RepoUrl set, else falls back
    /// to empty-repo scaffold), then per-project IssueStore +
    /// GitWorktreeService + GitHubService + PRWatcher are wired up.
    /// </summary>
    public ProjectDispatchBundle Build(ProjectOptions project)
    {
        // Ensure local working copy exists (idempotent).
        var bootstrap = new Projects.ProjectBootstrap(
            _dataRoot, _cloner, _options.GitHub, _loggerFactory.CreateLogger<Projects.ProjectBootstrap>());
        var ensured = bootstrap.EnsureProject(project);

        var dbPath = ForgesystemPaths.IssuesDb(_dataRoot, project.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var issueStore = new IssueStore(dbPath);
        var agents = new AgentStore(issueStore);
        var sprints = new SprintStore(issueStore);
        var designArtifacts = new DesignArtifactStore(dbPath);
        var artOutputs = new ArtOutputStore(dbPath);

        var worktrees = new GitWorktreeService(
            new WorkspaceOptions
            {
                Root = ensured.Project.Root,
                WorktreeRoot = ForgesystemPaths.WorktreeDir(_dataRoot, project.Id),
                DefaultBranch = string.IsNullOrWhiteSpace(ensured.Project.DefaultBranch) ? "main" : ensured.Project.DefaultBranch,
            },
            _loggerFactory.CreateLogger<GitWorktreeService>(),
            githubToken: ResolveGitHubToken(project.Id));

        var gitHub = new GitHubService(BuildGitHubOptions(ensured.Project));

        var prWatcher = new PRWatcher(
            gitHub, worktrees, issueStore,
            pollInterval: TimeSpan.FromSeconds(30),
            // Stale window for the sequential watch sweep. Anchored to
            // the watch's CreatedAt (see PRWatcher.PollWatchOnceAsync),
            // so it must cover the operator-merge latency: the solo-
            // identity model means a human merges by hand, possibly
            // hours after the PR opens. 30 minutes (the old poll-loop
            // era default) fails tasks whose PRs are perfectly healthy.
            staleAfter: TimeSpan.FromHours(24),
            _events,
            _loggerFactory.CreateLogger<PRWatcher>());

        return new ProjectDispatchBundle(
            ensured.Project,
            issueStore, agents, sprints,
            designArtifacts, artOutputs,
            worktrees, gitHub, prWatcher,
            _events,
            _loggerFactory.CreateLogger<ProjectDispatchBundle>());
    }
}
