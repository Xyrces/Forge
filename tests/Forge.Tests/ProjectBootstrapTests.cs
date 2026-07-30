using Forge.Configuration;
using Forge.Projects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public class ForgesystemPathsTests
{
    [Fact]
    public void ResolveDataRoot_NoOverride_UsesPlatformEnvVar()
    {
        // We can't depend on a specific machine's path layout, but we can
        // assert that ResolveDataRoot returns an absolute path and is
        // idempotent.
        var p1 = ForgesystemPaths.ResolveDataRoot();
        var p2 = ForgesystemPaths.ResolveDataRoot();
        Assert.Equal(p1, p2);
        Assert.True(Path.IsPathRooted(p1));
    }

    [Fact]
    public void ResolveDataRoot_Override_FullPath()
    {
        var root = TempRoot.Instance.NewDirectory("test");
        try
        {
            var resolved = ForgesystemPaths.ResolveDataRoot(root);
            Assert.Equal(Path.GetFullPath(root), resolved);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Expected paths built with Path.Combine rather than hardcoded '\'
    // literals so this passes on both Windows and Linux CI runners.
    [Fact]
    public void ProjectDir_UnderDataRoot()
    {
        var data = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "/", "fake", "forge-data");
        var dir = ForgesystemPaths.ProjectDir(data, "x");
        Assert.Equal(Path.Combine(data, "projects", "x"), dir);
    }

    [Fact]
    public void IssuesDb_ForProject_NestedUnderState()
    {
        var data = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "/", "fake", "forge-data");
        var db = ForgesystemPaths.IssuesDb(data, "x");
        Assert.Equal(Path.Combine(data, "projects", "x", ".forge", "state", "issues.db"), db);
    }
}

public class ProjectBootstrapTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly ProjectBootstrap _bootstrap;

    public ProjectBootstrapTests()
    {
        _tempRoot = TempRoot.Instance.NewDirectory("bootstrap");
        Directory.CreateDirectory(_tempRoot);
        var cloner = new ProjectCloner(_tempRoot, NullLogger<ProjectCloner>.Instance);
        _bootstrap = new ProjectBootstrap(_tempRoot, cloner, null, NullLogger<ProjectBootstrap>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* tolerate */ }
    }

    [Fact]
    public void EnsureProject_EmptyRoot_AutoScaffoldsUnderDataRoot()
    {
        var project = new ProjectOptions { Id = "fresh", Name = "Fresh", Root = string.Empty };
        var result = _bootstrap.EnsureProject(project);
        Assert.NotEqual(string.Empty, result.Project.Root);
        Assert.True(Directory.Exists(result.Project.Root));
        Assert.True(Directory.Exists(Path.Combine(result.Project.Root, ".git")));
        Assert.True(File.Exists(Path.Combine(result.Project.Root, ".gitignore")));
        Assert.True(result.Created);
        Assert.True(result.InitializedAsGitRepo);
    }

    [Fact]
    public void EnsureProject_ExistingEmptyPath_InitializesRepo()
    {
        var userPath = Path.Combine(_tempRoot, "userproj");
        Directory.CreateDirectory(userPath);
        var project = new ProjectOptions { Id = "userproj", Root = userPath };
        var result = _bootstrap.EnsureProject(project);
        Assert.True(result.InitializedAsGitRepo);
        Assert.True(Directory.Exists(Path.Combine(userPath, ".git")));
        // git scaffolding adds one commit, so HEAD exists.
        Assert.True(File.Exists(Path.Combine(userPath, ".git", "HEAD")));
    }

    [Fact]
    public void EnsureProject_Idempotent()
    {
        var project = new ProjectOptions { Id = "idem", Root = string.Empty };
        var first = _bootstrap.EnsureProject(project);
        var second = _bootstrap.EnsureProject(project with { Root = first.Project.Root });
        Assert.Equal(first.Project.Root, second.Project.Root);
        Assert.False(second.InitializedAsGitRepo);
    }

    [Fact]
    public void EnsureProject_DefaultProject_UsesLegacyPortHorizonStateLayout()
    {
        var userPath = Path.Combine(_tempRoot, "legacy");
        Directory.CreateDirectory(userPath);
        var project = new ProjectOptions { Id = "default", Root = userPath };
        var result = _bootstrap.EnsureProject(project);
        Assert.Equal(Path.Combine(userPath, ".portHorizon", "state"), result.StateDirectory);
        Assert.Equal(Path.Combine(userPath, ".portHorizon", "state", "issues.db"), result.IssuesDbPath);
    }
}
