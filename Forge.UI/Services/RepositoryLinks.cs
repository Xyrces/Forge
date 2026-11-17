using System.Text.Json;

namespace Forge.Dashboard.Services;

public static class RepositoryLinks
{
    public static string? WebUrl(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return null;
        var value = repository.Trim().TrimEnd('/');
        if (value.StartsWith("git@", StringComparison.Ordinal))
        {
            var colon = value.IndexOf(':');
            if (colon < 4) return null;
            value = "https://" + value[4..colon] + "/" + value[(colon + 1)..];
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http" or "ssh")) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        if (path.Length == 0) return null;
        var builder = new UriBuilder(uri) { Scheme = uri.Scheme == "ssh" ? "https" : uri.Scheme,
            UserName = "", Password = "", Query = "", Fragment = "", Path = path };
        if (uri.Scheme == "ssh") builder.Port = -1;
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    public static string? ForProject(JsonElement projects, string? projectId)
    {
        if (string.IsNullOrEmpty(projectId) || projects.ValueKind != JsonValueKind.Array) return null;
        foreach (var project in projects.EnumerateArray())
            if (project.TryGetProperty("id", out var id) && id.GetString() == projectId
                && project.TryGetProperty("repoUrl", out var repo)) return WebUrl(repo.GetString());
        return null;
    }

    public static string? PullRequest(string? repository, string number) =>
        WebUrl(repository) is { } url && int.TryParse(number, out var id) && id > 0 ? $"{url}/pull/{id}" : null;
}
