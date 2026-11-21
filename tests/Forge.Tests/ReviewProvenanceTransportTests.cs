using System.Net;
using System.Text;
using System.Text.Json;
using Octokit;
using Octokit.Internal;
using Xunit;

namespace Forge.Tests;

public sealed class ReviewProvenanceTransportTests
{
    private sealed class Handler(string head, bool markerAvailable, bool dateAvailable) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var query = input.RootElement.GetProperty("query").GetString()!;
            Assert.Contains("HEAD_REF_FORCE_PUSHED_EVENT", query);
            Assert.Contains("HEAD_REF_DELETED_EVENT", query);
            Assert.Contains("HEAD_REF_RESTORED_EVENT", query);
            Assert.Contains("BASE_REF_CHANGED_EVENT", query);
            Assert.Contains("REOPENED_EVENT", query);
            Assert.Contains("last:1", query);
            var body = JsonSerializer.Serialize(new { data = new { repository = new { databaseId = 123,
                pullRequest = new { databaseId = 6478831478L, headRefOid = head,
                    timelineItems = markerAvailable ? new { nodes = new[] { new { id = "reset-2" } } } : null } } } });
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (dateAvailable) response.Headers.Date = new DateTimeOffset(2026, 11, 19, 2, 27, 59, TimeSpan.Zero);
            return response;
        }
    }

    private static GitHubService Service(Handler handler)
    {
        var client = new GitHubClient(new Connection(new ProductHeaderValue("test"), new HttpClientAdapter(() => handler)));
        return new("owner", "repo", client, client);
    }

    [Fact]
    public async Task ReadsServerClockImmutableIdsAndResetMarkerTogether()
    {
        using var handler = new Handler("head", true, true);
        var boundary = await Service(handler).GetReviewObservationTimeAsync(159, "head");
        Assert.Equal(6478831478L, boundary.PullRequestId);
        Assert.Equal(123, boundary.RepositoryId);
        Assert.Equal("reset-2", boundary.ResetMarker);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 2, 27, 59, TimeSpan.Zero), boundary.ServerTime);
    }

    [Fact]
    public async Task MissingTimelineIsExplicitlyUnknownNotAnEmptyHistory()
    {
        using var handler = new Handler("head", false, true);
        Assert.Null((await Service(handler).GetReviewObservationTimeAsync(159, "head")).ResetMarker);
    }

    [Theory]
    [InlineData("moved", true)]
    [InlineData("head", false)]
    public async Task ChangedHeadOrMissingServerDateCannotEstablishBoundary(string head, bool date)
    {
        using var handler = new Handler(head, true, date);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(handler).GetReviewObservationTimeAsync(159, "head"));
    }
}
