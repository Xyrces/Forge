using System.Diagnostics;
using Forge.Configuration;
using Microsoft.Extensions.Logging;

namespace Forge.Projects;

/// <summary>
/// Result of preparing a single project for use.
/// <see cref="Created"/> is true when the bootstrap had to create the
/// project directory on disk. <see cref="InitializedAsGitRepo"/> is
/// true when the bootstrap had to scaffold an empty git repo (no
/// <see cref="ProjectOptions.RepoUrl"/> was set, OR a clone failed
/// and we fell back to the legacy empty-repo path).
/// </summary>
public sealed record ProjectBootstrapResult(
    ProjectOptions Project,
    bool Created,
    bool InitializedAsGitRepo,
    bool ClonedFromRemote,
    string StateDirectory,
    string IssuesDbPath,
    string WorktreeParent);

/// <summary>
/// Prepares per-project filesystem locations on disk. Three modes:
/// <list type="number">
///   <item><b>Managed clone</b>: <see cref="ProjectOptions.RepoUrl"/>
///   is set. <see cref="ProjectCloner"/> clones into
///   <c>{dataRoot}/projects/{id}/</c>.</item>
///   <item><b>Operator-managed</b>: <see cref="ProjectOptions.Root"/>
///   is set. Honored as-is (created if missing, initialized as a git
///   repo if missing).</item>
///   <item><b>Auto-scaffold</b>: both empty. Bootstrap creates
///   <c>{dataRoot}/projects/{id}/</c> + an empty git repo (legacy
///   "zero-config fresh machine" path).</item>
/// </list>
/// </summary>
public sealed class ProjectBootstrap
{
    private readonly string _dataRoot;
    private readonly ProjectCloner _cloner;
    private readonly Configuration.GitHubOptions? _github;
    private readonly ILogger<ProjectBootstrap>? _logger;

    public ProjectBootstrap(
        string dataRoot,
        ProjectCloner cloner,
        Configuration.GitHubOptions? github = null,
        ILogger<ProjectBootstrap>? logger = null)
    {
        _dataRoot = dataRoot;
        _cloner = cloner;
        _github = github;
        _logger = logger;
    }

    public string DataRoot => _dataRoot;

    public ProjectBootstrapResult EnsureProject(ProjectOptions project)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(project.Id))
            throw new InvalidOperationException("Project id is required.");

        var operatorRoot = project.Root;
        var isOperatorManaged = !string.IsNullOrWhiteSpace(operatorRoot);
        var hasRepoUrl = !string.IsNullOrWhiteSpace(project.RepoUrl);

        var rootPath = isOperatorManaged
            ? Path.GetFullPath(operatorRoot)
            : ForgesystemPaths.ProjectDir(_dataRoot, project.Id);

        var created = false;
        if (!Directory.Exists(rootPath))
        {
            Directory.CreateDirectory(rootPath);
            created = true;
            _logger?.LogInformation(
                "Project '{Id}': created root directory at {Root}",
                project.Id, rootPath);
        }

        var gitInitDone = false;
        var clonedFromRemote = false;
        if (!Directory.Exists(Path.Combine(rootPath, ".git")))
        {
            if (hasRepoUrl)
            {
                try
                {
                    _cloner.CloneAsync(project, _github, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    clonedFromRemote = true;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex,
                        "Project '{Id}': clone from {Url} failed; falling back to empty-repo scaffold",
                        project.Id, project.RepoUrl);
                }
            }

            if (!Directory.Exists(Path.Combine(rootPath, ".git")))
            {
                // The cloner deletes the local path on clone failure
                // (so a retry isn't a no-op). Re-create it here so
                // the git-init fallback has a valid cwd.
                Directory.CreateDirectory(rootPath);
                RunGit(rootPath, "init -q -b main");
                RunGit(rootPath, "config user.email \"forge@local\"");
                RunGit(rootPath, "config user.name \"Forge Bootstrap\"");
                File.WriteAllText(
                    Path.Combine(rootPath, ".gitignore"),
                    ".forge/\n*.user\n");
                RunGit(rootPath, "add .gitignore");
                RunGit(rootPath, "commit -q -m \"scaffold\"");
                gitInitDone = true;
                _logger?.LogInformation(
                    "Project '{Id}': initialised empty git repo at {Root} (default branch main)",
                    project.Id, rootPath);
            }
        }

        if (hasRepoUrl && Directory.Exists(Path.Combine(rootPath, ".git")))
        {
            // Scaffolded repo (an earlier clone failed and the git-init
            // fallback ran): reconcile with the remote so a fixed PAT
            // self-heals on boot. Only call sync when origin is
            // actually missing — a healthy clone needs nothing here.
            if (ProbeGit(rootPath, "remote get-url origin") != 0)
            {
                var healed = _cloner.SyncAsync(project, _github, CancellationToken.None)
                    .GetAwaiter().GetResult();
                _logger?.LogInformation(
                    "Project '{Id}': scaffold reconcile {Result}", project.Id, healed ? "succeeded" : "failed (see warnings)");
            }
        }

        var stateDir = isOperatorManaged
            ? ForgesystemPaths.StateDir(_dataRoot, project.Id)
            : Path.Combine(rootPath, ".forge", "state");
        if (project.Id == "default" && isOperatorManaged)
        {
            stateDir = Path.Combine(rootPath, ".portHorizon", "state");
        }
        Directory.CreateDirectory(stateDir);

        var worktreeParent = isOperatorManaged
            ? (string.IsNullOrWhiteSpace(project.Id) || project.Id == "default"
                ? Path.Combine(rootPath, ".portHorizon", "worktrees")
                : ForgesystemPaths.WorktreeDir(_dataRoot, project.Id))
            : Path.Combine(rootPath, ".forge", "worktrees");
        Directory.CreateDirectory(worktreeParent);

        var issuesDb = Path.Combine(stateDir, "issues.db");
        var resolved = project with { Root = rootPath };
        return new ProjectBootstrapResult(
            resolved,
            Created: created,
            InitializedAsGitRepo: gitInitDone,
            ClonedFromRemote: clonedFromRemote,
            StateDirectory: stateDir,
            IssuesDbPath: issuesDb,
            WorktreeParent: worktreeParent);
    }

    private static int ProbeGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(30_000);
        return p.ExitCode;
    }

    private static void RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit(30_000);
        if (p.ExitCode != 0)
        {
            var errOut = stderr.GetAwaiter().GetResult();
            throw new InvalidOperationException(
                $"git {args} (cwd={cwd}) failed: {errOut}");
        }
    }
}
