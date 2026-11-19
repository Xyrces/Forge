using System.Diagnostics;
using Forge.AgentTools;
using Forge.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public class GitWorktreeServiceTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _bareDir;
    private readonly GitWorktreeService _service;

    public GitWorktreeServiceTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("gw");
        _bareDir = _workDir + "-bare.git";
        Directory.CreateDirectory(_workDir);
        _service = new GitWorktreeService(
            new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance);

        InitRepo(_workDir);
    }

    [Fact]
    public async Task CleanIntegrationRequiresExactPublishedFirstParentAndUntamperedMergeTree()
    {
        Assert.Equal(0, RunGit(_workDir, "checkout -b feature"));
        File.WriteAllText(Path.Combine(_workDir, "feature.txt"), "published");
        RunGit(_workDir, "add -A"); RunGit(_workDir, "commit -qm feature");
        var published = RunGitForResult(_workDir, "rev-parse HEAD").Stdout;
        var tree = RunGitForResult(_workDir, "rev-parse HEAD^{tree}").Stdout;
        RunGit(_workDir, "checkout main");
        File.WriteAllText(Path.Combine(_workDir, "upstream.txt"), "upstream");
        RunGit(_workDir, "add -A"); RunGit(_workDir, "commit -qm upstream");
        RunGit(_workDir, "push origin main");
        RunGit(_workDir, "checkout feature"); RunGit(_workDir, "merge --no-ff main -m integrated");
        RunGit(_workDir, "push origin feature");
        var head = RunGitForResult(_workDir, "rev-parse HEAD").Stdout;
        var proof = await _service.VerifyCleanIntegrationAsync(published, head, tree, default);
        Assert.Equal(RunGitForResult(_workDir, "rev-parse HEAD^{tree}").Stdout, proof.Tree);
        File.WriteAllText(Path.Combine(_workDir, "feature.txt"), "unverified resolution edit");
        RunGit(_workDir, "add -A"); RunGit(_workDir, "commit --amend --no-edit");
        RunGit(_workDir, "push --force origin feature");
        var edited = RunGitForResult(_workDir, "rev-parse HEAD").Stdout;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.VerifyCleanIntegrationAsync(published, edited, tree, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.VerifyCleanIntegrationAsync(published, head, new string('a', 40), default));
    }

    [Fact]
    public async Task DefaultVerificationProfileUsesPinnedSourceRatherThanCurrentCheckout()
    {
        File.WriteAllText(Path.Combine(_workDir, "project.csproj"), "<Project />");
        RunGit(_workDir, "add -A"); RunGit(_workDir, "commit -qm dotnet"); RunGit(_workDir, "push origin main");
        var head = RunGitForResult(_workDir, "rev-parse HEAD").Stdout;
        var expected = RunVerification.DefaultCommands(_workDir);
        RunGit(_workDir, "checkout HEAD~1");
        Assert.Empty(RunVerification.DefaultCommands(_workDir));
        Assert.Equal(expected, await _service.DefaultVerificationCommandsAtHeadAsync(head, default));
    }

    [Fact]
    public async Task MergedIntegrationCannotUseItsOwnHistoryAsEvidenceOfTrustedBase()
    {
        RunGit(_workDir, "checkout -b feature");
        File.WriteAllText(Path.Combine(_workDir, "feature.txt"), "published");
        RunGit(_workDir, "add -A"); RunGit(_workDir, "commit -qm feature");
        var original = RunGitForResult(_workDir, "rev-parse HEAD").Stdout;
        var tree = RunGitForResult(_workDir, "rev-parse HEAD^{tree}").Stdout;
        RunGit(_workDir, "checkout -b side main");
        File.WriteAllText(Path.Combine(_workDir, "side.txt"), "not previously on main");
        RunGit(_workDir, "add -A"); RunGit(_workDir, "commit -qm side");
        RunGit(_workDir, "checkout feature"); RunGit(_workDir, "merge --no-ff side -m integrated");
        var head = RunGitForResult(_workDir, "rev-parse HEAD").Stdout;
        RunGit(_workDir, "push origin feature");
        RunGit(_workDir, "checkout main"); RunGit(_workDir, "merge --no-ff feature -m merged-pr");
        var merged = RunGitForResult(_workDir, "rev-parse HEAD").Stdout;
        RunGit(_workDir, "push origin main");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.VerifyCleanIntegrationAsync(original, head, tree, default, merged));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
        try { Directory.Delete(_bareDir, recursive: true); } catch { }
    }

    private static int RunGit(string dir, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static GitResult RunGitForResult(string dir, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new GitResult(p.ExitCode, stdout.Trim());
    }

    private readonly record struct GitResult(int ExitCode, string Stdout);

    private void InitRepo(string dir)
    {
        RunGit(dir, "init -q -b main");
        RunGit(dir, "config user.email test@example.com");
        RunGit(dir, "config user.name Test");
        File.WriteAllText(Path.Combine(dir, "README.md"), "# init");
        RunGit(dir, "add -A");
        RunGit(dir, "commit -q -m initial");

        // Create a bare clone so we have a valid 'origin' remote
        // that the sync method can fetch from
        RunGit(dir, $"clone --bare {dir} {_bareDir}");
        RunGit(dir, $"remote add origin {_bareDir}");
        RunGit(dir, "fetch origin");
    }

    [Fact]
    public async Task ReviewSnapshotPinsExternalHeadWithoutChangingDirtyEngineeringCheckout()
    {
        var engineering = await _service.CreateAsync("engineering", "main");
        var oldHead = await GetHeadShaAsync(engineering);
        File.WriteAllText(Path.Combine(engineering, "README.md"), "uncommitted engineering work");
        File.WriteAllText(Path.Combine(_workDir, "external.txt"), "new remote revision");
        Assert.Equal(0, RunGit(_workDir, "add external.txt"));
        Assert.Equal(0, RunGit(_workDir, "commit -qm external"));
        Assert.Equal(0, RunGit(_workDir, "push origin main"));
        var head = await GetHeadShaAsync(_workDir);
        var snapshot = await _service.CreateReviewSnapshotAsync("engineering", "attempt-1", head);
        Assert.Equal(head, await GetHeadShaAsync(snapshot));
        Assert.Equal("HEAD", RunGitForResult(snapshot, "rev-parse --abbrev-ref HEAD").Stdout);
        Assert.Equal("new remote revision", File.ReadAllText(Path.Combine(snapshot, "external.txt")));
        Assert.Equal(oldHead, await GetHeadShaAsync(engineering));
        Assert.Equal("uncommitted engineering work", File.ReadAllText(Path.Combine(engineering, "README.md")));
        File.WriteAllText(Path.Combine(snapshot, "README.md"), "do not overwrite");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateReviewSnapshotAsync("engineering", "attempt-1", head));
        Assert.Equal("do not overwrite", File.ReadAllText(Path.Combine(snapshot, "README.md")));
        var next = await _service.CreateReviewSnapshotAsync("engineering", "attempt-2", head);
        Assert.NotEqual(snapshot, next);
        Assert.Equal(head, await GetHeadShaAsync(next));
    }

    [Fact]
    public async Task ReviewCleanupRemovesOnlyDetachedGeneratedSnapshot()
    {
        var engineering = await _service.CreateAsync("engineering", "main");
        var head = await GetHeadShaAsync(engineering);
        var snapshot = await _service.CreateReviewSnapshotAsync("engineering", "cleanup", head);
        File.WriteAllText(Path.Combine(snapshot, "generated.txt"), "review build output");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RemoveReviewSnapshotAsync(engineering));
        await _service.RemoveReviewSnapshotAsync(snapshot);
        await _service.RemoveReviewSnapshotAsync(snapshot); // retry after deletion is idempotent
        Assert.False(Directory.Exists(snapshot));
        Assert.True(Directory.Exists(engineering));
        Assert.DoesNotContain(snapshot, RunGitForResult(_workDir, "worktree list --porcelain").Stdout);
    }

    [Fact]
    public async Task AppIsolatedReviewCleanupPreservesOtherWorkAndSupportsSubsequentRounds()
    {
        if (!OperatingSystem.IsLinux()) return;
        var engineering = await _service.CreateAsync("engineering", "main");
        var head = await GetHeadShaAsync(engineering);
        var isolated = new GitWorktreeService(
            new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance,
            publisherToken: () => throw new InvalidOperationException("Cleanup must not request credentials"),
            publisherRepository: "https://github.com/Xyrces/Forge");
        var configPath = Path.Combine(_workDir, ".git", "config");
        var config = File.ReadAllText(configPath);
        var outside = Path.Combine(_workDir, "outside-review");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "preserve.txt"), "keep");
        for (var round = 0; round < 2; round++)
        {
            var snapshot = await _service.CreateReviewSnapshotAsync("engineering", "isolated-" + round, head);
            var registration = Path.Combine(_workDir, ".git", "worktrees", Path.GetFileName(snapshot));
            Directory.CreateSymbolicLink(Path.Combine(snapshot, "outside-link"), outside);
            // Invalid shared config proves cleanup never executes Git against repository configuration.
            File.WriteAllText(configPath, "[broken configuration");
            try { await isolated.RemoveReviewSnapshotAsync(snapshot); }
            finally { File.WriteAllText(configPath, config); }
            await isolated.RemoveReviewSnapshotAsync(snapshot);
            Assert.False(Directory.Exists(snapshot));
            Assert.False(Directory.Exists(registration));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "preserve.txt")));
            Assert.Equal(head, await GetHeadShaAsync(engineering));
            Assert.Contains(engineering, RunGitForResult(_workDir, "worktree list --porcelain").Stdout);
            Assert.DoesNotContain(snapshot, RunGitForResult(_workDir, "worktree list --porcelain").Stdout);
        }
        var interrupted = await _service.CreateReviewSnapshotAsync("engineering", "interrupted", head);
        var remainingRegistration = Path.Combine(_workDir, ".git", "worktrees", Path.GetFileName(interrupted));
        Directory.Delete(interrupted, recursive: true);
        await isolated.RemoveReviewSnapshotAsync(interrupted);
        Assert.False(Directory.Exists(remainingRegistration));
        await Assert.ThrowsAsync<InvalidOperationException>(() => isolated.RemoveReviewSnapshotAsync(engineering));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppIsolatedCleanupResumesPartiallyDeletedQuarantinesAcrossRounds(bool partialRegistration)
    {
        if (!OperatingSystem.IsLinux()) return;
        var engineering = await _service.CreateAsync("engineering", "main");
        var head = await GetHeadShaAsync(engineering);
        var isolated = new GitWorktreeService(
            new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance,
            publisherToken: () => throw new InvalidOperationException("Cleanup must not request credentials"),
            publisherRepository: "https://github.com/Xyrces/Forge");
        for (var round = 0; round < 2; round++)
        {
            var snapshot = await _service.CreateReviewSnapshotAsync("engineering", "partial-" + round, head);
            var receipt = isolated.PrepareReviewSnapshotRetirement(snapshot)!;
            var savedReceipt = System.Text.Json.JsonSerializer.Serialize(receipt);
            var registration = Path.Combine(_workDir, ".git", "worktrees", Path.GetFileName(snapshot));
            var checkoutTrash = snapshot + ".retiring-" + receipt.Nonce;
            var registrationTrash = Path.Combine(_workDir, ".git", "forge-review-retired", Path.GetFileName(snapshot) + "-" + receipt.Nonce);
            Directory.Move(snapshot, checkoutTrash);
            Directory.CreateDirectory(Path.GetDirectoryName(registrationTrash)!);
            Directory.Move(registration, registrationTrash);
            if (partialRegistration)
            {
                Directory.Delete(checkoutTrash, recursive: true);
                File.Delete(Path.Combine(registrationTrash, "gitdir"));
                File.Delete(Path.Combine(registrationTrash, "HEAD"));
            }
            else File.Delete(Path.Combine(checkoutTrash, ".git"));
            // A fresh service/controller resumes solely from its previously persisted receipt,
            // not from proof files already removed by the interrupted recursive deletion.
            var recovered = System.Text.Json.JsonSerializer.Deserialize<ReviewSnapshotRetirement>(savedReceipt)!;
            await isolated.RemoveReviewSnapshotAsync(snapshot, retirement: recovered);
            await isolated.RemoveReviewSnapshotAsync(snapshot, retirement: recovered);
            Assert.False(Directory.Exists(checkoutTrash));
            Assert.False(Directory.Exists(registrationTrash));
            Assert.False(Directory.Exists(registration));
            Assert.Equal(head, await GetHeadShaAsync(engineering));
            Assert.Contains(engineering, RunGitForResult(_workDir, "worktree list --porcelain").Stdout);
        }
    }

    [Fact]
    public async Task AppIsolatedReviewCleanupSupportsExplicitSiblingRoot()
    {
        if (!OperatingSystem.IsLinux()) return;
        var sibling = _workDir + "-reviews";
        var options = new WorkspaceOptions { Root = _workDir, WorktreeRoot = sibling, DefaultBranch = "main" };
        var creator = new GitWorktreeService(options, NullLogger<GitWorktreeService>.Instance);
        var isolated = new GitWorktreeService(options, NullLogger<GitWorktreeService>.Instance,
            publisherToken: () => throw new InvalidOperationException("Cleanup must not request credentials"),
            publisherRepository: "https://github.com/Xyrces/Forge");
        try
        {
            var head = await GetHeadShaAsync(_workDir);
            var snapshot = await creator.CreateReviewSnapshotAsync("task", "sibling", head);
            await isolated.RemoveReviewSnapshotAsync(snapshot);
            await isolated.RemoveReviewSnapshotAsync(snapshot);
            Assert.False(Directory.Exists(snapshot));
            Assert.False(Directory.Exists(Path.Combine(_workDir, ".git", "worktrees", Path.GetFileName(snapshot))));
            Assert.Equal(head, await GetHeadShaAsync(_workDir));
            Assert.Throws<InvalidOperationException>(() => IsolatedGitMetadata.RemoveDetachedReviewSnapshot(
                _workDir, Path.Combine(_workDir, ".git"), Path.Combine(_workDir, ".git", "review-" + new string('a', 64))));
        }
        finally { if (Directory.Exists(sibling)) Directory.Delete(sibling, recursive: true); }
    }

    [Fact]
    public async Task AppIsolatedReviewCleanupRejectsChangedBacklinkAndLinkedParent()
    {
        if (!OperatingSystem.IsLinux()) return;
        var head = await GetHeadShaAsync(_workDir);
        var snapshot = await _service.CreateReviewSnapshotAsync("engineering", "backlink", head);
        var registration = Path.Combine(_workDir, ".git", "worktrees", Path.GetFileName(snapshot));
        var backlink = Path.Combine(registration, "gitdir");
        var original = File.ReadAllText(backlink);
        File.WriteAllText(backlink, Path.Combine(_workDir, "unrelated", ".git"));
        Assert.Throws<InvalidOperationException>(() => IsolatedGitMetadata.RemoveDetachedReviewSnapshot(_workDir, _service.WorktreeRoot, snapshot));
        Assert.True(Directory.Exists(snapshot));
        File.WriteAllText(backlink, original);
        var linked = Path.Combine(_workDir, "linked-worktrees");
        Directory.CreateSymbolicLink(linked, _service.WorktreeRoot);
        Assert.Throws<InvalidOperationException>(() => IsolatedGitMetadata.RemoveDetachedReviewSnapshot(_workDir, linked, Path.Combine(linked, Path.GetFileName(snapshot))));
        Assert.True(Directory.Exists(snapshot));
        IsolatedGitMetadata.RemoveDetachedReviewSnapshot(_workDir, _service.WorktreeRoot, snapshot);
    }

    [Fact]
    public async Task ConfiguredPublisherRefusesInvalidScopeBeforeMintingToken()
    {
        var tokenRequested = false;
        var service = new GitWorktreeService(
            new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance,
            publisherToken: () => { tokenRequested = true; return Task.FromResult<string?>("unused"); },
            publisherRepository: "https://example.invalid/owner/repo");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateFreshAsync("wrong-origin", "main"));
        Assert.Contains("canonical GitHub HTTPS repository", error.Message);
        Assert.False(tokenRequested);
    }

    [Fact]
    public async Task CancelledFreshCheckoutDoesNotCreateWorktree()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.CreateFreshAsync("cancelled", "main", cancellation.Token));
        Assert.False(Directory.Exists(_service.WorktreePathFor("cancelled")));
    }

    [Fact]
    public async Task FreshCheckoutFetchesRemoteWithoutMovingLocalBase()
    {
        var old = RunGitForResult(_workDir, "rev-parse HEAD").Stdout;
        var other = Path.Combine(_workDir, "upstream");
        Assert.Equal(0, RunGit(_workDir, $"clone -q {_bareDir} {other}"));
        RunGit(other, "config user.email test@example.com");
        RunGit(other, "config user.name Test");
        File.WriteAllText(Path.Combine(other, "fresh.txt"), "latest");
        RunGit(other, "add fresh.txt"); RunGit(other, "commit -qm latest");
        RunGit(other, "push -q origin main");
        var expected = RunGitForResult(other, "rev-parse HEAD").Stdout;
        var path = await _service.CreateFreshAsync("fresh", "main");
        Assert.Equal(expected, RunGitForResult(path, "rev-parse HEAD").Stdout);
        Assert.Equal(old, RunGitForResult(_workDir, "rev-parse HEAD").Stdout);
    }

    [Fact]
    public async Task FreshCheckoutRefusesFailedFetchAndPreservesOrphanBranch()
    {
        RunGit(_workDir, "branch agent/orphan");
        var before = RunGitForResult(_workDir, "rev-parse agent/orphan").Stdout;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateFreshAsync("orphan", "main"));
        Assert.Equal(before, RunGitForResult(_workDir, "rev-parse agent/orphan").Stdout);
        Assert.False(Directory.Exists(_service.WorktreePathFor("orphan")));
        RunGit(_workDir, "remote remove origin");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateFreshAsync("unavailable", "main"));
        Assert.False(Directory.Exists(_service.WorktreePathFor("unavailable")));
    }

    [Fact]
    public async Task DiffAndAheadCount_StaleLocalMain_DoNotFalsePositive()
    {
        // Regression (porthorizon task-7, 2026-07-29): the worktree's
        // LOCAL main ref is stale, but origin/main moved forward and
        // HEAD sits exactly on fresh origin/main. A diff against the
        // local ref shows the upstream commits as if they were the
        // agent's own ("self-committed" false positive). The honest
        // answer is: zero files, zero ahead.
        var worktreePath = _service.WorktreePathFor("t-stale");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-stale", "main");

        // Move origin/main forward from ANOTHER clone (local main
        // in the worktree stays put).
        var other = Path.Combine(_workDir, "other-clone");
        RunGit(_workDir, $"clone -q {_bareDir} {other}");
        RunGit(other, "config user.email test@example.com");
        RunGit(other, "config user.name Test");
        File.WriteAllText(Path.Combine(other, "upstream.txt"), "upstream work");
        RunGit(other, "add -A");
        RunGit(other, "commit -q -m upstream");
        RunGit(other, "push -q origin main");

        // The worktree fetches (picks up origin/main) and its task
        // branch is synced onto fresh origin/main — local main never
        // moves.
        RunGit(worktreePath, "fetch -q origin");
        RunGit(worktreePath, "reset -q --hard origin/main");
        // Sanity: local main really is behind.
        var behind = RunGitForResult(worktreePath, "rev-list --count main..origin/main");
        Assert.Equal("1", behind.Stdout);

        var diff = await _service.GetDiffStatsAsync(worktreePath, "main", CancellationToken.None);
        Assert.Equal(string.Empty, diff.Summary);
        Assert.Equal(0, await _service.GetAheadCountAsync(worktreePath, "main", CancellationToken.None));

        // And real work still registers.
        File.WriteAllText(Path.Combine(worktreePath, "mine.txt"), "mine");
        RunGit(worktreePath, "add -A");
        RunGit(worktreePath, "commit -q -m mine");
        Assert.Equal(1, await _service.GetAheadCountAsync(worktreePath, "main", CancellationToken.None));
    }

    [Fact]
    public async Task Push_DetachedHead_Throws()
    {
        // The push pushes the BRANCH REF; a detached HEAD means the
        // agent's commits are not on the task branch — fail loud
        // instead of silently pushing the wrong commits.
        var worktreePath = _service.WorktreePathFor("t-detach");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-detach", "main");
        RunGit(worktreePath, "checkout -q --detach HEAD");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.PushAsync(worktreePath, "agent/t-detach", CancellationToken.None));
    }

    [Fact]
    public async Task Commit_WithNoChanges_ReturnsNoChangesOutcome()
    {
        var worktreePath = _service.WorktreePathFor("t-1");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-1", "main");
        var result = await _service.CommitAllAsync(worktreePath, "msg");
        Assert.Equal(CommitOutcome.NoChanges, result.Outcome);
        Assert.False(result.HasChanges);
    }

    [Fact]
    public async Task Commit_WithNewFile_ReturnsCreatedOutcome()
    {
        var worktreePath = _service.WorktreePathFor("t-2");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-2", "main");
        File.WriteAllText(Path.Combine(worktreePath, "x.txt"), "hello");
        var result = await _service.CommitAllAsync(worktreePath, "msg");
        Assert.Equal(CommitOutcome.Created, result.Outcome);
        Assert.True(result.HasChanges);
    }


    [Fact]
    public async Task SyncWorktreeToRefAsync_RejectsRemoteRefStartingWithDash()
    {
        var worktreePath = _service.WorktreePathFor("t-inject-dash");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-inject-dash", "main");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-inject-dash", "origin/--upload-pack=malicious"));
        Assert.Contains("must not start with '-'", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Also test leading dash on remoteName portion (after split)
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-inject-dash-2", "-origin/main"));
        Assert.Contains("must not start with '-'", ex2.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncWorktreeToRefAsync_RejectsRemoteRefWithWhitespace()
    {
        var worktreePath = _service.WorktreePathFor("t-inject-ws");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-inject-ws", "main");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-inject-ws", "origin/agent/task x"));
        Assert.Contains("must not contain whitespace", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Tab variant (newline in refPath after split)
        var ex2 = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-inject-ws-2", "ori/gin\tagent/task"));
        Assert.Contains("must not contain whitespace", ex2.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncWorktreeToRefAsync_RejectsRefPathContainingOnlyDash()
    {
        var worktreePath = _service.WorktreePathFor("t-inject-dash-only");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-inject-dash-only", "main");

        // After splitting "origin/-", refPath is "-" which starts with '-'
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-inject-dash-only", "origin/-"));
        Assert.Contains("must not start with '-'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncWorktreeToRefAsync_RejectsEmptyRefPath()
    {
        var worktreePath = _service.WorktreePathFor("t-inject-empty");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-inject-empty", "main");

        // remoteRef "origin/" splits to remoteName="origin", refPath=""
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-inject-empty", "origin/"));
        Assert.Contains("must not be empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }



    [Fact]
    public async Task CreateAsync_FreshTask_WorktreeBranchAtDefaultTip()
    {
        var defaultHeadSha = await GetHeadShaAsync(_workDir);

        var worktreePath = _service.WorktreePathFor("t-fresh-noop");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-fresh-noop", "main");

        var worktreeSha = await GetHeadShaAsync(worktreePath);
        Assert.Equal(defaultHeadSha, worktreeSha);
    }

    [Fact]
    public async Task CreateAsync_Reuse_AbortsInProgressMergeFromKilledRound()
    {        // Live 2026-08-01 (task-18/364): a conflict-sync round killed
        // mid-merge leaves MERGE_HEAD + unmerged paths; the reuse
        // path's checkout -B then fails, MAF swallows the throw, and
        // every rework dispatch phantom-completes at checkpoint
        // Claimed. Reuse must abort the dead merge first.
        var taskId = "t-killed-merge";
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        var worktreePath = await _service.CreateAsync(taskId, "main");

        // Build a conflicting branch and start a merge that conflicts.
        File.WriteAllText(Path.Combine(_workDir, "README.md"), "# main change");
        RunGit(_workDir, "add -A");
        RunGit(_workDir, "commit -q -m main-change");
        File.WriteAllText(Path.Combine(worktreePath, "README.md"), "# branch change");
        RunGit(worktreePath, "add -A");
        RunGit(worktreePath, "commit -q -m branch-change");
        Assert.NotEqual(0, RunGit(worktreePath, "merge main"));
        Assert.Equal(0, RunGitForResult(worktreePath, "rev-parse -q --verify MERGE_HEAD").ExitCode);

        var reused = await _service.CreateAsync(taskId, "main", branchOverride: $"agent/{taskId}");

        Assert.Equal(worktreePath, reused);
        Assert.NotEqual(0, RunGitForResult(worktreePath, "rev-parse -q --verify MERGE_HEAD").ExitCode);
        Assert.Equal(0, RunGitForResult(worktreePath, "diff --quiet").ExitCode);
    }

    [Fact]
    public async Task RemoveAsync_DeletesSyncBaseRef_WhenPresent()
    {
        var taskId = "t-sync-cleanup";
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync(taskId, "main");

        // Plant a per-task sync-base ref, exactly as SyncWorktreeToRefAsync would.
        var localRef = "refs/forge/sync-base/" + taskId;
        RunGit(_workDir, $"update-ref {localRef} HEAD");

        // Confirm the ref exists before removal.
        var beforeVerify = RunGitForResult(_workDir, $"show-ref --verify {localRef}");
        Assert.Equal(0, beforeVerify.ExitCode);

        await _service.RemoveAsync(taskId);

        // Confirm the ref is gone after removal.
        var afterVerify = RunGitForResult(_workDir, $"show-ref --verify {localRef}");
        Assert.NotEqual(0, afterVerify.ExitCode);
    }

    [Fact]
    public async Task RemoveAsync_DoesNotThrow_WhenSyncBaseRefAbsent()
    {
        var taskId = "t-no-sync-ref";
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync(taskId, "main");

        // No sync-base ref planted — should not throw.
        var ex = await Record.ExceptionAsync(() => _service.RemoveAsync(taskId));
        Assert.Null(ex);
    }

    [Fact]
    public async Task RemoveAsync_DeletesSyncBaseRef_WithSanitizedTaskId()
    {
        // Use a taskId containing characters that Sanitize transforms
        // (tilde, colon, question mark all become '_'), so the ref path
        // RemoveAsync computes internally (via its own Sanitize call)
        // must match the path SyncWorktreeToRefAsync would create.
        // This guards against a future one-sided Sanitize refactor.
        var rawTaskId = "t~:sync?cleanup";
        var sanitizedTaskId = "t__sync_cleanup";
        var localRef = "refs/forge/sync-base/" + sanitizedTaskId;

        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync(rawTaskId, "main");

        // Plant the ref at the sanitized path, as SyncWorktreeToRefAsync would.
        RunGit(_workDir, "update-ref " + localRef + " HEAD");

        // Confirm the ref exists before removal.
        var beforeVerify = RunGitForResult(_workDir, "show-ref --verify " + localRef);
        Assert.Equal(0, beforeVerify.ExitCode);

        await _service.RemoveAsync(rawTaskId);

        // Confirm the ref is gone after removal.
        var afterVerify = RunGitForResult(_workDir, "show-ref --verify " + localRef);
        Assert.NotEqual(0, afterVerify.ExitCode);
    }

    [Fact]
    public async Task SyncWorktreeToRefAsync_DivergedBranch_SyncsToRef()
    {
        var initialSha = await GetHeadShaAsync(_workDir);

        var worktreePath = _service.WorktreePathFor("t-diverged");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-diverged", "main");

        // Create a feature branch from the initial commit, add a commit,
        // push it. This simulates the PR head that a rework round should sync to.
        var rc = RunGit(_workDir, "checkout -b agent/task-X");
        Assert.Equal(0, rc);

        File.WriteAllText(Path.Combine(_workDir, "task-x-feature.txt"), "feature work");
        rc = RunGit(_workDir, "add task-x-feature.txt");
        Assert.Equal(0, rc);

        rc = RunGit(_workDir, "commit -q -m feature");
        Assert.Equal(0, rc);

        rc = RunGit(_workDir, "push origin agent/task-X");
        Assert.Equal(0, rc);

        var featureBranchSha = await GetHeadShaAsync(_workDir);
        Assert.NotEqual(initialSha, featureBranchSha);

        // Verify worktree is still at the initial commit (diverged from feature branch)
        var beforeSha = await GetHeadShaAsync(worktreePath);
        Assert.Equal(initialSha, beforeSha);

        // Sync the worktree to the remote feature branch ref
        await _service.SyncWorktreeToRefAsync(worktreePath, "t-diverged", "origin/agent/task-X");

        var afterSha = await GetHeadShaAsync(worktreePath);
        Assert.Equal(featureBranchSha, afterSha);
    }

    [Fact]
    public async Task SyncWorktreeToRefAsync_FreshTask_NoOpWhenAlreadyAtRef()
    {
        var worktreePath = _service.WorktreePathFor("t-fresh-sync");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-fresh-sync", "main");

        var pushResult = RunGit(_workDir, "push origin main");
        Assert.Equal(0, pushResult);

        var beforeSha = await GetHeadShaAsync(worktreePath);

        await _service.SyncWorktreeToRefAsync(worktreePath, "t-fresh-sync", "origin/main");

        var afterSha = await GetHeadShaAsync(worktreePath);
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    public async Task SyncWorktreeToRefAsync_DetachedHead_Throws()
    {
        var worktreePath = _service.WorktreePathFor("t-detached");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-detached", "main");

        var sha = await GetHeadShaAsync(worktreePath);
        RunGit(worktreePath, $"checkout --detach {sha}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncWorktreeToRefAsync(worktreePath, "t-detached", "origin/main"));
        Assert.Contains("detached HEAD", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncWorktreeToRefAsync_ForceUpdatedRef_SyncsAgain()
    {
        var initialSha = await GetHeadShaAsync(_workDir);

        var worktreePath = _service.WorktreePathFor("t-force");
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        await _service.CreateAsync("t-force", "main");

        // Create a feature branch, add a commit, push it — simulates first round
        var rc = RunGit(_workDir, "checkout -b agent/task-force");
        Assert.Equal(0, rc);

        File.WriteAllText(Path.Combine(_workDir, "round1.txt"), "first round");
        rc = RunGit(_workDir, "add round1.txt");
        Assert.Equal(0, rc);
        rc = RunGit(_workDir, "commit -q -m round1");
        Assert.Equal(0, rc);
        rc = RunGit(_workDir, "push origin agent/task-force");
        Assert.Equal(0, rc);

        var round1Sha = await GetHeadShaAsync(_workDir);
        Assert.NotEqual(initialSha, round1Sha);

        // First sync — worktree moves to round1
        await _service.SyncWorktreeToRefAsync(worktreePath, "t-force", "origin/agent/task-force");
        var afterFirstSync = await GetHeadShaAsync(worktreePath);
        Assert.Equal(round1Sha, afterFirstSync);

        // Simulate a force-push (rebase): amend the commit on the feature branch
        rc = RunGit(_workDir, "reset --soft HEAD~1");
        Assert.Equal(0, rc);
        File.WriteAllText(Path.Combine(_workDir, "round2.txt"), "second round (force-pushed)");
        rc = RunGit(_workDir, "add round2.txt");
        Assert.Equal(0, rc);
        rc = RunGit(_workDir, "commit -q -m round2-rebased");
        Assert.Equal(0, rc);

        var round2Sha = await GetHeadShaAsync(_workDir);
        Assert.NotEqual(round1Sha, round2Sha);

        // Force-push to the same remote branch
        rc = RunGit(_workDir, "push --force origin agent/task-force");
        Assert.Equal(0, rc);

        // Second sync — worktree must move to round2 (force-updated ref)
        await _service.SyncWorktreeToRefAsync(worktreePath, "t-force", "origin/agent/task-force");
        var afterSecondSync = await GetHeadShaAsync(worktreePath);
        Assert.Equal(round2Sha, afterSecondSync);
    }

    private async Task<string> GetHeadShaAsync(string repoPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "rev-parse HEAD",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout.Trim();
    }

    [Fact]
    public async Task IsAncestorAsync_DetectsDivergence()
    {
        // The rework divergence guard's probe (2026-08-01 task-377):
        // a branch reset onto main does NOT contain the PR head.
        var taskId = "t-ancestor";
        Directory.CreateDirectory(Path.Combine(_workDir, ".wt"));
        var worktreePath = await _service.CreateAsync(taskId, "main");

        // Diverge: local branch gets a commit main lacks.
        File.WriteAllText(Path.Combine(worktreePath, "a.txt"), "branch work");
        RunGit(worktreePath, "add -A");
        RunGit(worktreePath, "commit -q -m branch-work");

        Assert.True(await _service.IsAncestorAsync(worktreePath, "main", "HEAD"));
        Assert.False(await _service.IsAncestorAsync(worktreePath, "HEAD", "main"));
    }
}
