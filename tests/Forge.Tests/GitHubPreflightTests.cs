using System.Net;
using System.Text.Json;
using Forge.Configuration;
using Forge.Core;
using Forge.Projects;
using Xunit;

namespace Forge.Tests;

public sealed class GitHubPreflightTests
{
    [Fact]
    public async Task ProbesBothAppRolesForEveryRegisteredConfiguredRepository()
    {
        var credentials = new Credentials();
        using var handler = new MetadataHandler();
        using var http = new HttpClient(handler);
        var result = await new GitHubPreflight(http, credentials).CheckAsync(
            [Project("first"), Project("second")], new GitHubOptions(), default);

        Assert.Empty(result.Failed);
        Assert.Equal(4, result.Passed.Count);
        Assert.Equal(4, credentials.AppRequests.Count);
        Assert.Equal(0, credentials.LegacyRequests);
        Assert.Equal(10, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Equal(2, handler.Requests.Count(request => request.Path.Contains("/actions/")));
        Assert.All(handler.Requests.Where(request => request.Path.Contains("/actions/")),
            request => Assert.StartsWith("publisher-", request.Token));
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("duplicate")]
    [InlineData("reviewer")]
    [InlineData("actions")]
    [InlineData("wrong-repository")]
    public async Task AppFailuresAreExplicitAndNeverFallBackOrDiscloseCredentials(string failure)
    {
        var credentials = new Credentials { Failure = failure };
        using var handler = new MetadataHandler { Failure = failure };
        using var http = new HttpClient(handler);
        var result = await new GitHubPreflight(http, credentials).CheckAsync(
            [Project("first")], new GitHubOptions { Token = "valid-legacy-secret" }, default);

        Assert.NotEmpty(result.Failed);
        Assert.Equal(0, credentials.LegacyRequests);
        var output = string.Join("\n", result.Failed.Concat(result.Passed));
        Assert.DoesNotContain("private-test-secret", output);
        Assert.DoesNotContain("valid-legacy-secret", output);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        if (failure is "configuration" or "duplicate") Assert.Empty(handler.Requests);
        if (failure == "actions") Assert.Contains(result.Failed, message => message.Contains("publisher App Actions metadata"));
        if (failure == "reviewer") Assert.Contains(result.Failed, message => message.Contains("reviewer App installation credentials"));
    }

    [Fact]
    public async Task TokenOnlyRepositoryRetainsExplicitHumanReview()
    {
        var credentials = new Credentials { Legacy = true, Mode = RequiredReview.Human };
        using var handler = new MetadataHandler();
        using var http = new HttpClient(handler);
        var result = await new GitHubPreflight(http, credentials).CheckAsync(
            [Project("legacy")], new GitHubOptions(), default);

        Assert.Empty(result.Failed);
        Assert.Single(result.Passed);
        Assert.Empty(credentials.AppRequests);
        Assert.Equal(1, credentials.LegacyRequests);
        Assert.Single(handler.Requests);
        Assert.Equal("legacy-private-test-secret", handler.Requests[0].Token);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsAnAuthenticationFailure()
    {
        var credentials = new Credentials { Failure = "cancelled" };
        using var handler = new MetadataHandler();
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GitHubPreflight(http, credentials).CheckAsync([Project("first")], new GitHubOptions(), cancellation.Token));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("reviewer", 1)]
    [InlineData("actions", 1)]
    [InlineData("configuration", 1)]
    public async Task ControllerCheckUsesRegisteredRepositoriesAndFailsOnAppErrors(string? failure, int expectedExit)
    {
        var credentials = new Credentials { Failure = failure };
        using var handler = new MetadataHandler { Failure = failure };
        using var http = new HttpClient(handler);
        var options = new AgentOptions();
        options.Controller.Enabled = true;
        options.Llm.DefaultProvider = "fixture";
        options.Llm.Providers.Add(new LlmProviderOptions
        {
            Name = "fixture",
            DefaultModel = "fixture-model",
            BaseUrl = "https://model.invalid"
        });
        var stateReads = 0;
        var exit = await Forge.Orchestrator.Control.ControllerHost.CheckAsync(options,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            _ =>
            {
                stateReads++;
                return Task.FromResult<(int, IReadOnlyList<ProjectOptions>)>((IssueStore.CurrentSchemaVersion, [Project("registered-workload")]));
            },
            (projects, ct) => new GitHubPreflight(http, credentials).CheckAsync(projects, options.GitHub, ct));

        Assert.Equal(expectedExit, exit);
        Assert.Equal(1, stateReads);
        Assert.Equal(0, credentials.LegacyRequests);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        if (failure != "configuration")
        {
            Assert.Equal(2, credentials.AppRequests.Count);
            Assert.All(credentials.AppRequests, request => Assert.Equal("registered-workload", request.Repository));
            Assert.Contains(credentials.AppRequests, request => request.Reviewer);
            Assert.Contains(credentials.AppRequests, request => !request.Reviewer);
        }
    }

    private static ProjectOptions Project(string name) => new() { Id = name, RepoUrl = "https://github.com/owner/" + name };

    [Theory]
    [InlineData(RequiredReview.Automated)]
    [InlineData(RequiredReview.Both)]
    public async Task TokenOnlyAutomaticPolicyFailsOnlyForAffectedProject(RequiredReview mode)
    {
        var credentials = new Credentials { Legacy = true, Mode = mode };
        using var handler = new MetadataHandler();
        using var http = new HttpClient(handler);
        var result = await new GitHubPreflight(http, credentials).CheckAsync(
            [Project("legacy"), Project("human-only")], new GitHubOptions(), default);
        Assert.Contains("independent reviewer App", Assert.Single(result.Failed));
        Assert.Contains("human-only", Assert.Single(result.Passed));
        Assert.Equal(1, credentials.LegacyRequests);
    }

    private sealed class Credentials : IGitHubPreflightCredentials
    {
        public string? Failure { get; init; }
        public bool Legacy { get; init; }
        public RequiredReview Mode { get; init; }
        public Task<RequiredReview> ReviewModeAsync(string projectId, CancellationToken ct) =>
            Task.FromResult(projectId == "human-only" ? RequiredReview.Human : Mode);
        public int LegacyRequests { get; private set; }
        public List<(string Repository, bool Reviewer)> AppRequests { get; } = [];

        public GitHubAppIdentities.Repository? FindApps(string owner, string repository)
        {
            if (Failure == "configuration") throw new InvalidOperationException("private-test-secret");
            if (Legacy) return null;
            var publisher = new GitHubAppIdentities.App(1, 10, "publisher", "/not-read/publisher.pem");
            var reviewer = Failure == "duplicate" ? publisher : new GitHubAppIdentities.App(2, 20, "reviewer", "/not-read/reviewer.pem");
            return new(owner, repository, publisher, reviewer);
        }

        public Task<string> AppTokenAsync(GitHubAppIdentities.Repository repository, bool reviewer, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            AppRequests.Add((repository.Name, reviewer));
            if (Failure == "reviewer" && reviewer) throw new InvalidOperationException("private-test-secret");
            return Task.FromResult((reviewer ? "reviewer-" : "publisher-") + repository.Name + "-private-test-secret");
        }

        public Task<string?> LegacyTokenAsync(string projectId, CancellationToken ct)
        {
            LegacyRequests++;
            return Task.FromResult<string?>("legacy-private-test-secret");
        }
    }

    private sealed class MetadataHandler : HttpMessageHandler
    {
        public string? Failure { get; init; }
        public List<(HttpMethod Method, string Path, string Token)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path, request.Headers.Authorization?.Parameter ?? ""));
            Assert.Equal("api.github.com", request.RequestUri.Host);
            var scope = string.Join('/', path.Split('/').Skip(2).Take(2));
            var body = path.Contains("/pulls") ? "[]"
                : path.Contains("/actions/") ? "{\"workflows\":[]}"
                : JsonSerializer.Serialize(new { full_name = Failure == "wrong-repository" ? "other/repo" : scope });
            var status = Failure == "actions" && path.Contains("/actions/") ? HttpStatusCode.Forbidden : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
