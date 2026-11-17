namespace Forge.Core;

/// <summary>Retired PR checkouts remain immutable artifacts; a new verification uses a distinct directory.</summary>
public static class IssueWorktreeIdentity
{
    public static string For(IssueRecord task) => task.GetMetadata("verificationCheckoutKey") ?? task.Id;
}
