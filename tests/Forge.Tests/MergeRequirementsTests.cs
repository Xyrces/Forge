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
            if (Queries[^1].Contains("node(id:$id)"))
            {
                using var response = JsonDocument.Parse(body);
                if (response.RootElement.GetProperty("data").TryGetProperty("repository", out var repository))
                {
                    var id = json.RootElement.GetProperty("variables").GetProperty("id").GetString();
                    var thread = repository.GetProperty("pullRequest").GetProperty("reviewThreads").GetProperty("nodes")
                        .EnumerateArray().Single(t => t.GetProperty("id").GetString() == id);
                    body = JsonSerializer.Serialize(new { data = new { node = new { comments = thread.GetProperty("latest") } } });
                }
            }
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
        bool other = false, bool resolved = false, bool truncated = false, string last = "comment", string body = "Original finding",
        string originalHead = "older-head", string? lastAuthor = null, string? lastActorType = null, bool append = false) => new
        {
            id, isResolved = resolved, viewerCanResolve = true, path = "File.cs",
            comments = new { pageInfo = new { hasNextPage = truncated },
                nodes = other || append ? new[] { new { id = "comment", updatedAt = "2026-01-01T00:00:00Z", body = "Original finding", author = new { login = author, __typename = actorType } },
                    new { id = last, updatedAt = "2026-01-01T00:00:00Z", body, author = new { login = lastAuthor ?? (append ? author : "human"), __typename = lastActorType ?? (append ? actorType : "User") } } }
                : new[] { new { id = last, updatedAt = "2026-01-01T00:00:00Z", body, author = new { login = author, __typename = actorType } } } },
            original = new { nodes = new[] { new { body = "Original finding", originalCommit = new { oid = originalHead } } } },
            latest = new { nodes = new[] { new { id = last, body, author = new { login = lastAuthor ?? (other ? "human" : author),
                __typename = lastActorType ?? (other ? "User" : actorType) } } } }
        };
    private static string Page(object[] threads, string state = "CLEAN", string head = "head", string? next = null) =>
        JsonSerializer.Serialize(new { data = new { repository = new { pullRequest = new
        {
            headRefOid = head, isDraft = false, mergeStateStatus = state,
            reviewThreads = new { pageInfo = new { hasNextPage = next is not null, endCursor = next }, nodes = threads }
        } } } });

    private static string Version() => ReviewFindings.VersionOf(new[] { new ReviewCommentVersion("comment", "reviewer[bot]", "2026-01-01T00:00:00Z") });

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
        Assert.All(handler.Queries, query => Assert.DoesNotContain("body", query));
        Assert.All(result.Conversations, t => Assert.Empty(t.Body));
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
                    body: replied ? replyBody! : "finding", append: replied)]);
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
        var draft = new ReviewDraft(2, "head", "Approve", "Verified correction", [], [new("thread", "comment", "The corrected guard rejects an empty path.", Version())]);
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
                return Page([Thread("thread", last: posted is null ? "comment" : "receipt", body: posted ?? "Finding", append: posted is not null)]);
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
        var draft = new ReviewDraft(2, "head", "Approve", "Verified", [], [new("thread", "comment", "Guard corrected", Version())]);
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

    [Fact]
    public async Task HumanEvidenceReplyRecoversLostResponseAndNeverResolvesOrApprovesForHuman()
    {
        string? posted = null;
        var posts = 0;
        using var handler = new Handler((request, token) =>
        {
            var query = request.GetProperty("query").GetString()!;
            if (query.StartsWith("query"))
            {
                Assert.Equal("publisher", token);
                return Page([Thread("human-thread", "alice", "User", other: posted is not null,
                    last: posted is null ? "comment" : "reply", body: posted ?? "Please fix",
                    lastAuthor: posted is null ? "alice" : "reviewer", lastActorType: posted is null ? "User" : "Bot")]);
            }
            Assert.Equal("reviewer", token);
            Assert.Contains("addPullRequestReviewThreadReply", query);
            posted = request.GetProperty("variables").GetProperty("body").GetString();
            Assert.Contains("Verified correction at `head`", posted);
            posts++;
            throw new IOException("Lost accepted reply response");
        });
        var service = new Service(Client(handler, "publisher"), Client(handler, "reviewer"));
        var initial = await service.GetMergeRequirementsAsync(1, "head");
        var draft = new ReviewDraft(2, "head", "Approve", "Fixed", [],
            HumanThreadVersions: ReviewFindings.HumanConversationVersions(initial.Conversations, "head"),
            EvidenceReplies: [new("human-thread", "comment", "The validation now rejects invalid paths; regression passes.")]);
        await Assert.ThrowsAsync<IOException>(() => service.ResolveReviewedConversationsAsync(1, draft));
        var receipts = await service.ResolveReviewedConversationsAsync(1, draft);
        Assert.Equal(1, posts);
        var snapshot = await service.GetMergeRequirementsAsync(1, "head");
        Assert.Equal(1, snapshot.Unresolved);
        Assert.False(snapshot.AllowsMerge);
        Assert.Equal(snapshot.Conversations[0].Version, receipts["human-thread"]);
        Assert.DoesNotContain(handler.Queries, q => q.Contains("resolveReviewThread(input"));
    }

    [Fact]
    public async Task ChangedHumanConversationRejectsSavedEvidenceBeforePosting()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.StartsWith("query", request.GetProperty("query").GetString());
            return Page([Thread("human-thread", "alice", "User", last: "new-comment")]);
        });
        var service = new Service(Client(handler, "publisher"), Client(handler, "reviewer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveReviewedConversationsAsync(1,
            new(2, "head", "Approve", "Fixed", [], EvidenceReplies: [new("human-thread", "comment", "Verified")])));
    }

    [Fact]
    public void HumanEvidenceRequiresACompleteOlderFindingAndCannotClaimResolution()
    {
        var human = new ReviewConversation("human", false, true, "File.cs", "alice", "comment", false, "Finding",
            OriginalHead: "older-head");
        var output = "REVIEWER_ADDRESSED_THREAD: {\"threadId\":\"human\",\"lastCommentId\":\"comment\",\"reason\":\"Verified correction\"}";
        var draft = ReviewFindings.Create(2, "head", "Approve", output, "", [human], "reviewer[bot]");
        Assert.Single(draft.EvidenceReplies!);
        Assert.Null(draft.Resolutions);
        Assert.NotNull(draft.HumanThreadCheckpoint);
        foreach (var ineligible in new[] { human with { Complete = false }, human with { OriginalHead = "head" },
            human with { Author = "another[bot]" }, human with { OriginalHead = null } })
            Assert.Throws<InvalidOperationException>(() => ReviewFindings.Create(2, "head", "Approve", output, "", [ineligible], "reviewer[bot]"));
        Assert.Throws<InvalidOperationException>(() => ReviewFindings.Create(2, "head", "Approve",
            output.Replace("REVIEWER_ADDRESSED_THREAD:", "REVIEWER_RESOLVED_THREAD:"), "", [human], "reviewer[bot]"));

    }
    [Fact]
    public async Task DiscussionInputsArePaginatedBoundedAndNeverReadResolvedHistory()
    {
        var calls = 0;
        var versions = Enumerable.Range(0, 21).Select(i => new ReviewCommentVersion($"c{i}", "reviewer[bot]", "date")).ToArray();
        using var handler = new Handler((request, _) =>
        {
            calls++;
            Assert.Equal("open", request.GetProperty("variables").GetProperty("id").GetString());
            var first = !request.GetProperty("variables").TryGetProperty("cursor", out var cursor) || cursor.ValueKind == JsonValueKind.Null;
            return JsonSerializer.Serialize(new { data = new { node = new { comments = new {
                pageInfo = new { hasNextPage = first, endCursor = first ? "next" : null },
                nodes = versions.Skip(first ? 0 : 20).Take(first ? 20 : 1).Select(v => new {
                    id = v.Id, updatedAt = v.UpdatedAt, body = "Evidence", author = new { login = "reviewer", __typename = "Bot" } })
            } } } });
        });
        var service = new Service(Client(handler, "publisher"), Client(handler, "reviewer"));
        var observed = new ReviewConversation("open", false, true, "File.cs", "reviewer[bot]", "c20", false, "", Versions: versions, AssessmentComplete: false);
        var inputs = await service.GetReviewConversationInputsAsync([observed with { Id = "resolved", Resolved = true }, observed], "head");
        Assert.Equal(2, calls);
        Assert.True(Assert.Single(inputs).AssessmentComplete);
        Assert.Equal(21, inputs[0].Body.Split("Evidence").Length - 1);
    }

    [Fact]
    public async Task OversizedDiscussionStaysOpenAndCannotAuthorizeResolution()
    {
        using var handler = new Handler((_, _) => JsonSerializer.Serialize(new { data = new { node = new { comments = new {
            pageInfo = new { hasNextPage = false, endCursor = (string?)null },
            nodes = new[] { new { id = "comment", updatedAt = "date", body = new string('x', 80_001), author = new { login = "reviewer", __typename = "Bot" } } }
        } } } }));
        var service = new Service(Client(handler, "publisher"), Client(handler, "reviewer"));
        var observed = new ReviewConversation("large", false, true, "File.cs", "reviewer[bot]", "comment", false, "");
        var inputs = await service.GetReviewConversationInputsAsync([observed], "head");
        Assert.False(inputs[0].AssessmentComplete);
        Assert.Empty(inputs[0].Body);
        var draft = ReviewFindings.Create(1, "head", "Approve", "Approved", "", inputs, "reviewer[bot]");
        Assert.Equal("large", Assert.Single(draft.LimitedConversations!));
        Assert.Null(draft.Resolutions);
        Assert.Throws<InvalidOperationException>(() => ReviewFindings.Create(1, "head", "Approve",
            "REVIEWER_RESOLVED_THREAD: {\"threadId\":\"large\",\"lastCommentId\":\"comment\",\"reason\":\"verified\"}", "", inputs, "reviewer[bot]"));
    }

    [Fact]
    public async Task SameIdEditAfterAssessmentCannotBeResolved()
    {
        using var handler = new Handler((_, _) => Page([Thread("thread")]).Replace("2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z"));
        var service = new Service(Client(handler, "publisher"), Client(handler, "reviewer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveReviewedConversationsAsync(1,
            new(1, "head", "Approve", "Verified", [], [new("thread", "comment", "corrected", Version())])));
        Assert.DoesNotContain(handler.Queries, q => q.StartsWith("mutation"));
    }

    [Fact]
    public async Task CombinedDiscussionBudgetAndEditedEvidenceAreBounded()
    {
        using var handler = new Handler((request, _) => JsonSerializer.Serialize(new { data = new { node = new { comments = new {
            pageInfo = new { hasNextPage = false, endCursor = (string?)null },
            nodes = new[] { new { id = "comment", updatedAt = "date", body = new string('x', 30_000), author = new { login = "reviewer", __typename = "Bot" } } }
        } } } }));
        var service = new Service(Client(handler, "publisher"), Client(handler, "reviewer"));
        var observed = new ReviewConversation("a", false, true, "File.cs", "reviewer[bot]", "comment", false, "",
            Versions: [new("comment", "reviewer[bot]", "date")], AssessmentComplete: false);
        var inputs = await service.GetReviewConversationInputsAsync([observed, observed with { Id = "b" }, observed with { Id = "c" }], "head");
        Assert.Equal(2, inputs.Count(i => i.AssessmentComplete));
        Assert.True(inputs.Where(i => i.AssessmentComplete).Sum(i => ReviewFindings.ConversationPrompt(i, "head", "reviewer[bot]").Length + 2) < 80_000);
        Assert.Empty(inputs[2].Body);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetReviewConversationInputsAsync(
            [observed with { Versions = [new("comment", "reviewer[bot]", "previous-date")] }], "head"));
    }

    [Fact]
    public async Task SameIdEditAfterReceiptPreventsClosingEvenOnRecovery()
    {
        string? posted = null;
        using var handler = new Handler((request, _) =>
        {
            if (request.GetProperty("query").GetString()!.StartsWith("query"))
            {
                var page = Page([Thread("thread", append: posted is not null, last: posted is null ? "comment" : "receipt", body: posted ?? "Finding")]);
                return posted is null ? page : page.Replace("2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z");
            }
            Assert.Contains("addPullRequestReviewThreadReply", request.GetProperty("query").GetString());
            posted = request.GetProperty("variables").GetProperty("body").GetString();
            return "{\"data\":{\"addPullRequestReviewThreadReply\":{\"comment\":{\"id\":\"receipt\"}}}}";
        });
        var service = new Service(Client(handler, "publisher"), Client(handler, "reviewer"));
        var draft = new ReviewDraft(1, "head", "Approve", "Verified", [], [new("thread", "comment", "corrected", Version())]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveReviewedConversationsAsync(1, draft));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveReviewedConversationsAsync(1, draft));
        Assert.DoesNotContain(handler.Queries, q => q.Contains("resolveReviewThread(input"));
        Assert.Single(handler.Queries.Where(q => q.StartsWith("mutation")));
    }

}
