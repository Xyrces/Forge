using System.Text.Json;
using Forge.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Octokit;
using Xunit;

namespace Forge.Tests;

public sealed class GitHubReviewLabelTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ManagedPrOpensWithAnAvailableHumanReviewLabel(bool exists, bool concurrentCreation)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var created = 0;
        var available = exists;
        app.Map("/{**path}", async (HttpContext context) =>
        {
            var path = context.Request.Path.Value!;
            if (path.EndsWith("/labels/" + GitHubService.HumanReviewLabel) && context.Request.Method == "GET")
                return available ? Results.Ok(new { name = GitHubService.HumanReviewLabel }) : Results.NotFound();
            if (path.EndsWith("/labels") && context.Request.Method == "POST")
            {
                var body = await JsonDocument.ParseAsync(context.Request.Body);
                Assert.Equal(GitHubService.HumanReviewLabel, body.RootElement.GetProperty("name").GetString());
                Assert.Contains("publisher-fixture", context.Request.Headers.Authorization.ToString());
                created++;
                available = true;
                return concurrentCreation ? Results.Json(new { message = "Validation Failed" }, statusCode: 422)
                    : Results.Ok(new { name = GitHubService.HumanReviewLabel });
            }
            if (path.EndsWith("/pulls") && context.Request.Method == "POST")
            {
                Assert.True(available);
                return Results.Ok(new { number = 5, title = "Test change" });
            }
            return Results.NotFound();
        });
        await app.StartAsync();
        var uri = new Uri($"http://127.0.0.1:{app.GetPort()}/");
        var publisher = new GitHubClient(new ProductHeaderValue("publisher"), uri) { Credentials = new Credentials("publisher-fixture") };
        var reviewer = new GitHubClient(new ProductHeaderValue("reviewer"), uri) { Credentials = new Credentials("reviewer-fixture") };
        var service = new GitHubService("owner", "repo", publisher, reviewer);
        Assert.Equal(5, (await service.CreatePullRequestAsync("Test change", "Review workflow test", "example")).Number);
        Assert.Equal(exists ? 0 : 1, created);
    }
}
