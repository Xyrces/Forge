using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Reviewer;
using Octokit;
using Octokit.Internal;
using Xunit;

namespace Forge.Tests;

public sealed class MergeRequirementsTests
{
    private sealed class Handler(Func<JsonElement, string, string> reply) : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Queries.Add(json.RootElement.GetProperty("query").GetString()!);
            var body = reply(json.RootElement, request.Headers.Authorization!.Parameter!);
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Service(GitHubClient publisher, GitHubClient reviewer) : GitHubService("o", "r", publisher, reviewer)
    {
        public override string? ReviewerLogin => "reviewer[bot]";
    }
    private static GitHubClient Client(HttpMessageHandler handler, string token) => new(new Connection(
        new ProductHeaderValue("test"), new HttpClientAdapter(() => handler))) { Credentials = new Credentials(token) };
    private static object Thread(string id, string author = "reviewer", string actorType = "Bot",
        bool other = false, bool resolved = false, bool truncated = false, string last = "comment", string body = "Original finding") => new
        {
            id, isResolved = resolved, viewerCanResolve = true, path = "File.cs",
            comments = new { pageInfo = new { hasNextPage = truncated },
                nodes = other ? new[] { new { id = "comment", author = new { login = author, __typename = actorType } },
                    new { id = last, author = new { login = "human", __typename = "User" } } }
                : new[] { new { id = last, author = new { login = author, __typename = actorType } } } },
            original = new { nodes = new[] { new { body = "Original finding" } } },
            latest = new { nodes = new[] { new { id = last, body } } }
        };
    private static string Page(object[] threads, string state = "CLEAN", string head = "head", string? next = null) =>
        JsonSerializer.Serialize(new { data = new { repository = new { pullRequest = new
        {
            headRefOid = head, isDraft = false, mergeStateStatus = state,
            reviewThreads = new { pageInfo = new { hasNextPage = next is not null, endCursor = next }, nodes = threads }
        } } } });

    [Fact]
    public async Task ReadsEveryPageAndCanonicalizesBotIdentityWithoutTrustingHumanSlug()
    {
        using var handler = new Handler((r, token) =>
        {
            Assert.Equal("publisher", token);
            return !r.GetProperty("variables").TryGetProperty("cursor", out var cursor) || cursor.ValueKind == JsonValueKind.Null
                ? Page([Thread("bot")], next: "next")
                : Page([Thread("human", actorType: "User"), Thread("replied", other: true), Thread("partial", truncated: true)]);
        });
        var service = new Service(Client(handler, "publisher"), Client(handler, "reviewer"));
        var result = await service.GetMergeRequirementsAsync(1, "head");
        Assert.Equal(4, result.Unresolved);
        Assert.False(result.AllowsMerge);
        Assert.Equal("reviewer[bot]", result.Conversations[0].Author);
        Assert.True(GitHubService.CanResolveConversation(result.Conversations[0], service.ReviewerLogin));
        Assert.All(result.Conversations.Skip(1), t => Assert.False(GitHubService.CanResolveConversation(t, service.ReviewerLogin)));
        Assert.Equal(2, handler.Queries.Count);
    }

    [Theory]
    [InlineData("BEHIND")] [InlineData("BLOCKED")] [InlineData("UNKNOWN")] [InlineData("DIRTY")] [InlineData("DRAFT")]
    public async Task UnmetGitHubRequirementsNeverPermitMerge(string state)
    {
        using var handler = new Handler((_, _) => Page([], state));
        var service = new Service(Client(handler, "publisher"), Client(handler, "reviewer"));
        Assert.False((await service.GetMergeRequirementsAsync(1, "head")).AllowsMerge);
    }

    [Fact]
    public async Task HeadChangesAndGraphqlErrorsFailClosed()
    {
        foreach (var response in new[] { Page([], head: "changed"), "{\"errors\":[{\"message\":\"denied\"}]}", Page([], state: "NEW_STATE") })
        {
            using var handler = new Handler((_, _) => response);
            var service = new Service(Client(handler, "publisher"), Client(handler, "reviewer"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetMergeRequirementsAsync(1, "head"));
        }
    }

    [Fact]
    public async Task VerifiedResolutionRepliesAsReviewerAndRecoversLostResolutionResponse()
    {
        var replied = false;
        var resolved = false;
        var replies = 0;
        var resolutions = 0;
        string? replyBody = null;
        using var handler = new Handler((request, token) =>
        {
            var query = request.GetProperty("query").GetString()!;
            if (query.StartsWith("query"))
            {
                Assert.Equal("publisher", token);
                return Page([Thread("thread", resolved: resolved, last: replied ? "reply" : "comment",
                    body: replied ? replyBody! : "finding")]);
            }
            Assert.Equal("reviewer", token);
            if (query.Contains("addPullRequestReviewThreadReply"))
            {
                Assert.Contains("Verified at", request.GetProperty("variables").GetProperty("body").GetString());
                replyBody = request.GetProperty("variables").GetProperty("body").GetString();
                replied = true; replies++;
                return "{\"data\":{\"addPullRequestReviewThreadReply\":{\"comment\":{\"id\":\"reply\"}}}}";
            }
            resolved = true; resolutions++;
            throw new IOException("Lost response after mutation");
        });
        var service = new Service(Client(handler, "publisher"), Client(handler, "reviewer"));
        var draft = new ReviewDraft(2, "head", "Approve", "Verified correction", [], [new("thread", "comment", "The corrected guard rejects an empty path.")]);
        await Assert.ThrowsAsync<IOException>(() => service.ResolveReviewedConversationsAsync(1, draft));
        await service.ResolveReviewedConversationsAsync(1, draft);
        Assert.Equal(1, replies);
        Assert.Equal(1, resolutions);
    }

    [Fact]
    public async Task HumanReplyAfterVerificationPreventsAutomaticResolution()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.StartsWith("query", request.GetProperty("query").GetString());
            return Page([Thread("thread", other: true, last: "human")]);
        });
        var service = new Service(Client(handler, "publisher"), Client(handler, "reviewer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveReviewedConversationsAsync(1,
            new(2, "head", "Approve", "Verified", [], [new("thread", "comment", "corrected")])));
    }

    [Fact]
    public async Task LostReplyResponseReusesAuthenticatedLatestReceipt()
    {
        string? posted = null;
        var posts = 0;
        var resolves = 0;
        using var handler = new Handler((request, _) =>
        {
            var query = request.GetProperty("query").GetString()!;
            if (query.StartsWith("query"))
                return Page([Thread("thread", last: posted is null ? "comment" : "receipt", body: posted ?? "Finding")]);
            if (query.Contains("addPullRequestReviewThreadReply"))
            {
                posted = request.GetProperty("variables").GetProperty("body").GetString();
                posts++;
                throw new IOException("Reply accepted but response lost");
            }
            resolves++;
            return "{\"data\":{\"resolveReviewThread\":{\"thread\":{\"isResolved\":true}}}}";
        });
        var service = new Service(Client(handler, "publisher"), Client(handler, "reviewer"));
        var draft = new ReviewDraft(2, "head", "Approve", "Verified", [], [new("thread", "comment", "Guard corrected")]);
        await Assert.ThrowsAsync<IOException>(() => service.ResolveReviewedConversationsAsync(1, draft));
        await service.ResolveReviewedConversationsAsync(1, draft);
        Assert.Equal(1, posts);
        Assert.Equal(1, resolves);
    }

    [Fact]
    public async Task NewCommentFromSameAppAfterVerificationReplyRemainsUnresolved()
    {
        var replied = false;
        using var handler = new Handler((request, _) =>
        {
            var query = request.GetProperty("query").GetString()!;
            if (query.StartsWith("query"))
                return Page([Thread("thread", last: replied ? "new-finding" : "comment", body: "Additional concern")]);
            Assert.Contains("addPullRequestReviewThreadReply", query);
            replied = true;
            return "{\"data\":{\"addPullRequestReviewThreadReply\":{\"comment\":{\"id\":\"receipt\"}}}}";
        });
        var service = new Service(Client(handler, "publisher"), Client(handler, "reviewer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveReviewedConversationsAsync(1,
            new(2, "head", "Approve", "Verified", [], [new("thread", "comment", "fixed")])));
        Assert.DoesNotContain(handler.Queries, q => q.Contains("resolveReviewThread(input"));
    }

    [Fact]
    public void ApprovalRequiresSpecificEvidenceForEveryEligibleThread()
    {
        var thread = new ReviewConversation("thread", false, true, "File.cs", "reviewer[bot]", "comment", false, "finding");
        Assert.Throws<InvalidOperationException>(() => ReviewFindings.Create(2, "head", "Approve", "Approved", "", [thread], "reviewer[bot]"));
        var output = "REVIEWER_RESOLVED_THREAD: {\"threadId\":\"thread\",\"lastCommentId\":\"comment\",\"reason\":\"Verified guard and test\"}\nREVIEWER_VERDICT: APPROVE";
        var draft = ReviewFindings.Create(2, "head", "Approve", output, "", [thread], "reviewer[bot]");
        Assert.Single(draft.Resolutions!);
        Assert.Throws<InvalidOperationException>(() => ReviewFindings.Create(2, "head", "Approve", output, "",
            [thread with { Author = "human" }], "reviewer[bot]"));
        Assert.Throws<InvalidOperationException>(() => ReviewFindings.Create(2, "head", "Approve", output, "",
            [thread with { LastCommentId = "new" }], "reviewer[bot]"));
    }
}
