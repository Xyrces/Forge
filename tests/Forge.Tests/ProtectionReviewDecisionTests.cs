using System.Net;
using System.Text;
using System.Text.Json;
using Octokit;
using Octokit.Internal;
using Xunit;

namespace Forge.Tests;

public sealed class ProtectionReviewDecisionTests
{
    [Theory]
    [InlineData("null", ProtectionReviewDecision.NotRequired)]
    [InlineData("\"APPROVED\"", ProtectionReviewDecision.Approved)]
    [InlineData("\"REVIEW_REQUIRED\"", ProtectionReviewDecision.ReviewRequired)]
    [InlineData("\"CHANGES_REQUESTED\"", ProtectionReviewDecision.ChangesRequested)]
    public async Task ReadsEffectiveDecisionUsingPublisherIdentityAtExactHead(string decision, ProtectionReviewDecision expected)
    {
        using var handler = new Handler("{\"data\":{\"repository\":{\"pullRequest\":{\"headRefOid\":\"head\",\"reviewDecision\":" + decision + "}}}}");
        using var adapter = new HttpClientAdapter(() => handler);
        var publisher = new GitHubClient(new Connection(new ProductHeaderValue("test"), adapter)) { Credentials = new Credentials("publisher-test-token") };
        var reviewer = new GitHubClient(new ProductHeaderValue("must-not-be-called"));
        var service = new GitHubService("owner", "repo", publisher, reviewer);
        Assert.Equal(expected, await service.GetProtectionReviewDecisionAsync(151, "head"));
        Assert.Equal("publisher-test-token", handler.Token);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.StartsWith("query(", body.RootElement.GetProperty("query").GetString());
        Assert.Equal("owner", body.RootElement.GetProperty("variables").GetProperty("owner").GetString());
        Assert.Equal(151, body.RootElement.GetProperty("variables").GetProperty("number").GetInt32());
    }

    [Theory]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"errors\":[{\"message\":\"private detail\"}]}")]
    [InlineData("{\"data\":{\"repository\":{\"pullRequest\":{\"headRefOid\":\"new-head\",\"reviewDecision\":\"APPROVED\"}}}}")]
    [InlineData("{\"data\":{\"repository\":{\"pullRequest\":{\"headRefOid\":\"head\",\"reviewDecision\":\"FUTURE_VALUE\"}}}}")]
    public void MissingStaleAndPartialErrorResponsesNeverAuthorizeMerge(string body)
    {
        var error = Assert.Throws<InvalidOperationException>(() => GitHubService.ParseProtectionReviewDecision(body, "head"));
        Assert.DoesNotContain("private detail", error.Message);
    }

    private sealed class Handler(string response) : HttpMessageHandler
    {
        public string? Token, Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://api.github.com/graphql", request.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Post, request.Method);
            Token = request.Headers.Authorization?.Parameter;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
