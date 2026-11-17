using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Dashboard.Features.Deployments;
using Forge.Dashboard.Services;
using Forge.Deploy;
using Xunit;

namespace Forge.Tests;

public sealed class DashboardContractTests
{
    [Fact]
    public async Task EveryApiDeploymentStatusCanBeReadByTheDashboardClient()
    {
        var candidates = Enum.GetValues<DeploymentStatus>().Select(status => new DeploymentCandidate(
            status.ToString(), "forge", "abc", null, status, DateTime.UtcNow, "operator", null, null, null, null, null)).ToArray();
        using var http = new HttpClient(new JsonHandler(JsonSerializer.Serialize(candidates, JsonSerializerOptions.Web)))
            { BaseAddress = new Uri("http://localhost") };
        var rows = await new DeploymentsClient(http).ListAsync(null, default);
        Assert.Equal(candidates.Select(c => c.Status.ToString()), rows.Select(r => r.Status));
    }

    [Fact]
    public async Task MissingHistoryIsAnErrorRatherThanAnEmptyList()
    {
        using var http = new HttpClient(new JsonHandler("null")) { BaseAddress = new Uri("http://localhost") };
        var client = new DeploymentsClient(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync(null, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListCommitsAsync("forge", default));
    }

    [Fact]
    public void LateCommitResultsOrFailuresCannotReplaceSelectedProject()
    {
        var selected = DeploymentsReducers.OnLoadCommits(new(), new("second"));
        Assert.Same(selected, DeploymentsReducers.OnCommitsLoaded(selected, new("first", [])));
        Assert.Same(selected, DeploymentsReducers.OnCommitsLoadFailed(selected, new("first", "unavailable")));
        var failed = DeploymentsReducers.OnCommitsLoadFailed(selected, new("second", "unavailable"));
        Assert.False(failed.CommitsLoading);
        Assert.Equal("unavailable", failed.CommitsError);
    }

    [Theory]
    [InlineData("https://github.com/Xyrces/Forge.git", "https://github.com/Xyrces/Forge/pull/124")]
    [InlineData("https://github.com/Xyrces/Forge.git/", "https://github.com/Xyrces/Forge/pull/124")]
    [InlineData("git@github.com:Xyrces/Forge.git", "https://github.com/Xyrces/Forge/pull/124")]
    [InlineData("ssh://git@github.com/Xyrces/Forge.git", "https://github.com/Xyrces/Forge/pull/124")]
    [InlineData("https://user:secret@github.com/Xyrces/Forge.git?token=secret", "https://github.com/Xyrces/Forge/pull/124")]
    [InlineData("file:///tmp/repo.git", null)]
    public void RepositoryCloneUrlsBecomeSafeWebLinks(string input, string? expected) =>
        Assert.Equal(expected, RepositoryLinks.PullRequest(input, "124"));

    [Fact]
    public void SelectedProjectNeverFallsBackToFirstRepository()
    {
        using var projects = JsonDocument.Parse("""[{"id":"first","repoUrl":"https://github.com/one/one.git"},{"id":"second","repoUrl":"https://github.com/two/two.git"}]""");
        Assert.Equal("https://github.com/two/two", RepositoryLinks.ForProject(projects.RootElement, "second"));
        Assert.Null(RepositoryLinks.ForProject(projects.RootElement, "missing"));
        Assert.Null(RepositoryLinks.ForProject(projects.RootElement, null));
    }

    [Fact]
    public void UnconfirmedGateNeverDisplaysOpen()
    {
        Assert.Equal("loading", GatePresentation.Label(null, "merge", true));
        Assert.Equal("unknown", GatePresentation.Label(new Dictionary<string, string>(), "merge", false));
        Assert.Equal("unknown", GatePresentation.Label(new Dictionary<string, string> { ["merge"] = "invalid" }, "merge", false));
        Assert.Equal("held", GatePresentation.Label(new Dictionary<string, string> { ["merge"] = "hold" }, "merge", false));
        Assert.Equal("open", GatePresentation.Label(new Dictionary<string, string> { ["merge"] = "open" }, "merge", false));
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
