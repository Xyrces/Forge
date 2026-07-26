using System.Text.RegularExpressions;

namespace Forge.AgentTools;

/// <summary>
/// Deterministic shell-mutation classifier for the plan gate's hard
/// enforcement. Before the plan is approved, commands classified as
/// mutating are refused; read/explore/build/test commands pass.
///
/// This is a structural signal, not a security boundary: the agent
/// is not adversarial, and false positives (a read that looks
/// mutating) merely make it rephrase. False negatives are bounded
/// by the role prompt instructing the plan-first protocol.
/// </summary>
public static partial class ShellMutationClassifier
{
    public static bool IsMutating(string command)
    {
        // Redirection into a file (>, >>, tee) — heredoc writes and
        // output overwrites are the agent's edit mechanism.
        if (RedirectionRegex().IsMatch(command)) return true;
        // Git mutations (state-changing subcommands only — fetch,
        // status, log, diff, show, branch listing stay open).
        if (GitMutationRegex().IsMatch(command)) return true;
        // Classic filesystem mutators.
        if (FsMutationRegex().IsMatch(command)) return true;
        // In-place editors and interpreters writing files.
        if (InPlaceEditRegex().IsMatch(command)) return true;
        return false;
    }

    /// <summary>`>` / `>>` outside quotes (approximate), or tee.</summary>
    [GeneratedRegex(@"(>>|[^>|]>(?![>=])|\btee\b)", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex RedirectionRegex();

    [GeneratedRegex(@"\bgit\s+(commit|push|merge|rebase|reset|checkout|switch|restore|apply|am\b|cherry-pick|revert|tag\s+-|branch\s+-[dDmM]|add\s|mv\s|rm\s|stash\s+pop|stash\s+drop|clean\s)", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex GitMutationRegex();

    [GeneratedRegex(@"(^|[;&|]\s*|\b)(rm|mv|cp|mkdir|rmdir|touch|chmod|chown|chgrp|ln|install|dd|truncate|rsync|patch|shred)\s", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex FsMutationRegex();

    [GeneratedRegex(@"(\bsed\s+(-\w*\s)*-i|\bperl\s+(-\w*\s)*-i|\bawk\s+-i\b|\bpython3?\b[^|]*\bwrite\b|\bnode\s+[^|]*\bwriteFile)", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex InPlaceEditRegex();
}
