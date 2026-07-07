namespace Forge.Configuration;

public sealed record ProjectsOptions
{
    public List<ProjectOptions> Projects { get; set; } = new();
}

public sealed record ProjectOptions
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Root { get; set; } = string.Empty;
    public string? SkillPlaybookUrl { get; set; }
    public Dictionary<string, int> Roles { get; set; } = new();
}

public static class DefaultProjectRoles
{
    public static readonly Dictionary<string, int> Default = new(StringComparer.OrdinalIgnoreCase)
    {
        ["coredev"] = 2,
        ["clientdev"] = 2,
        ["reviewer"] = 2,
        ["intake"] = 1,
        ["designer"] = 1,
        ["artist"] = 1,
        ["groomer"] = 1,
        ["orchestrator"] = 1,
    };

    public static int MaxFor(Dictionary<string, int> roles, string role)
    {
        foreach (var kv in roles)
            if (string.Equals(kv.Key, role, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return Default.TryGetValue(role, out var d) ? d : 1;
    }
}
