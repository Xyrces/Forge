using System.Diagnostics;
using Forge.Configuration;
using Forge.Projects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class SourceFreshnessTests : IDisposable
{
    private readonly string _root = TempRoot.Instance.NewDirectory("source-freshness");
    private readonly string _source;
    private readonly string _remote;
    private readonly ProjectCloner _cloner;
    private readonly ProjectOptions _project;

    public SourceFreshnessTests()
    {
        _source = Path.Combine(_root, "upstream");
        _remote = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(_source);
        Git(_source, "init", "-b", "main");
        Git(_source, "config", "user.name", "Fixture");
        Git(_source, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(_source, "base.txt"), "base");
        Git(_source, "add", "base.txt"); Git(_source, "commit", "-m", "base");
        Git(_root, "clone", "--bare", _source, _remote);
        Git(_source, "remote", "add", "origin", _remote);
        _cloner = new ProjectCloner(_root, NullLogger<ProjectCloner>.Instance);
        _project = new ProjectOptions { Id = "fixture", RepoUrl = _remote, DefaultBranch = "main" };
    }

    private string Advance()
    {
        File.WriteAllText(Path.Combine(_source, "latest.txt"), "latest");
        Git(_source, "add", "latest.txt"); Git(_source, "commit", "-m", "advance");
        Git(_source, "push", "origin", "main");
        return Git(_source, "rev-parse", "HEAD");
    }

    [Fact]
    public async Task StartupRefreshesManagedCloneWithResolvedRoot()
    {
        var clone = await _cloner.CloneAsync(_project, null);
        _project.Root = clone.LocalPath;
        var expected = Advance();
        new ProjectBootstrap(_root, _cloner).EnsureProject(_project);
        Assert.Equal(expected, Git(clone.LocalPath, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task StartupRefreshFailurePreservesDashboardBootstrapAndLocalWork()
    {
        var clone = await _cloner.CloneAsync(_project, null);
        _project.Root = clone.LocalPath;
        File.WriteAllText(Path.Combine(clone.LocalPath, "local.txt"), "preserve");
        var before = Git(clone.LocalPath, "rev-parse", "HEAD");
        Advance();
        var result = new ProjectBootstrap(_root, _cloner).EnsureProject(_project);
        Assert.Equal(clone.LocalPath, result.Project.Root);
        Assert.Equal(before, Git(clone.LocalPath, "rev-parse", "HEAD"));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(clone.LocalPath, "local.txt")));
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("untracked")]
    [InlineData("wrong-branch")]
    [InlineData("local-ahead")]
    [InlineData("unavailable")]
    [InlineData("missing-origin")]
    public async Task RefreshFailurePreservesCheckout(string condition)
    {
        var clone = await _cloner.CloneAsync(_project, null);
        var path = clone.LocalPath;
        _project.Root = path;
        if (condition == "dirty") File.WriteAllText(Path.Combine(path, "base.txt"), "preserve");
        if (condition == "untracked") File.WriteAllText(Path.Combine(path, "local.txt"), "preserve");
        if (condition == "wrong-branch") Git(path, "checkout", "-b", "operator-work");
        if (condition == "local-ahead")
        {
            Git(path, "config", "user.name", "Fixture"); Git(path, "config", "user.email", "fixture@example.invalid");
            File.WriteAllText(Path.Combine(path, "local.txt"), "preserve");
            Git(path, "add", "local.txt"); Git(path, "commit", "-m", "local");
        }
        if (condition == "missing-origin") Git(path, "remote", "remove", "origin");
        if (condition == "unavailable") Git(path, "remote", "set-url", "origin", Path.Combine(_root, "absent.git"));
        var before = Git(path, "rev-parse", "HEAD");
        var status = Git(path, "status", "--porcelain");
        Advance();
        Assert.False((await _cloner.SyncAsync(_project, null)).Ok);
        Assert.Equal(before, Git(path, "rev-parse", "HEAD"));
        Assert.Equal(status, Git(path, "status", "--porcelain"));
    }

    [Fact]
    public async Task ExplicitRootRefreshDoesNotModifyAnotherManagedClone()
    {
        var clone = await _cloner.CloneAsync(_project, null);
        var before = Git(clone.LocalPath, "rev-parse", "HEAD");
        var other = Path.Combine(_root, "operator");
        Git(_root, "clone", _remote, other);
        _project.Root = other;
        var expected = Advance();
        Assert.True((await _cloner.SyncAsync(_project, null)).Ok);
        Assert.Equal(expected, Git(other, "rev-parse", "HEAD"));
        Assert.Equal(before, Git(clone.LocalPath, "rev-parse", "HEAD"));
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.Trim();
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
