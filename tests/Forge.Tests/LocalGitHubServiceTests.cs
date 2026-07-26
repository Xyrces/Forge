using System.Threading.Tasks;
using Xunit;

namespace Forge.Tests;

public sealed class LocalGitHubServiceTests
{
    [Fact]
    public async Task GetOpenPullRequestForBranchAsync_ReturnsOpenPr()
    {
        // Arrange
        var svc = new LocalGitHubService("/tmp/fake-remote", "owner", "repo");
        svc.RegisterPushedBranch("agent/task-1", "sha123");
        await svc.CreatePullRequestAsync("title", "body", "agent/task-1");

        // Act
        var pr = await svc.GetOpenPullRequestForBranchAsync("agent/task-1");

        // Assert
        Assert.NotNull(pr);
    }

    [Fact]
    public async Task GetOpenPullRequestForBranchAsync_ReturnsNull_WhenMerged()
    {
        // Arrange
        var svc = new LocalGitHubService("/tmp/fake-remote", "owner", "repo");
        svc.RegisterPushedBranch("agent/task-2", "sha456");
        var created = await svc.CreatePullRequestAsync("title", "body", "agent/task-2");
        await svc.MergePullRequestAsync(created.Number);

        // Act
        var pr = await svc.GetOpenPullRequestForBranchAsync("agent/task-2");

        // Assert
        Assert.Null(pr);
    }

    [Fact]
    public async Task GetOpenPullRequestForBranchAsync_ReturnsNull_WhenBranchNotFound()
    {
        // Arrange
        var svc = new LocalGitHubService("/tmp/fake-remote", "owner", "repo");

        // Act
        var pr = await svc.GetOpenPullRequestForBranchAsync("nonexistent-branch");

        // Assert
        Assert.Null(pr);
    }

    [Fact]
    public async Task GetOpenPullRequestForBranchAsync_ReturnsOpenPr_WhenOtherPrMerged()
    {
        // Arrange
        var svc = new LocalGitHubService("/tmp/fake-remote", "owner", "repo");
        svc.RegisterPushedBranch("agent/task-merged", "sha1");
        var mergedPr = await svc.CreatePullRequestAsync("merged pr", "body", "agent/task-merged");
        await svc.MergePullRequestAsync(mergedPr.Number);

        svc.RegisterPushedBranch("agent/task-open", "sha2");
        await svc.CreatePullRequestAsync("open pr", "body", "agent/task-open");

        // Act
        var merged = await svc.GetOpenPullRequestForBranchAsync("agent/task-merged");
        var open = await svc.GetOpenPullRequestForBranchAsync("agent/task-open");

        // Assert
        Assert.Null(merged);
        Assert.NotNull(open);
    }
}
