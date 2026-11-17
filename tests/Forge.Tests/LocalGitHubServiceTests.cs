using System.Threading.Tasks;
using Xunit;

namespace Forge.Tests;

public sealed class LocalGitHubServiceTests
{
    [Fact]
    public async Task Merge_AfterRework_RequiresCurrentBranchHead()
    {
        var svc = new LocalGitHubService("/tmp/fake-remote", "owner", "repo");
        svc.RegisterPushedBranch("agent/rework", "original-head");
        var pr = await svc.CreatePullRequestAsync("title", "body", "agent/rework");
        svc.RegisterPushedBranch("agent/rework", "reworked-head");
        Assert.False(await svc.MergePullRequestAsync(pr.Number, expectedHeadSha: "original-head"));
        Assert.NotNull(await svc.GetOpenPullRequestForBranchAsync("agent/rework"));
        Assert.True(await svc.MergePullRequestAsync(pr.Number, expectedHeadSha: "reworked-head"));
    }

    [Fact]
    public async Task Merge_RejectsHeadDifferentFromVerifiedSha()
    {
        var svc = new LocalGitHubService("/tmp/fake-remote", "owner", "repo");
        svc.RegisterPushedBranch("agent/task-1", "new-head");
        var pr = await svc.CreatePullRequestAsync("title", "body", "agent/task-1");
        Assert.False(await svc.MergePullRequestAsync(pr.Number, expectedHeadSha: "reviewed-head"));
        Assert.NotNull(await svc.GetOpenPullRequestForBranchAsync("agent/task-1"));
        Assert.True(await svc.MergePullRequestAsync(pr.Number, expectedHeadSha: "new-head"));
    }

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
