using System.Diagnostics;
using Forge.AgentTools;
using Xunit;

namespace Forge.Tests;

public sealed class IsolatedGitMetadataTests
{
    [Fact]
    public void ConflictOnSecondMutableFilePublishesNeitherFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TempRoot.Instance.NewDirectory("git-batch-conflict");
        var first = Path.Combine(root, "a-ref"); var second = Path.Combine(root, "b-ref");
        var update = Path.Combine(root, "proposed");
        File.WriteAllText(first, "original"); File.WriteAllText(second, "newer-worker-value"); File.WriteAllText(update, "proposed");
        var expected = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("original"));
        Assert.Throws<InvalidOperationException>(() => AtomicGitData.UpdateMany([
            new(first, expected, update), new(second, expected, update)]));
        Assert.Equal("original", File.ReadAllText(first));
        Assert.Equal("newer-worker-value", File.ReadAllText(second));
        Assert.False(File.Exists(first + ".lock")); Assert.False(File.Exists(second + ".lock"));
    }

    [Fact]
    public async Task TwoWorkersKeepIndependentCommitsAndReadOnlySnapshotsNeverOverwriteNewerRefs()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TempRoot.Instance.NewDirectory("two-worker-git");
        await Git(root, "init", "-b", "main");
        await Git(root, "config", "user.name", "Fixture");
        await Git(root, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(root, "source.txt"), "original");
        await Git(root, "add", "-A"); await Git(root, "commit", "-m", "Initial");
        var one = Path.Combine(root, ".wt", "one"); var two = Path.Combine(root, ".wt", "two");
        await Git(root, "worktree", "add", "-b", "worker-one", one);
        await Git(root, "worktree", "add", "-b", "worker-two", two);
        using var reader = new IsolatedGitMetadata(root);
        using var first = new IsolatedGitMetadata(one, trustedRepositoryRoot: root);
        using var second = new IsolatedGitMetadata(two, trustedRepositoryRoot: root);
        File.WriteAllText(Path.Combine(one, "one.txt"), "first worker");
        File.WriteAllText(Path.Combine(two, "two.txt"), "second worker");
        await PrivateGit(first, "add", "-A"); await PrivateGit(first, "commit", "-m", "First worker");
        await PrivateGit(second, "add", "-A"); await PrivateGit(second, "commit", "-m", "Second worker");
        await Task.WhenAll(Task.Run(first.PublishData), Task.Run(second.PublishData));
        reader.PublishData();
        Assert.Contains("one.txt", await Git(root, "ls-tree", "--name-only", "worker-one"));
        Assert.Contains("two.txt", await Git(root, "ls-tree", "--name-only", "worker-two"));
        using var competingOne = new IsolatedGitMetadata(root);
        using var competingTwo = new IsolatedGitMetadata(root);
        await PrivateGit(competingOne, "update-ref", "refs/heads/main", "worker-one");
        await PrivateGit(competingTwo, "update-ref", "refs/heads/main", "worker-two");
        competingOne.PublishData();
        Assert.Throws<InvalidOperationException>(() => competingTwo.PublishData());
        Assert.Equal(await Git(root, "rev-parse", "worker-one"), await Git(root, "rev-parse", "main"));
    }

    [Fact]
    public async Task NewWorktreeMarkerRequiresExplicitAuthorizedRoot()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TempRoot.Instance.NewDirectory("worktree-confinement");
        await Git(root, "init", "-b", "main");
        using var isolated = new IsolatedGitMetadata(root);
        var metadata = Path.Combine(isolated.CommonDirectory, "worktrees", "new-worker");
        Directory.CreateDirectory(metadata);
        File.WriteAllText(Path.Combine(metadata, "gitdir"), Path.Combine(root, "new-worker", ".git"));
        Assert.Throws<InvalidOperationException>(() => isolated.PublishData());
        Assert.False(Directory.Exists(Path.Combine(root, "new-worker")));
        Assert.False(Directory.Exists(Path.Combine(root, ".git", "worktrees")));
    }

    private static async Task PrivateGit(IsolatedGitMetadata isolated, params string[] arguments)
    {
        var start = ControllerGitRunner.NewProcess(); isolated.Configure(start);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var result = await ControllerGitRunner.ExecuteAsync(start, default);
        Assert.True(result.ExitCode == 0, result.Stderr);
    }

    [Fact]
    public async Task OptionalReadOnlyRepositoryTiming()
    {
        var repository = Environment.GetEnvironmentVariable("FORGE_GIT_ISOLATION_BENCHMARK_REPO");
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(repository)) return;
        var elapsed = Stopwatch.StartNew();
        using var isolated = new IsolatedGitMetadata(repository);
        var start = ControllerGitRunner.NewProcess(); isolated.Configure(start);
        start.ArgumentList.Add("rev-parse"); start.ArgumentList.Add("HEAD");
        Assert.Equal(0, (await ControllerGitRunner.ExecuteAsync(start, default)).ExitCode);
        Console.WriteLine($"Private Git import and HEAD read: {elapsed.Elapsed.TotalMilliseconds:F0}ms");
    }

    [Fact]
    public async Task ImportsOnlyDataAndRoundTripsIndexAndCommit()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = TempRoot.Instance.NewDirectory("private-git");
        await Git(root, "init", "-b", "main");
        await Git(root, "config", "user.name", "Fixture");
        await Git(root, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(root, "one.txt"), "one");
        await Git(root, "add", "-A");
        await Git(root, "commit", "-m", "Initial");
        await Git(root, "config", "remote.origin.pushurl", "https://example.invalid/fixture");
        await Git(root, "config", "filter.fixture.required", "true");
        await Git(root, "config", "credential.helper", "fixture-disabled");
        File.WriteAllText(Path.Combine(root, "two.txt"), "two");
        using (var isolated = new IsolatedGitMetadata(root, "https://github.com/owner/repo"))
        {
            var config = File.ReadAllText(Path.Combine(isolated.CommonDirectory, "config"));
            Assert.DoesNotContain("pushurl", config);
            Assert.DoesNotContain("filter.fixture", config);
            Assert.DoesNotContain("fixture-disabled", config);
            var start = ControllerGitRunner.NewProcess();
            start.Environment["GIT_EXTERNAL_DIFF"] = "fixture-disabled";
            isolated.Configure(start);
            Assert.False(start.Environment.ContainsKey("GIT_EXTERNAL_DIFF"));
            Assert.Equal("/usr/bin/git", start.FileName);
            Assert.False(start.WorkingDirectory.StartsWith(root, StringComparison.Ordinal));
            start.ArgumentList.Add("add"); start.ArgumentList.Add("-A");
            Assert.Equal(0, (await ControllerGitRunner.ExecuteAsync(start, default)).ExitCode);
            isolated.PublishData();
        }
        using (var isolated = new IsolatedGitMetadata(root))
        {
            var start = ControllerGitRunner.NewProcess(); isolated.Configure(start);
            start.ArgumentList.Add("commit"); start.ArgumentList.Add("-m"); start.ArgumentList.Add("Second");
            var result = await ControllerGitRunner.ExecuteAsync(start, default);
            Assert.True(result.ExitCode == 0, result.Stderr);
            isolated.PublishData();
        }
        Assert.Contains("two.txt", await Git(root, "ls-tree", "--name-only", "HEAD"));
        var worktree = Path.Combine(root, ".wt", "task-one");
        using (var isolated = new IsolatedGitMetadata(root, permittedWorktreeRoot: Path.Combine(root, ".wt")))
        {
            var start = ControllerGitRunner.NewProcess(); isolated.Configure(start);
            foreach (var argument in new[] { "worktree", "add", "-b", "agent/task-one", worktree, "main" }) start.ArgumentList.Add(argument);
            var result = await ControllerGitRunner.ExecuteAsync(start, default);
            Assert.True(result.ExitCode == 0, result.Stderr);
            isolated.PublishData();
        }
        using (var isolated = new IsolatedGitMetadata(worktree))
        {
            var start = ControllerGitRunner.NewProcess(); isolated.Configure(start);
            start.ArgumentList.Add("rev-parse"); start.ArgumentList.Add("HEAD");
            Assert.Equal((await Git(root, "rev-parse", "HEAD")).Trim(), (await ControllerGitRunner.ExecuteAsync(start, default)).Stdout.Trim());
        }
        await Git(root, "update-ref", "refs/forge/temporary", "HEAD");
        using (var isolated = new IsolatedGitMetadata(root))
        {
            var start = ControllerGitRunner.NewProcess(); isolated.Configure(start);
            foreach (var argument in new[] { "update-ref", "-d", "refs/forge/temporary" }) start.ArgumentList.Add(argument);
            Assert.Equal(0, (await ControllerGitRunner.ExecuteAsync(start, default)).ExitCode);
            isolated.PublishData();
        }
        Assert.False(File.Exists(Path.Combine(root, ".git", "refs", "forge", "temporary")));
        var service = new GitWorktreeService(
            new Forge.Configuration.WorkspaceOptions { Root = root, WorktreeRoot = ".wt", DefaultBranch = "main" },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GitWorktreeService>.Instance,
            publisherToken: () => throw new InvalidOperationException("Local operation must not mint credentials."),
            publisherRepository: "https://github.com/owner/repo");
        await service.RemoveAsync("task-one");
        Assert.False(Directory.Exists(worktree));
    }

    private static async Task<string> Git(string root, params string[] args)
    {
        var start = ControllerGitRunner.NewProcess(); start.WorkingDirectory = root;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var result = await ControllerGitRunner.ExecuteAsync(start, default);
        Assert.True(result.ExitCode == 0, result.Stderr);
        return result.Stdout;
    }
}
