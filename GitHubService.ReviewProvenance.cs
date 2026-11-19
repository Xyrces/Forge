using System.Globalization;
using System.Text.Json;

namespace Forge;

public sealed record ReviewObservationBoundary(DateTimeOffset ServerTime, long RepositoryId, long PullRequestId, string? ResetMarker = "none");

public partial class GitHubService
{
    /// <summary>Recheck the head and obtain GitHub's clock for a durable review-observation boundary.</summary>
    public virtual async Task<ReviewObservationBoundary> GetReviewObservationTimeAsync(int number, string head, CancellationToken ct = default)
    {
        const string query = """
            query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){databaseId
              pullRequest(number:$number){databaseId headRefOid
                timelineItems(last:1,itemTypes:[HEAD_REF_FORCE_PUSHED_EVENT,HEAD_REF_DELETED_EVENT,HEAD_REF_RESTORED_EVENT,BASE_REF_CHANGED_EVENT,REOPENED_EVENT]){
                  nodes{... on Node{id}}}}}}
            """;
        var response = await _client.Connection.Post<string>(new Uri("graphql", UriKind.Relative),
            new { query, variables = new { owner = _owner, name = _repo, number } },
            "application/json", "application/json", new Dictionary<string, string>(), ct);
        using var json = JsonDocument.Parse(response.Body);
        var hasErrors = json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind != JsonValueKind.Null
            && (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0);
        var repository = json.RootElement.GetProperty("data").GetProperty("repository");
        var pr = repository.GetProperty("pullRequest");
        if (pr.GetProperty("headRefOid").GetString() != head)
            throw new InvalidOperationException("PR head changed while establishing review provenance.");
        var date = response.HttpResponse.Headers.FirstOrDefault(p => string.Equals(p.Key, "Date", StringComparison.OrdinalIgnoreCase)).Value;
        if (!DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var observed))
            throw new InvalidOperationException("GitHub server time is unavailable; review origin cannot be verified.");
        var repositoryId = repository.GetProperty("databaseId").GetInt64();
        var pullRequestId = pr.GetProperty("databaseId").GetInt64();
        if (repositoryId <= 0 || pullRequestId <= 0) throw new InvalidOperationException("GitHub repository identity is unavailable.");
        string? marker = null;
        if (!hasErrors && pr.TryGetProperty("timelineItems", out var timeline) && timeline.ValueKind == JsonValueKind.Object
            && timeline.TryGetProperty("nodes", out var events) && events.ValueKind == JsonValueKind.Array)
            marker = events.GetArrayLength() == 0 ? "none" : events[0].GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(marker)) marker = null; // Local publication proof remains usable; polling cannot establish new origins.
        return new(observed, repositoryId, pullRequestId, marker);
    }
}
