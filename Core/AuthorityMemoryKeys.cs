namespace Forge.Core;

/// <summary>Operator controls share the legacy memory layout for rollback compatibility,
/// but are not general-purpose agent or dashboard memory.</summary>
public static class AuthorityMemoryKeys
{
    public static bool IsProtected(string key) => MemoryStore.IsReservedReviewOriginKey(key)
        || key.StartsWith("review/", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("gate/", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("workflow/", StringComparison.OrdinalIgnoreCase)
        || key.Equals("deployment/merge-fence", StringComparison.OrdinalIgnoreCase);
}
