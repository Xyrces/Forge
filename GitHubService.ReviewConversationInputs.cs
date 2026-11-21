using System.Text;
using Forge.Reviewer;

namespace Forge;

public partial class GitHubService
{
    internal const int ReviewConversationCharacterBudget = 80_000;
    internal const int ReviewConversationLimit = 40;

    /// <summary>Load bounded evidence only after a lightweight observation has established that review is needed.</summary>
    public virtual async Task<IReadOnlyList<ReviewConversation>> GetReviewConversationInputsAsync(
        IReadOnlyList<ReviewConversation> observations, string head, CancellationToken ct = default)
    {
        var remaining = ReviewConversationCharacterBudget - 256;
        var examined = 0;
        List<ReviewConversation> result = [];
        foreach (var observed in observations.Where(t => !t.Resolved))
        {
            var input = observed with { Body = "", AssessmentComplete = false };
            if (!observed.Complete || examined >= ReviewConversationLimit || remaining <= 0)
            {
                result.Add(input);
                continue;
            }
            examined++;
            string? cursor = null;
            var body = new StringBuilder();
            List<ReviewCommentVersion> versions = [];
            var oversized = false;
            do
            {
                var response = await ReviewGraphAsync(_client, """
                    query($id:ID!,$cursor:String){node(id:$id){... on PullRequestReviewThread{
                      comments(first:20,after:$cursor){pageInfo{hasNextPage endCursor}
                        nodes{id updatedAt body author{login __typename}}}}}}
                    """, new { id = observed.Id, cursor }, ct);
                var comments = response.GetProperty("data").GetProperty("node").GetProperty("comments");
                foreach (var comment in comments.GetProperty("nodes").EnumerateArray())
                {
                    var text = ReviewActor(comment) + ": " + comment.GetProperty("body").GetString();
                    if (text.Length + body.Length + 2 > remaining)
                    {
                        oversized = true;
                        break;
                    }
                    body.AppendLine(text).AppendLine();
                    versions.Add(CommentVersion(comment));
                }
                if (oversized) break;
                var page = comments.GetProperty("pageInfo");
                var hasNext = page.GetProperty("hasNextPage").GetBoolean();
                var next = hasNext ? page.GetProperty("endCursor").GetString() : null;
                if (hasNext && (string.IsNullOrEmpty(next) || next == cursor) || versions.Count > 100)
                    throw new InvalidOperationException("Review discussion pagination could not establish a complete conversation.");
                cursor = next;
            } while (cursor is not null);
            if (!oversized)
            {
                if (observed.Versions is not null && ReviewFindings.VersionOf(versions) != observed.Version)
                    throw new InvalidOperationException("Review discussion changed while loading its evidence.");
                input = observed with { Body = body.ToString(), AssessmentComplete = true };
                var size = ReviewFindings.ConversationPrompt(input, head, ReviewerLogin).Length + 2;
                if (size > remaining) input = observed with { Body = "", AssessmentComplete = false };
                else remaining -= size;
            }
            result.Add(input);
        }
        return result;
    }

    private async Task<string?> ReadConversationReceiptAsync(ReviewConversation observed, CancellationToken ct)
    {
        if (!string.Equals(observed.LastAuthor, ReviewerLogin, StringComparison.OrdinalIgnoreCase)) return null;
        var response = await ReviewGraphAsync(_client, """
            query($id:ID!){node(id:$id){... on PullRequestReviewThread{
              comments(last:1){nodes{id body author{login __typename}}}}}}
            """, new { id = observed.Id }, ct);
        var latest = response.GetProperty("data").GetProperty("node").GetProperty("comments").GetProperty("nodes")[0];
        return latest.GetProperty("id").GetString() == observed.LastCommentId
            && string.Equals(ReviewActor(latest), ReviewerLogin, StringComparison.OrdinalIgnoreCase)
            ? latest.GetProperty("body").GetString() : null;
    }
}
