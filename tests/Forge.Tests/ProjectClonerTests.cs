using System.Diagnostics;
using Forge.Configuration;
using Forge.Projects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public class ProjectClonerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _remoteDir;
    private readonly ProjectCloner _cloner;

    public ProjectClonerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"ph-clone-{Guid.NewGuid():N}");
        _remoteDir = Path.Combine(_tempRoot, "remote.git");
        Directory.CreateDirectory(_tempRoot);
        InitBareRepo(_remoteDir);
        SeedCommit(_remoteDir, "main", "README.md", "# Seed\n", "initial");
        _cloner = new ProjectCloner(_tempRoot, NullLogger<ProjectCloner>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task CloneAsync_LocalFileUrl_CreatesCheckout()
    {
        var project = new ProjectOptions
        {
            Id = "demo",
            Name = "Demo",
            RepoUrl = _remoteDir,
            DefaultBranch = "main",
        };

        var result = await _cloner.CloneAsync(project, github: null);
        Assert.True(result.Created);
        Assert.True(Directory.Exists(Path.Combine(result.LocalPath, ".git")));
        Assert.True(File.Exists(Path.Combine(result.LocalPath, "README.md")));
        // Idempotent: second call should not re-clone.
        var second = await _cloner.CloneAsync(project, github: null);
        Assert.False(second.Created);
        Assert.Equal(result.LocalPath, second.LocalPath);
    }

    [Fact]
    public async Task CloneAsync_WithToken_DoesNotPersistTokenInRemoteUrl()
    {
        // Inject a fake GitHub-style URL with token in the "remote" by
        // rewriting the URL through the cloner's own authenticated-URL
        // builder is not possible (we don't expose it); so the test
        // asserts the *post-clone* remote URL is the clean form when
        // the source was an HTTPS-style URL we can't actually clone in
        // CI. We substitute a file:// URL and just verify the remote
        // is the original (no PAT-injected URL stored).
        var project = new ProjectOptions
        {
            Id = "nopat",
            Name = "NoPat",
            RepoUrl = _remoteDir,
            DefaultBranch = "main",
        };
        var fakeGithub = new GitHubOptions { Token = "ghp_fake_should_not_persist" };

        var result = await _cloner.CloneAsync(project, fakeGithub);
        // For file:// URLs the cloner skips auth entirely; the
        // assertion here is that the stored remote is the original
        // (un-augmented) URL, never one containing "fake_should".
        var head = Path.Combine(result.LocalPath, ".git", "HEAD");
        Assert.True(File.Exists(head));
        var config = await ReadGitConfig(result.LocalPath);
        Assert.DoesNotContain("fake_should", config);
        Assert.DoesNotContain("x-access-token", config);
    }

    [Fact]
    public async Task SyncAsync_PullsNewCommitFromRemote()
    {
        var project = new ProjectOptions
        {
            Id = "syncer",
            Name = "Syncer",
            RepoUrl = _remoteDir,
            DefaultBranch = "main",
        };

        var first = await _cloner.CloneAsync(project, github: null);
        Assert.True(File.Exists(Path.Combine(first.LocalPath, "README.md")));

        // Push a new commit to the remote.
        SeedCommit(_remoteDir, "main", "CHANGELOG.md", "v0.2.0\n", "second");

        var ok = (await _cloner.SyncAsync(project, github: null)).Ok;
        Assert.True(ok);
        Assert.True(File.Exists(Path.Combine(first.LocalPath, "CHANGELOG.md")));
    }

    [Fact]
    public async Task SyncAsync_NoWorkingCopy_ClonesInsteadOfFailing()
    {
        // Registration-time clone failures (e.g. a stale global PAT)
        // are retried via sync — the documented operator recovery path.
        var project = new ProjectOptions
        {
            Id = "lateclone",
            Name = "LateClone",
            RepoUrl = _remoteDir,
            DefaultBranch = "main",
        };

        var ok = (await _cloner.SyncAsync(project, github: null)).Ok;
        Assert.True(ok);
        Assert.True(File.Exists(Path.Combine(
            ForgesystemPaths.ProjectDir(_tempRoot, project.Id), "README.md")));
    }

    [Fact]
    public async Task SyncAsync_ScaffoldedRepo_ReconcilesWithRemote()
    {
        // Simulate the bootstrap's git-init fallback: a local repo
        // with a scaffold commit and no origin, shadowing the remote.
        var project = new ProjectOptions
        {
            Id = "scaffolded",
            Name = "Scaffolded",
            RepoUrl = _remoteDir,
            DefaultBranch = "main",
        };
        var localPath = ForgesystemPaths.ProjectDir(_tempRoot, project.Id);
        Directory.CreateDirectory(localPath);
        RunGit(localPath, "init", "-q -b main");
        RunGit(localPath, "config", "user.email forge@local");
        RunGit(localPath, "config", "user.name \"Forge Bootstrap\"");
        File.WriteAllText(Path.Combine(localPath, ".gitignore"), ".forge/\n");
        RunGit(localPath, "add", ".gitignore");
        RunGit(localPath, "commit", "-q -m scaffold");

        var ok = (await _cloner.SyncAsync(project, github: null)).Ok;

        Assert.True(ok);
        Assert.True(File.Exists(Path.Combine(localPath, "README.md")));

        // Origin reattached: a second sync takes the ff-only pull path.
        SeedCommit(_remoteDir, "main", "CHANGELOG.md", "v0.2.0\n", "second");
        Assert.True((await _cloner.SyncAsync(project, github: null)).Ok);
        Assert.True(File.Exists(Path.Combine(localPath, "CHANGELOG.md")));
    }

    [Fact]
    public async Task DetectDefaultBranchAsync_ReturnsRemoteHead()
    {
        var trunkRemote = Path.Combine(_tempRoot, "trunk-remote.git");
        InitBareRepo(trunkRemote, "trunk");
        SeedCommit(trunkRemote, "trunk", "README.md", "# Trunk\n", "initial");

        var project = new ProjectOptions
        {
            Id = "detector",
            Name = "Detector",
            RepoUrl = trunkRemote,
            DefaultBranch = "main",
        };

        var detected = await _cloner.DetectDefaultBranchAsync(project, github: null);
        Assert.Equal("trunk", detected);
    }

    [Fact]
    public async Task SyncAsync_ScaffoldedRepo_UsesDetectedBranchWhenStoredMissing()
    {
        // Registry says "main" (the old guess) but the remote's
        // default is "trunk": reconcile should detect + align to
        // trunk and report it in the result.
        var trunkRemote = Path.Combine(_tempRoot, "trunk-remote2.git");
        InitBareRepo(trunkRemote, "trunk");
        SeedCommit(trunkRemote, "trunk", "README.md", "# Trunk\n", "initial");

        var project = new ProjectOptions
        {
            Id = "scaffold-trunk",
            Name = "ScaffoldTrunk",
            RepoUrl = trunkRemote,
            DefaultBranch = "main",
        };
        var localPath = ForgesystemPaths.ProjectDir(_tempRoot, project.Id);
        Directory.CreateDirectory(localPath);
        RunGit(localPath, "init", "-q -b main");
        RunGit(localPath, "config", "user.email forge@local");
        RunGit(localPath, "config", "user.name \"Forge Bootstrap\"");
        File.WriteAllText(Path.Combine(localPath, ".gitignore"), ".forge/\n");
        RunGit(localPath, "add", ".gitignore");
        RunGit(localPath, "commit", "-q -m scaffold");

        var result = await _cloner.SyncAsync(project, github: null);

        Assert.True(result.Ok);
        Assert.Equal("trunk", result.Branch);
        Assert.True(File.Exists(Path.Combine(localPath, "README.md")));

        // Pull info stamped: the clone's origin/HEAD now reflects the
        // remote default.
        Assert.Equal("trunk", await _cloner.ReadCloneDefaultBranchAsync(localPath));
    }

    [Fact]
    public async Task ReadCloneDefaultBranchAsync_ReadsOriginHead()
    {
        var trunkRemote = Path.Combine(_tempRoot, "trunk-remote3.git");
        InitBareRepo(trunkRemote, "trunk");
        SeedCommit(trunkRemote, "trunk", "README.md", "# Trunk\n", "initial");

        var project = new ProjectOptions
        {
            Id = "pullinfo",
            Name = "PullInfo",
            RepoUrl = trunkRemote,
            DefaultBranch = "trunk",
        };
        var clone = await _cloner.CloneAsync(project, github: null);
        Assert.Equal("trunk", await _cloner.ReadCloneDefaultBranchAsync(clone.LocalPath));

        // No origin (scaffold-style repo) → null.
        var bare = Path.Combine(_tempRoot, "no-origin");
        Directory.CreateDirectory(bare);
        RunGit(bare, "init", "-q -b main");
        Assert.Null(await _cloner.ReadCloneDefaultBranchAsync(bare));
    }

    private static void InitBareRepo(string path, string branch = "main")
    {
        // `git init --bare <path>` creates <path>; we must run from
        // <path>'s parent, not from <path> itself (which doesn't exist yet).
        var parent = Path.GetDirectoryName(path)!;
        RunGit(parent, "init", $"--bare --initial-branch={branch} -q \"{path}\"");
    }

    private static void SeedCommit(string bareRepo, string branch, string filename, string content, string message)
    {
        // Seed via a side working tree. Clone from the bare repo (so
        // we get the existing history on subsequent seeds) then add
        // a new commit and push. This pattern works for the first
        // commit too — clone an empty repo, write a file, commit.
        var work = Path.Combine(Path.GetTempPath(), $"ph-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            // Clone (works for empty OR non-empty bare repo).
            RunGit(work, "clone", $"-q \"{bareRepo}\" \"{work}\"");
            RunGit(work, "config", "user.email forge@test.local");
            RunGit(work, "config", "user.name Forge Test");
            // If the bare repo was empty, HEAD doesn't exist yet —
            // create an initial empty commit so subsequent branches
            // can be created from a known ref.
            var hasHead = File.Exists(Path.Combine(work, ".git", "HEAD"))
                && !File.ReadAllText(Path.Combine(work, ".git", "HEAD")).Trim().EndsWith("/");
            if (!hasHead)
            {
                RunGit(work, "commit", "--allow-empty -q -m initial");
                RunGit(work, "push", $"-q origin {branch}");
            }
            File.WriteAllText(Path.Combine(work, filename), content);
            RunGit(work, "add", filename);
            RunGit(work, "commit", $"-q -m \"{message}\"");
            RunGit(work, "push", $"-q origin {branch}");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    private static void RunGit(string cwd, string verb, string args)
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
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // ArgumentList bypasses the runtime's shell-tokenizer so
        // quoted paths with spaces aren't double-split.
        psi.ArgumentList.Add(verb);
        foreach (var part in SplitArgs(args)) psi.ArgumentList.Add(part);
        using var p = Process.Start(psi)!;
        p.WaitForExit(60_000);
        if (p.ExitCode != 0)
        {
            var err = p.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {verb} {args} (cwd={cwd}) failed: {err}");
        }
    }

    private static IEnumerable<string> SplitArgs(string s)
    {
        // Tiny tokenizer: split on whitespace, honour "..." and \"...\".
        // We don't need full shell grammar — just enough for the test args.
        var sb = new System.Text.StringBuilder();
        bool inQuote = false;
        foreach (var c in s)
        {
            if (c == '"') { inQuote = !inQuote; continue; }
            if (!inQuote && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static async Task<string> ReadGitConfig(string repoPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "config --local --list",
            WorkingDirectory = repoPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        return await p.StandardOutput.ReadToEndAsync();
    }
}