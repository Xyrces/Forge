namespace PortHorizon.Agents.Core;

/// <summary>
/// The 6 checkpoints the engineering dispatch workflow can be
/// in. The recoverer (P4 Stage A, see <c>docs/p4-restart-safety.md</c>)
/// reads the issue's <c>dispatch_checkpoint</c> column and decides
/// which side-effects still need to be replayed.
///
/// <para>
/// Stored as TEXT in SQLite. Values are lowercased to match the
/// existing status column convention.
/// </para>
/// </summary>
public enum DispatchCheckpoint
{
    /// <summary>
    /// ClaimExecutor accepted the issue but no worktree yet.
    /// Side-effects: status=InProgress, assignee=kilo. Replay
    /// from worktree acquisition.
    /// </summary>
    Claimed,

    /// <summary>
    /// Worktree directory + branch created. Side-effects:
    /// <c>worktreePath</c> + <c>branch</c> set in metadata.
    /// Replay from agent run.
    /// </summary>
    WorktreeAcquired,

    /// <summary>
    /// LLM finished; <c>result.Text</c> captured. Side-effects:
    /// <c>modelResponse</c> set in metadata. Replay from commit.
    /// </summary>
    AgentCompleted,

    /// <summary>
    /// Local commit on the branch. Side-effects: <c>branchSha</c>
    /// updated. Replay from push.
    /// </summary>
    CommitDone,

    /// <summary>
    /// Remote branch pushed. No extra metadata. Replay from PR
    /// open.
    /// </summary>
    PushDone,

    /// <summary>
    /// PR exists on GitHub. Side-effects: <c>prNumber</c> set.
    /// Replay from enqueue watch (the existing PRWatcher path).
    /// </summary>
    PrOpened,
}

public static class DispatchCheckpointExtensions
{
    public static string ToDbValue(this DispatchCheckpoint c) => c switch
    {
        DispatchCheckpoint.Claimed => "claimed",
        DispatchCheckpoint.WorktreeAcquired => "worktree_acquired",
        DispatchCheckpoint.AgentCompleted => "agent_completed",
        DispatchCheckpoint.CommitDone => "commit_done",
        DispatchCheckpoint.PushDone => "push_done",
        DispatchCheckpoint.PrOpened => "pr_opened",
        _ => "claimed",
    };

    public static bool TryParseDb(string? s, out DispatchCheckpoint c)
    {
        switch (s)
        {
            case "claimed": c = DispatchCheckpoint.Claimed; return true;
            case "worktree_acquired": c = DispatchCheckpoint.WorktreeAcquired; return true;
            case "agent_completed": c = DispatchCheckpoint.AgentCompleted; return true;
            case "commit_done": c = DispatchCheckpoint.CommitDone; return true;
            case "push_done": c = DispatchCheckpoint.PushDone; return true;
            case "pr_opened": c = DispatchCheckpoint.PrOpened; return true;
            default: c = DispatchCheckpoint.Claimed; return false;
        }
    }
}

/// <summary>
/// The action the recoverer took for one issue, written into
/// the <c>recovery_report.actions_json</c> array.
/// </summary>
/// <param name="IssueId">The issue that was inspected.</param>
/// <param name="BeforeCheckpoint">The checkpoint recorded on the
/// issue when recovery started (may be <c>null</c> for issues
/// predating v11).</param>
/// <param name="AfterCheckpoint">The checkpoint the recoverer
/// set after replaying (may be <c>null</c> if the issue was
/// left alone or transitioned to Failed).</param>
/// <param name="Action">One of: <c>replay</c>, <c>failed</c>,
/// <c>left_alone</c>, <c>already_recovered</c>.</param>
/// <param name="Error">Free-form error message when
/// <paramref name="Action"/> is <c>failed</c>.</param>
public sealed record RecoveryActionRecord(
    string IssueId,
    string? BeforeCheckpoint,
    string? AfterCheckpoint,
    string Action,
    string? Error);

/// <summary>
/// One row per StartupRecovery pass. The recoverer writes one
/// row per pass at the end of the run, regardless of how many
/// issues were touched. <c>ActionsJson</c> is a JSON array of
/// <see cref="RecoveryActionRecord"/>.
/// </summary>
public sealed record RecoveryReportRecord(
    long Id,
    DateTime Ts,
    string? SpecId,
    int IssuesScanned,
    int IssuesReplayed,
    int IssuesFailed,
    string ActionsJson,
    long DurationMs);