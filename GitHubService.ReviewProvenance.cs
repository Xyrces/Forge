using System.Globalization;
using System.Text.Json;

namespace Forge;

public sealed record ReviewObservationBoundary(DateTimeOffset ServerTime, long RepositoryId, long PullRequestId);

public partial class GitHubService
{
    /// <summary>Recheck the head and obtain GitHub's clock for a durable review-observation boundary.</summary>
    public virtual async Task<ReviewObservationBoundary> GetReviewObservationTimeAsync(int number, string head, CancellationToken ct = default)
    {
        var response = await _client.Connection.Get<string>(
            new Uri($"repos/{_owner}/{_repo}/pulls/{number}", UriKind.Relative), null, "application/vnd.github+json", ct);
        using var json = JsonDocument.Parse(response.Body);
        if (json.RootElement.GetProperty("head").GetProperty("sha").GetString() != head)
            throw new InvalidOperationException("PR head changed while establishing review provenance.");
        var date = response.HttpResponse.Headers.FirstOrDefault(p => string.Equals(p.Key, "Date", StringComparison.OrdinalIgnoreCase)).Value;
        if (!DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var observed))
            throw new InvalidOperationException("GitHub server time is unavailable; review origin cannot be verified.");
        var repositoryId = json.RootElement.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64();
        var pullRequestId = json.RootElement.GetProperty("id").GetInt64();
        if (repositoryId <= 0 || pullRequestId <= 0) throw new InvalidOperationException("GitHub repository identity is unavailable.");
        return new(observed, repositoryId, pullRequestId);
    }
}
