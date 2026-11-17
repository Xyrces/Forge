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
/// Outcome of a <see cref="ProjectCloner.SyncAsync"/> call.
/// <see cref="Branch"/> is the branch actually synced — the
/// reconcile path may discover the remote's real default branch
/// when the stored one doesn't exist there.
/// </summary>
public sealed record ProjectSyncResult(bool Ok, string Branch);

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

    /// <summary>The managed working-copy path for a project id.</summary>
    public string LocalPathFor(string projectId) => ForgesystemPaths.ProjectDir(_dataRoot, projectId);

    /// <summary>
    /// Read the default branch from a local clone's origin/HEAD
    /// symref — the "pull info": what the clone itself recorded from
    /// the remote. Returns null when the symref isn't set.
    /// </summary>
    public async Task<string?> ReadCloneDefaultBranchAsync(string localPath)
    {
        var result = await RunGitAsync(localPath, "symbolic-ref", "--short", "refs/remotes/origin/HEAD");
        if (result.ExitCode != 0) return null;
        const string prefix = "origin/";
        var name = result.Stdout.Trim();
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length
            ? name[prefix.Length..]
            : null;
    }

    /// <summary>
    /// List the remote's branches via <c>git ls-remote --heads</c>.
    /// Returns an empty list when the remote is unreachable.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListRemoteBranchesAsync(
        ProjectOptions project, GitHubOptions? github, CancellationToken ct = default)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(project.RepoUrl)) return Array.Empty<string>();

        var (effectiveUrl, _) = BuildAuthenticatedUrl(project.RepoUrl, github?.Token);
        Directory.CreateDirectory(_dataRoot);
        var result = await RunGitAsync(_dataRoot, "ls-remote", "--heads", effectiveUrl);
        if (result.ExitCode != 0)
        {
            _logger?.LogInformation("Project '{Id}': branch listing failed: {Err}",
                project.Id, result.Stderr.Trim());
            return Array.Empty<string>();
        }

        // Line shape: "<sha>\trefs/heads/<branch>"
        const string marker = "refs/heads/";
        var branches = new List<string>();
        foreach (var raw in result.Stdout.Split('\n'))
        {
            var line = raw.TrimEnd();
            var idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) continue;
            var name = line[(idx + marker.Length)..];
            if (!string.IsNullOrWhiteSpace(name)) branches.Add(name);
        }
        branches.Sort(StringComparer.OrdinalIgnoreCase);
        return branches;
    }

    /// <summary>
    /// Ask the remote for its default branch (the target of its HEAD
    /// symref) via <c>git ls-remote --symref</c>. Returns null when the
    /// remote is unreachable or doesn't advertise a symref — callers
    /// then keep their configured/fallback branch.
    /// </summary>
    public async Task<string?> DetectDefaultBranchAsync(
        ProjectOptions project, GitHubOptions? github, CancellationToken ct = default)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        if (string.IsNullOrWhiteSpace(project.RepoUrl)) return null;

        var (effectiveUrl, _) = BuildAuthenticatedUrl(project.RepoUrl, github?.Token);
        Directory.CreateDirectory(_dataRoot);
        var result = await RunGitAsync(_dataRoot, "ls-remote", "--symref", effectiveUrl, "HEAD");
        if (result.ExitCode != 0)
        {
            _logger?.LogInformation("Project '{Id}': default-branch detection failed: {Err}",
                project.Id, result.Stderr.Trim());
            return null;
        }

        // First line shape: "ref: refs/heads/<branch>\tHEAD"
        foreach (var raw in result.Stdout.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (!line.StartsWith("ref: refs/heads/", StringComparison.Ordinal) ||
                !line.EndsWith("HEAD", StringComparison.Ordinal))
                continue;
            var name = line["ref: refs/heads/".Length..];
            var cut = name.IndexOfAny(new[] { '\t', ' ' });
            if (cut > 0) name = name[..cut];
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        return null;
    }

    /// <summary>
    /// Refresh a clean checkout from its exact configured remote branch.
    /// Local divergence, dirty work, or a missing origin requires explicit repair.
    /// Shared by operator sync, startup, and planning; never resets local work.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> SyncLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public async Task<ProjectSyncResult> SyncAsync(ProjectOptions project, GitHubOptions? github, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(project.Root)
            ? ForgesystemPaths.ProjectDir(_dataRoot, project.Id) : project.Root);
        var gate = SyncLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await SyncCoreAsync(project, github, ct); }
        finally { gate.Release(); }
    }

    private async Task<ProjectSyncResult> SyncCoreAsync(ProjectOptions project, GitHubOptions? github, CancellationToken ct)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        var branch = string.IsNullOrWhiteSpace(project.DefaultBranch) ? "main" : project.DefaultBranch;
        var localPath = string.IsNullOrWhiteSpace(project.Root)
            ? ForgesystemPaths.ProjectDir(_dataRoot, project.Id) : Path.GetFullPath(project.Root);
        if (!Directory.Exists(Path.Combine(localPath, ".git")))
        {
            if (!string.Equals(Path.GetFullPath(localPath), Path.GetFullPath(ForgesystemPaths.ProjectDir(_dataRoot, project.Id)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return new ProjectSyncResult(false, branch);

            // The registration-time clone may have failed (e.g. a
            // stale global PAT while the per-project secret is valid);
            // sync is the documented retry path, so attempt the clone.
            _logger?.LogInformation(
                "Project '{Id}': no working copy at {Path}; attempting clone", project.Id, localPath);
            try
            {
                await CloneAsync(project, github, ct);
                return new ProjectSyncResult(true, branch);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Project '{Id}': clone-on-sync failed", project.Id);
                return new ProjectSyncResult(false, branch);
            }
        }

        var scaffoldMarker = Path.Combine(localPath, ".git", "forge-scaffold-head");
        if (File.Exists(scaffoldMarker))
        {
            var recorded = (await File.ReadAllTextAsync(scaffoldMarker, ct)).Trim();
            var head = await RunGitAsync(localPath, ct, "rev-parse", "HEAD");
            var status = await RunGitAsync(localPath, ct, "status", "--porcelain", "--untracked-files=all");
            if (!System.Text.RegularExpressions.Regex.IsMatch(recorded, "^[0-9a-f]{40}$")
                || head.ExitCode != 0 || head.Stdout.Trim() != recorded || status.ExitCode != 0 || status.Stdout.Length != 0)
                return new ProjectSyncResult(false, branch);
            var healed = await ReconcileScaffoldAsync(project, localPath, branch, github, ct);
            if (healed.Ok) File.Delete(scaffoldMarker);
            return healed;
        }

        // Do not infer that an origin-less repository is a disposable scaffold.
        // Automatic refresh must never reset unknown local work.
        var remoteCheck = await RunGitAsync(localPath, ct, "remote", "get-url", "origin");
        if (remoteCheck.ExitCode != 0)
        {
            _logger?.LogWarning("Project '{Id}': origin is missing; explicit repository repair is required, preserving local work", project.Id);
            return new ProjectSyncResult(false, branch);
        }

        var current = await RunGitAsync(localPath, ct, "symbolic-ref", "--short", "HEAD");
        var dirty = await RunGitAsync(localPath, ct, "status", "--porcelain", "--untracked-files=all");
        if (current.ExitCode != 0 || current.Stdout.Trim() != branch || dirty.ExitCode != 0 || dirty.Stdout.Length != 0)
        {
            _logger?.LogWarning("Project '{Id}': refresh requires a clean checkout on configured branch {Branch}; preserving local work", project.Id, branch);
            return new ProjectSyncResult(false, branch);
        }

        var observedRef = "refs/forge/planning/" + Guid.NewGuid().ToString("N");
        try
        {
            var fetch = await RunGitAsync(localPath, ct, "fetch", "--no-tags", "--no-write-fetch-head", "origin", $"+refs/heads/{branch}:{observedRef}");
            if (fetch.ExitCode != 0) return new ProjectSyncResult(false, branch);
            var fetched = await RunGitAsync(localPath, ct, "rev-parse", "--verify", observedRef + "^{commit}");
            var sha = fetched.Stdout.Trim();
            if (fetched.ExitCode != 0 || !System.Text.RegularExpressions.Regex.IsMatch(sha, "^[0-9a-f]{40}$"))
                return new ProjectSyncResult(false, branch);
            var ancestor = await RunGitAsync(localPath, ct, "merge-base", "--is-ancestor", "HEAD", sha);
            if (ancestor.ExitCode != 0)
            {
                _logger?.LogWarning("Project '{Id}': local commits differ from remote; preserving checkout", project.Id);
                return new ProjectSyncResult(false, branch);
            }
            var result = await RunGitAsync(localPath, ct, "merge", "--ff-only", sha);
            if (result.ExitCode != 0) return new ProjectSyncResult(false, branch);
            return new ProjectSyncResult(true, branch);
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var deleted = await RunGitAsync(localPath, cleanup.Token, "update-ref", "-d", observedRef);
                if (deleted.ExitCode != 0) _logger?.LogWarning("Project '{Id}': could not remove temporary source ref", project.Id);
            }
            catch (Exception ex) { _logger?.LogWarning(ex, "Project '{Id}': temporary source ref cleanup failed", project.Id); }
        }

    }

    /// <summary>
    /// Reattach a scaffolded working copy (the bootstrap's git-init
    /// fallback) to its remote: add origin, install the credential
    /// helper (HTTPS + PAT), fetch, and align the branch with
    /// <c>origin/&lt;branch&gt;</c>. When the stored branch doesn't
    /// exist on the remote, the remote's advertised default branch
    /// wins (the stored value was a guess, not an override).
    /// Scaffold commits are disposable by definition — agents never
    /// commit in the project root.
    /// </summary>
    private async Task<ProjectSyncResult> ReconcileScaffoldAsync(
        ProjectOptions project, string localPath, string branch, GitHubOptions? github, CancellationToken ct)
    {
        _logger?.LogInformation("Project '{Id}': no origin remote; reconciling scaffold against {Url}",
            project.Id, ScrubUrl(project.RepoUrl));

        var exists = await RunGitAsync(localPath, ct, "remote", "get-url", "origin");
        var add = await RunGitAsync(localPath, ct, "remote", exists.ExitCode == 0 ? "set-url" : "add", "origin", project.RepoUrl);
        if (add.ExitCode != 0)
        {
            _logger?.LogWarning("Project '{Id}': remote add failed: {Err}", project.Id, add.Stderr.Trim());
            return new ProjectSyncResult(false, branch);
        }

        if (IsHttps(project.RepoUrl) && !string.IsNullOrEmpty(github?.Token))
        {
            var credPath = Path.Combine(localPath, ".forge", "git-credentials");
            Directory.CreateDirectory(Path.GetDirectoryName(credPath)!);
            await File.WriteAllTextAsync(credPath, BuildCredentialStoreEntry(project.RepoUrl, github.Token) + "\n", ct);
            try
            {
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    File.SetUnixFileMode(credPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { /* non-POSIX — best effort */ }
            await RunGitAsync(localPath, ct, "config", "credential.helper", $"store --file={credPath}");
        }

        var fetch = await RunGitAsync(localPath, ct, "fetch", "origin", branch);
        if (fetch.ExitCode != 0)
        {
            // The stored branch may predate detection support (a "main"
            // guess) — ask the remote for its real default and retry.
            var detected = await DetectDefaultBranchAsync(project, github, ct);
            if (detected is null || string.Equals(detected, branch, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning("Project '{Id}': fetch failed: {Err}", project.Id, fetch.Stderr.Trim());
                return new ProjectSyncResult(false, branch);
            }
            _logger?.LogInformation(
                "Project '{Id}': stored branch '{Stored}' not on remote; using detected default '{Detected}'",
                project.Id, branch, detected);
            branch = detected;
            fetch = await RunGitAsync(localPath, ct, "fetch", "origin", branch);
            if (fetch.ExitCode != 0)
            {
                _logger?.LogWarning("Project '{Id}': fetch failed: {Err}", project.Id, fetch.Stderr.Trim());
                return new ProjectSyncResult(false, branch);
            }
        }

        var align = await RunGitAsync(localPath, ct, "checkout", "-B", branch, $"origin/{branch}");
        if (align.ExitCode != 0)
        {
            _logger?.LogWarning("Project '{Id}': align to origin/{Branch} failed: {Err}", project.Id, branch, align.Stderr.Trim());
            return new ProjectSyncResult(false, branch);
        }
        _logger?.LogInformation("Project '{Id}': scaffold reconciled with origin/{Branch}", project.Id, branch);
        // Stamp origin/HEAD so the clone's own pull info reflects the
        // remote default from here on (best effort).
        await RunGitAsync(localPath, ct, "remote", "set-head", "origin", "--auto");
        return new ProjectSyncResult(true, branch);
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

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(string cwd, params string[] args)
        => RunGitAsync(cwd, CancellationToken.None, args);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string cwd, CancellationToken ct, params string[] args)
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            await p.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdoutTask, stderrTask);
            throw;
        }
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }
}
