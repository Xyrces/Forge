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
    public static bool IsMutating(string command) => IsMutating(command, out _);

    public static bool IsMutating(string command, out string? reason)
    {
        reason = null;
        // Silencer idioms are NOT writes: `2>/dev/null`, `>/dev/null`,
        // `2>&1`, `&>/dev/null` discard or merge streams — they never
        // create files. Scrub them BEFORE the redirect test below,
        // whose `[^>|]>` otherwise matches the `2>` fd prefix and
        // refuses the most common read-only exploration idiom in
        // existence (observed live 2026-08-06: task-382's run got
        // `ls -la .forge/ 2>/dev/null` refused, concluded "the system
        // is blocking even read-only commands", and burned both plan
        // revisions exploring blind).
        var scrubbed = SilencerRegex().Replace(command, " ");
        // Redirection into a file (>, >>, tee) — heredoc writes and
        // output overwrites are the agent's edit mechanism.
        if (RedirectionRegex().IsMatch(scrubbed)) { reason = "writes a file (>, >>, or tee)"; return true; }
        // Git mutations (state-changing subcommands only — fetch,
        // status, log, diff, show, branch listing stay open).
        if (GitMutationRegex().IsMatch(command)) { reason = "state-changing git subcommand"; return true; }
        // Classic filesystem mutators.
        if (FsMutationRegex().IsMatch(command)) { reason = "filesystem mutator (rm/mv/cp/mkdir/...)"; return true; }
        // In-place editors and interpreters writing files.
        if (InPlaceEditRegex().IsMatch(command)) { reason = "in-place edit (sed -i / perl -i / script write)"; return true; }
        return false;
    }

    /// <summary>fd-dup and /dev/null discards: `2>/dev/null`,
    /// `2>&1`, `&>/dev/null`, `>/dev/null 2>&1`, etc.</summary>
    [GeneratedRegex(@"\d*\s*&?>{1,2}\s*(/dev/null|&\d)", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex SilencerRegex();

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
