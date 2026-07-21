using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Forge.Configuration;

namespace Forge.Projects;

/// <summary>
/// Outcome of a single <see cref="ProjectCloner.CloneAsync"/> call.
/// Idempotent: when the local clone already exists, <see cref="Created"/>
/// is false and <see cref="LocalPath"/> points at the existing checkout.
/// </summary>
public sealed record ProjectCloneResult(
    string LocalPath,
    bool Created,
    string? Source);

/// <summary>
/// Clones (or refreshes) a project's git working copy into a Forge-managed
/// directory under <c>{dataRoot}/projects/{id}/</c>. Auth model:
///
/// <list type="bullet">
///   <item>GitHub HTTPS: PAT is read from <c>GITHUB_TOKEN</c> env or
///   <see cref="GitHubOptions.Token"/>; injected into the clone URL as
///   <c>https://x-access-token:&lt;PAT&gt;@github.com/...</c>, then the
///   stored remote URL is reset to the clean form so the PAT never
///   persists in <c>.git/config</c>.</item>
///   <item>Future push/fetch auth: a credential helper file is written
///   next to the clone (mode 0600) and registered via
///   <c>git config credential.helper 'store --file=...'</c>. The file
///   contains the same <c>x-access-token</c> line.</item>
///   <item>SSH URLs: PAT is ignored; the user's existing
///   <c>~/.ssh/config</c> + ssh-agent do the auth. Credential helper is
///   not installed.</item>
/// </list>
///
/// The cloner is intentionally synchronous-ish: it shells out to <c>git</c>
/// via <see cref="Process.Start"/> and waits for exit. Failures are
/// surfaced as exceptions with the captured stderr; callers (the project
/// bootstrap path + the <c>POST /api/projects</c> handler) decide
/// whether to log + continue or abort startup.
/// </summary>
public sealed class ProjectCloner
{
    private readonly string _dataRoot;
    private readonly ILogger<ProjectCloner>? _logger;

    public ProjectCloner(string dataRoot, ILogger<ProjectCloner>? logger = null)
    {
        _dataRoot = dataRoot;
        _logger = logger;
    }

    public string DataRoot => _dataRoot;

    /// <summary>
    /// Ensure a working copy exists at <c>{dataRoot}/projects/{id}/</c>.
    /// If the directory already contains a <c>.git/</c>, treat it as
    /// already cloned and skip (<see cref="ProjectCloneResult.Created"/>
    /// is false). If the directory is empty or missing, run
    /// <c>git clone --branch {DefaultBranch} &lt;repoUrl&gt;</c> with PAT
    /// auth (for HTTPS), then strip the PAT from <c>.git/config</c> and
    /// install a credential helper file for future push/fetch.
    /// </summary>
    public async Task<ProjectCloneResult> CloneAsync(
        ProjectOptions project,
        GitHubOptions? github,
        CancellationToken ct = default)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(project.RepoUrl))
            throw new InvalidOperationException($"Project '{project.Id}' has no RepoUrl.");

        var localPath = ForgesystemPaths.ProjectDir(_dataRoot, project.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        var gitDir = Path.Combine(localPath, ".git");
        if (Directory.Exists(gitDir))
        {
            _logger?.LogInformation("Project '{Id}': working copy already present at {Path}; skipping clone",
                project.Id, localPath);
            return new ProjectCloneResult(localPath, Created: false, Source: "existing");
        }

        Directory.CreateDirectory(localPath);

        var (effectiveUrl, isHttps) = BuildAuthenticatedUrl(project.RepoUrl, github?.Token);
        var branch = string.IsNullOrWhiteSpace(project.DefaultBranch) ? "main" : project.DefaultBranch;

        _logger?.LogInformation("Project '{Id}': cloning {Url} -> {Path} (branch={Branch}, auth={Auth})",
            project.Id, ScrubUrl(project.RepoUrl), localPath, branch,
            isHttps && !string.IsNullOrEmpty(github?.Token) ? "pat" : "anonymous");

        var cloneResult = await RunGitAsync(localPath,
            "clone", "--branch", branch, effectiveUrl, localPath);
        if (cloneResult.ExitCode != 0)
        {
            // Clean up the empty dir we created so a retry isn't a no-op.
            try { Directory.Delete(localPath, recursive: true); } catch { }
            throw new InvalidOperationException(
                $"git clone for project '{project.Id}' failed: {cloneResult.Stderr.Trim()}");
        }

        if (isHttps && !string.IsNullOrEmpty(github?.Token))
        {
            // 1. Reset the stored remote URL to drop the PAT from .git/config.
            await ResetRemoteAsync(localPath, project.RepoUrl, ct);

            // 2. Write a credential helper file (mode 0600) so future
            //    push/fetch operations don't prompt. The file lives
            //    inside the clone, not in the user's home — it goes
            //    away with the clone (rm -rf <localPath> revokes the
            //    stored credential).
            var credPath = Path.Combine(localPath, ".forge", "git-credentials");
            Directory.CreateDirectory(Path.GetDirectoryName(credPath)!);
            var credUrl = BuildCredentialStoreEntry(project.RepoUrl, github.Token);
            await File.WriteAllTextAsync(credPath, credUrl + "\n", ct);
            try
            {
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    File.SetUnixFileMode(credPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch { /* non-POSIX — best effort */ }

            await RunGitAsync(localPath, "config",
                "credential.helper", $"store --file={credPath}");
        }

        return new ProjectCloneResult(localPath, Created: true, Source: project.RepoUrl);
    }

    /// <summary>
    /// Pull the latest from origin/<see cref="ProjectOptions.DefaultBranch"/>.
    /// Returns true on success. Used by the manual <c>POST /api/projects/{id}/sync</c>
    /// endpoint and as a sanity check at startup (so the dashboard
    /// surfaces "remote is ahead by N commits" via the UI).
    /// </summary>
    public async Task<bool> SyncAsync(ProjectOptions project, GitHubOptions? github, CancellationToken ct = default)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        var localPath = ForgesystemPaths.ProjectDir(_dataRoot, project.Id);
        if (!Directory.Exists(Path.Combine(localPath, ".git")))
        {
            _logger?.LogWarning("Project '{Id}': sync requested but no working copy at {Path}", project.Id, localPath);
            return false;
        }

        var branch = string.IsNullOrWhiteSpace(project.DefaultBranch) ? "main" : project.DefaultBranch;
        var result = await RunGitAsync(localPath,
            "pull", "--ff-only", "origin", branch);
        if (result.ExitCode != 0)
        {
            _logger?.LogWarning("Project '{Id}': pull failed: {Err}", project.Id, result.Stderr.Trim());
            return false;
        }
        return true;
    }

    private static (string effectiveUrl, bool isHttps) BuildAuthenticatedUrl(string repoUrl, string? token)
    {
        if (string.IsNullOrEmpty(token)) return (repoUrl, IsHttps(repoUrl));
        if (!IsHttps(repoUrl)) return (repoUrl, false);

        // https://github.com/owner/repo.git -> https://x-access-token:<PAT>@github.com/owner/repo.git
        var uri = new Uri(repoUrl);
        var authed = new UriBuilder(uri)
        {
            UserName = Uri.EscapeDataString("x-access-token"),
            Password = token,
        }.Uri.ToString();
        return (authed, true);
    }

    private static async Task ResetRemoteAsync(string repoPath, string cleanUrl, CancellationToken ct)
    {
        await RunGitAsync(repoPath,
            "remote", "set-url", "origin", cleanUrl);
    }

    private static string BuildCredentialStoreEntry(string repoUrl, string token)
    {
        // The credential store format is one URL per line, no scheme
        // prefix. Git matches by host + path prefix; we give it the
        // full URL with embedded creds so any future fetch/push on
        // the same repo gets the token.
        var uri = new Uri(repoUrl);
        return $"{uri.Scheme}://x-access-token:{token}@{uri.Host}{uri.AbsolutePath}";
    }

    private static bool IsHttps(string url) => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string ScrubUrl(string url)
    {
        // For logging — strip any embedded credentials so the URL
        // doesn't leak into journald.
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            return $"{u.Scheme}://{u.Host}{u.AbsolutePath}";
        return url;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // GIT_TERMINAL_PROMPT=0 stops git from prompting on the
        // terminal when credentials are missing — useful in CI /
        // server contexts where a hanging git would block startup.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        // ArgumentList bypasses the runtime's shell-style tokenizer
        // (which mangles quoted args on Linux and turned "main" into
        // an extra positional that confused git into printing help).
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }
}
