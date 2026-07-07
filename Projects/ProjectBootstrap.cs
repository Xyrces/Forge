using System.Diagnostics;
using Forge.Configuration;
using Microsoft.Extensions.Logging;

namespace Forge.Projects;

/// <summary>
/// Result of preparing a single project for use. Operators who
/// pre-supplied their own <c>workspace.root</c> get
/// <see cref="Mode"/> = Operator; auto-scaffolded projects get
/// <see cref="Mode"/> = AutoScaffold so the UI can surface which paths
/// the orchestrator created.
/// </summary>
public sealed record ProjectBootstrapResult(
    ProjectOptions Project,
    bool Created,
    bool InitializedAsGitRepo,
    string StateDirectory,
    string IssuesDbPath,
    string WorktreeParent);

/// <summary>
/// Prepares per-project filesystem locations on disk. When the operator
/// has supplied <see cref="ProjectOptions.Root"/>, that path is honored
/// as-is (created if missing, initialized as a git repo if missing).
/// When empty, the project is fully auto-scaffolded under
/// <see cref="ForgesystemPaths.ProjectDir"/> and assigned a Root equal
/// to that scaffold directory.
/// </summary>
public sealed class ProjectBootstrap
{
    private readonly string _dataRoot;
    private readonly ILogger<ProjectBootstrap>? _logger;

    public ProjectBootstrap(string dataRoot, ILogger<ProjectBootstrap>? logger = null)
    {
        _dataRoot = dataRoot;
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
        if (!Directory.Exists(Path.Combine(rootPath, ".git")))
        {
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
            StateDirectory: stateDir,
            IssuesDbPath: issuesDb,
            WorktreeParent: worktreeParent);
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
