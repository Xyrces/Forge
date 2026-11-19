namespace Forge.Core;

/// <summary>Resolves one project's vision without treating a shared store as a shared vision.</summary>
public static class VisionMemory
{
    /// <summary>Authoritative operator-edited Markdown, stored only in the owning project's database.</summary>
    public static string DocumentKey(string projectId) => "vision/document/" + projectId;

    public static async Task<MemoryRecord?> ReadAsync(
        MemoryStore memory, string? projectId, string? legacyOwnerProjectId = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var scoped = await memory.GetAsync("vision/" + projectId, ct);
            // An explicitly saved empty vision is meaningful and suppresses the legacy alias.
            if (scoped is not null) return scoped;
            if (!string.Equals(projectId, legacyOwnerProjectId, StringComparison.Ordinal))
                return null;
        }
        return await memory.GetAsync("vision/master", ct);
    }
}
