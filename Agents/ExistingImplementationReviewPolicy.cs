namespace Forge.Agents;

internal static class ExistingImplementationReviewPolicy
{
    public const string Instructions = "This invocation verifies an existing implementation against exact approved engineering criteria, not a PR. " +
        "All reviewer read-only and architecture rules still apply. Re-read the specified source; prior sessions or an engineer's no-op assertion are not approval. " +
        "Absence of architectural violations alone does not establish acceptance: every listed criterion needs concrete evidence. " +
        "Use the strict JSON response contract in the request instead of the ordinary PR comment format. Never claim later deployment or operator observations.";
}
