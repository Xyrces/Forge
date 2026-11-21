using System.Net;
using Forge.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Octokit;
using Xunit;

namespace Forge.Tests;
public sealed class GitHubReviewIdentityTests
{
    [Fact]
    public async Task FormalReviewUsesReviewerClientNotPublisher()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var reviewerRequests = 0;
        var publisherRequests = 0;
        app.MapPost("/{**path}", (HttpContext context) =>
        {
            Assert.EndsWith("/repos/owner/repo/pulls/1/reviews", context.Request.Path.Value);
            if (context.Request.Headers.Authorization.ToString().Contains("reviewer-fixture", StringComparison.Ordinal)) reviewerRequests++;
            else publisherRequests++;
            return Results.Json(new { id = 42 });
        });
        await app.StartAsync();
        var url = $"http://127.0.0.1:{app.GetPort()}/";
        var publisher = new GitHubClient(new ProductHeaderValue("publisher-test"), new Uri(url)) { Credentials = new Credentials("publisher-fixture") };
        var reviewer = new GitHubClient(new ProductHeaderValue("reviewer-test"), new Uri(url)) { Credentials = new Credentials("reviewer-fixture") };
        var service = new GitHubService("owner", "repo", publisher, reviewer);
        var id = await service.SubmitReviewAsync(1, new string('a', 40), "Independent review", PullRequestReviewState.Approved);
        Assert.Equal(42, id);
        Assert.Equal(1, reviewerRequests);
        Assert.Equal(0, publisherRequests);
    }
}
