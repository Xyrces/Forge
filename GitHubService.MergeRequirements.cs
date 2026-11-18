using System.Text.Json;
using Forge.Reviewer;
using Octokit;

namespace Forge;

public sealed record ReviewConversation(string Id, bool Resolved, bool CanResolve, string Path,
    string Author, string LastCommentId, bool HasOtherParticipants, string Body, string? LastBody = null);
public sealed record MergeRequirements(string State, IReadOnlyList<ReviewConversation> Conversations)
{
    public int Unresolved => Conversations.Count(t => !t.Resolved);
    public bool AllowsMerge => Unresolved == 0 && State is "CLEAN" or "HAS_HOOKS" or "UNSTABLE";
}

public partial class GitHubService
{
    public virtual async Task<MergeRequirements> GetMergeRequirementsAsync(int number, string head, CancellationToken ct = default)
    {
        List<ReviewConversation> threads = [];
        string? cursor = null;
        string? state = null;
        do
        {
            var root = await ReviewGraphAsync(_client, """
                query($owner:String!,$name:String!,$number:Int!,$cursor:String){repository(owner:$owner,name:$name){pullRequest(number:$number){
                  headRefOid isDraft mergeStateStatus reviewThreads(first:100,after:$cursor){pageInfo{hasNextPage endCursor} nodes{
                    id isResolved viewerCanResolve path comments(first:100){pageInfo{hasNextPage} nodes{id author{login __typename}}}
                    original:comments(first:1){nodes{body}} latest:comments(last:1){nodes{id body}}}}}}}
                """, new { owner = _owner, name = _repo, number, cursor }, ct);
            var pr = root.GetProperty("data").GetProperty("repository").GetProperty("pullRequest");
            if (pr.GetProperty("headRefOid").GetString() != head)
                throw new InvalidOperationException("PR revision changed while reading merge requirements.");
            state = pr.GetProperty("isDraft").GetBoolean() ? "DRAFT" : pr.GetProperty("mergeStateStatus").GetString();
            if (state is not ("CLEAN" or "HAS_HOOKS" or "UNSTABLE" or "BLOCKED" or "BEHIND" or "DIRTY" or "DRAFT" or "UNKNOWN"))
                throw new InvalidOperationException("GitHub returned an unknown merge requirement state.");
            var page = pr.GetProperty("reviewThreads");
            foreach (var row in page.GetProperty("nodes").EnumerateArray())
            {
                var comments = row.GetProperty("comments");
                var nodes = comments.GetProperty("nodes").EnumerateArray().ToArray();
                if (nodes.Length == 0) throw new InvalidOperationException("GitHub returned a review thread without its original comment.");
                static string Login(JsonElement node)
                {
                    var actor = node.GetProperty("author");
                    if (actor.ValueKind == JsonValueKind.Null) return "";
                    var login = actor.GetProperty("login").GetString() ?? "";
                    return actor.GetProperty("__typename").GetString() == "Bot" ? login + "[bot]" : login;
                }
                var author = Login(nodes[0]);
                var truncated = comments.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean();
                threads.Add(new(row.GetProperty("id").GetString()!, row.GetProperty("isResolved").GetBoolean(),
                    row.GetProperty("viewerCanResolve").GetBoolean(), row.GetProperty("path").GetString()!, author,
                    nodes[^1].GetProperty("id").GetString()!, truncated || nodes.Any(n => !string.Equals(Login(n), author, StringComparison.OrdinalIgnoreCase)),
                    row.GetProperty("original").GetProperty("nodes")[0].GetProperty("body").GetString() + "\n\n"
                        + row.GetProperty("latest").GetProperty("nodes")[0].GetProperty("body").GetString(),
                    row.GetProperty("latest").GetProperty("nodes")[0].GetProperty("body").GetString()));
            }
            var info = page.GetProperty("pageInfo");
            var next = info.GetProperty("hasNextPage").GetBoolean() ? info.GetProperty("endCursor").GetString() : null;
            if (info.GetProperty("hasNextPage").GetBoolean() && string.IsNullOrEmpty(next)
                || next == cursor && next is not null || threads.Count > 1000)
                throw new InvalidOperationException("GitHub review threads exceed the bounded merge check; operator review is required.");
            cursor = next;
        } while (cursor is not null);
        return new(state!, threads);
    }

    internal static bool CanResolveConversation(ReviewConversation thread, string? reviewer) => !thread.Resolved
        && thread.CanResolve && !thread.HasOtherParticipants && !string.IsNullOrWhiteSpace(reviewer)
        && string.Equals(thread.Author, reviewer, StringComparison.OrdinalIgnoreCase);

    public virtual async Task ResolveReviewedConversationsAsync(int number, ReviewDraft draft, CancellationToken ct = default)
    {
        if (draft.Resolutions is not { Count: > 0 }) return;
        if (_reviewClient is null || draft.Verdict != nameof(ReviewerVerdict.Approve))
            throw new InvalidOperationException("Review conversation resolution requires the independent reviewer's approval.");
        foreach (var resolution in draft.Resolutions)
        {
            var current = await GetMergeRequirementsAsync(number, draft.HeadSha, ct);
            var thread = current.Conversations.SingleOrDefault(t => t.Id == resolution.ThreadId)
                ?? throw new InvalidOperationException("Reviewed conversation is no longer available.");
            if (thread.Resolved) continue;
            var marker = ReviewFindings.ResolutionMarker(draft, resolution.ThreadId);
            var previouslyReplied = thread.LastBody?.EndsWith(marker, StringComparison.Ordinal) == true;
            var receiptId = thread.LastCommentId;
            if (!CanResolveConversation(thread, ReviewerLogin) || (!previouslyReplied && thread.LastCommentId != resolution.LastCommentId))
                throw new InvalidOperationException("Review conversation changed after verification; manual reconciliation is required.");
            if (!previouslyReplied)
            {
                var reply = await ReviewGraphAsync(_reviewClient,
                    "mutation($id:ID!,$body:String!){addPullRequestReviewThreadReply(input:{pullRequestReviewThreadId:$id,body:$body}){comment{id}}}",
                    new { id = thread.Id, body = $"Verified at `{draft.HeadSha}`. {resolution.Reason}\n\n{marker}" }, ct);
                receiptId = reply.GetProperty("data").GetProperty("addPullRequestReviewThreadReply").GetProperty("comment").GetProperty("id").GetString()
                    ?? throw new InvalidOperationException("GitHub did not identify the verification reply.");
            }
            // Recheck the head and participants after replying. GitHub remains the final authority on races at merge.
            current = await GetMergeRequirementsAsync(number, draft.HeadSha, ct);
            thread = current.Conversations.Single(t => t.Id == resolution.ThreadId);
            if (thread.Resolved) continue;
            if (!CanResolveConversation(thread, ReviewerLogin) || thread.LastCommentId != receiptId)
                throw new InvalidOperationException("Another participant joined the review conversation; leaving it open.");
            var result = await ReviewGraphAsync(_reviewClient,
                "mutation($id:ID!){resolveReviewThread(input:{threadId:$id}){thread{isResolved}}}", new { id = thread.Id }, ct);
            if (!result.GetProperty("data").GetProperty("resolveReviewThread").GetProperty("thread").GetProperty("isResolved").GetBoolean())
                throw new InvalidOperationException("GitHub did not confirm review conversation resolution.");
        }
    }

    private static async Task<JsonElement> ReviewGraphAsync(GitHubClient client, string query, object variables, CancellationToken ct)
    {
        var response = await client.Connection.Post<string>(new Uri("graphql", UriKind.Relative), new { query, variables },
            "application/json", "application/json", new Dictionary<string, string>(), ct);
        using var doc = JsonDocument.Parse(response.HttpResponse.Body as string ?? throw new InvalidOperationException("GitHub response is unavailable."));
        var root = doc.RootElement;
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null
            && (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() > 0))
            throw new InvalidOperationException("GitHub could not verify review conversations or merge requirements.");
        return root.Clone();
    }
}
