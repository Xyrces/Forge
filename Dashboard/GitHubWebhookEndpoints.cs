using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Core;
using Forge.Core.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Forge.Dashboard;

/// <summary>Signature-only ingress. It accepts wake hints, never task state or review decisions.</summary>
public static class GitHubWebhookEndpoints
{
    public const string Route = "/hooks/github";
    internal const int MaxBodyBytes = 1024 * 1024;

    public static void MapGitHubWebhookEndpoints(WebApplication app, string secret,
        IProjectStore projects, IEventPublisher publisher)
    {
        if (Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("GitHub webhook secret must contain at least 32 bytes.");
        var key = Encoding.UTF8.GetBytes(secret);
        var deliveries = new ConcurrentDictionary<Guid, DateTimeOffset>();
        app.MapPost(Route, async (HttpContext context, CancellationToken ct) =>
        {
            var request = context.Request;
            if (request.ContentLength > MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            if (!Guid.TryParse(request.Headers["X-GitHub-Delivery"], out var delivery)) return Results.BadRequest();
            // Bound streamed/chunked requests too, before parsing or consulting any store.
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + read > MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                body.Write(buffer, 0, read);
            }
            var bytes = body.ToArray();
            if (!ValidSignature(bytes, request.Headers["X-Hub-Signature-256"].ToString(), key))
                return Results.Unauthorized();
            var kind = request.Headers["X-GitHub-Event"].ToString();
            if (kind == "ping") return Results.Ok(new { received = true });
            WebhookRoute? route;
            try { route = Parse(kind, bytes); }
            catch (JsonException) { return Results.BadRequest(); }
            if (route is null) return Results.NoContent();
            var owners = (await projects.ListAsync(ct)).Where(p =>
                string.Equals(RepositoryName(p.RepoUrl), route.Repository, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (owners.Length == 0) return Results.NoContent();
            // Bounded process-local duplicate suppression is an optimization.
            // Replays after restart are safe because consumers apply no payload state.
            if (!deliveries.TryAdd(delivery, DateTimeOffset.UtcNow)) return Results.Accepted();
            if (deliveries.Count > 2048)
                foreach (var old in deliveries.OrderBy(x => x.Value).Take(1024)) deliveries.TryRemove(old.Key, out _);
            foreach (var project in owners)
                for (var i = 0; i < route.Hints.Count; i++)
                {
                    var hint = route.Hints[i];
                    await publisher.PublishAsync(new PrWatchRequested
                    {
                        MessageId = $"github:{project.Id}:{delivery:N}:{i}", ProjectId = project.Id,
                        PrNumber = hint.PrNumber, HeadSha = hint.HeadSha, Branch = hint.Branch, Reason = "github:" + kind,
                    }, ct);
                }
            return Results.Accepted();
        });
    }

    internal static bool ValidSignature(byte[] body, string signature, byte[] key)
    {
        if (signature.Length != 71 || !signature.StartsWith("sha256=", StringComparison.Ordinal)) return false;
        try { return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, body), Convert.FromHexString(signature[7..])); }
        catch (FormatException) { return false; }
    }

    internal static string? RepositoryName(string url)
    {
        // Restrict this ingress to GitHub.com repository registrations.
        if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) url = "https://github.com/" + url[15..];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "github.com"
            || uri.Scheme is not ("https" or "ssh") || !string.IsNullOrEmpty(uri.Query)) return null;
        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        return path.Split('/').Length == 2 ? path : null;
    }

    internal sealed record WatchHint(int? PrNumber = null, string? HeadSha = null, string? Branch = null);
    internal sealed record WebhookRoute(string Repository, IReadOnlyList<WatchHint> Hints);

    internal static WebhookRoute? Parse(string kind, byte[] bytes)
    {
        if (kind is not ("pull_request" or "pull_request_review" or "pull_request_review_comment"
            or "check_run" or "check_suite" or "status" or "push")) return null;
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        var repo = Text(Child(root, "repository"), "full_name");
        if (repo is null || repo.Length > 256 || repo.Split('/').Length != 2) return null;
        var hints = new List<WatchHint>();
        if (kind is "pull_request" or "pull_request_review" or "pull_request_review_comment")
        {
            var pr = Child(root, "pull_request");
            var number = Number(pr, "number") ?? Number(root, "number");
            if (number is > 0) hints.Add(new(number));
        }
        else if (kind == "push")
        {
            var branch = Text(root, "ref");
            if (branch?.StartsWith("refs/heads/", StringComparison.Ordinal) == true)
                hints.Add(new(HeadSha: Text(root, "after"), Branch: branch[11..]));
        }
        else if (kind == "status")
        {
            var sha = Text(root, "sha");
            hints.Add(new(HeadSha: sha));
            var branches = Child(root, "branches");
            if (branches.ValueKind == JsonValueKind.Array)
                foreach (var branch in branches.EnumerateArray().Take(100))
                    if (Text(branch, "name") is { } name) hints.Add(new(HeadSha: sha, Branch: name));
        }
        else
        {
            var check = Child(root, kind);
            var sha = Text(check, "head_sha");
            var suite = kind == "check_suite" ? check : Child(check, "check_suite");
            hints.Add(new(HeadSha: sha, Branch: Text(suite, "head_branch")));
            var prs = Child(check, "pull_requests");
            if (prs.ValueKind == JsonValueKind.Array)
                foreach (var pr in prs.EnumerateArray().Take(100))
                    if (Number(pr, "number") is > 0 and var number) hints.Add(new(number, sha));
        }
        return hints.Count == 0 ? null : new(repo, hints.Distinct().ToArray());
    }

    private static JsonElement Child(JsonElement node, string key)
        => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var child) ? child : default;
    private static string? Text(JsonElement node, string key)
    {
        var value = Child(node, key);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static int? Number(JsonElement node, string key)
    {
        var value = Child(node, key);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }
}
