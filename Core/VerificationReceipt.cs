namespace Forge.Core;

/// <summary>Trusted worker verification evidence bound to content and exact configured commands.</summary>
public sealed record VerificationReceipt(string AttemptId, string TreeSha, string CommandsSha, DateTimeOffset CompletedAt);
