using Forge.Orchestrator;
using Xunit;

namespace Forge.Tests;

public class GitHubOwnerRepoParseTests
{
    [Theory]
    [InlineData("https://github.com/Xyrces/Forge.git", "Xyrces", "Forge")]
    [InlineData("https://github.com/Xyrces/Forge", "Xyrces", "Forge")]
    [InlineData("https://github.com/Xyrces/Forge/", "Xyrces", "Forge")]
    [InlineData("git@github.com:Xyrces/Forge.git", "Xyrces", "Forge")]
    [InlineData("git@github.com:Xyrces/Forge", "Xyrces", "Forge")]
    [InlineData("https://github.com/some-org/deep.repo.name.git", "some-org", "deep.repo.name")]
    public void Parses_CommonShapes(string url, string owner, string repo)
    {
        var parsed = ProjectDispatchBundleFactory.ParseGitHubOwnerRepo(url);
        Assert.NotNull(parsed);
        Assert.Equal(owner, parsed!.Value.Owner);
        Assert.Equal(repo, parsed.Value.Repo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://gitlab.com/Xyrces/Forge.git")]
    [InlineData("https://github.com/onlyowner")]
    [InlineData("not a url")]
    public void Rejects_NonGitHub_Or_Unparseable(string? url)
    {
        Assert.Null(ProjectDispatchBundleFactory.ParseGitHubOwnerRepo(url));
    }
}
