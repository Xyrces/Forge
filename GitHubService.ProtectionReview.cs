using System.Text.Json;
using Octokit;

namespace Forge;

public enum ProtectionReviewDecision { NotRequired, Approved, ReviewRequired, ChangesRequested }

public partial class GitHubService
{
    /// <summary>GitHub's effective protected review decision, read with the publisher identity.
    /// This is additional to Forge's own review policy, never a substitute for it.</summary>
    public virtual async Task<ProtectionReviewDecision> GetProtectionReviewDecisionAsync(int number, string head,
        CancellationToken ct = default)
    {
        var response = await _client.Connection.Post<string>(new Uri("graphql", UriKind.Relative),
            new { query = "query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){headRefOid reviewDecision}}}",
                variables = new { owner = _owner, name = _repo, number } },
            "application/json", "application/json", new Dictionary<string, string>(), ct);
        return ParseProtectionReviewDecision(response.HttpResponse.Body as string ?? throw new InvalidOperationException("GitHub protected review response is unavailable."), head);
    }

    internal static ProtectionReviewDecision ParseProtectionReviewDecision(string body, string head)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null
            && (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0))
            throw new InvalidOperationException("GitHub protected review decision could not be read.");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("repository", out var repository) || repository.ValueKind != JsonValueKind.Object
            || !repository.TryGetProperty("pullRequest", out var pr) || pr.ValueKind != JsonValueKind.Object
            || !pr.TryGetProperty("headRefOid", out var observed) || observed.GetString() != head
            || !pr.TryGetProperty("reviewDecision", out var decision))
            throw new InvalidOperationException("GitHub protected review decision is unavailable for the current revision.");
        return decision.ValueKind == JsonValueKind.Null ? ProtectionReviewDecision.NotRequired
            : decision.GetString() switch
            {
                "APPROVED" => ProtectionReviewDecision.Approved,
                "REVIEW_REQUIRED" => ProtectionReviewDecision.ReviewRequired,
                "CHANGES_REQUESTED" => ProtectionReviewDecision.ChangesRequested,
                _ => throw new InvalidOperationException("GitHub returned an unknown protected review decision.")
            };
    }
}
