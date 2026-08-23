using System.Text;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Microsoft.Extensions.Logging;
using Octokit;

namespace Forge.Reviewer;

/// <summary>
/// The watch-lane QA stage (operator rule: PRs must not merge without
/// passing QA that actually plays the product, with screenshots recorded
/// on the PR). Sibling of <see cref="ReviewerDispatcher"/> — runs BEFORE
/// the reviewer on every new PR head for projects with the $qa flag on:
///
///   1. Sync a dedicated QA worktree (agent/qa-&lt;taskId&gt;) to the PR head.
///   2. Run the QA role agent (bash, no plan gate): it exercises the
///      change via the repo's documented QA harness and captures RASTER
///      screenshot evidence into test-results/.
///   3. The dispatcher — never the agent — commits + pushes the evidence
///      to the PR branch, and only when every touched path is an
///      evidence path (test-results/). A pass without evidence files is
///      not QA; evidence mixed with source edits is refused.
///   4. The verdict lands in task metadata: qaForSha (code head
///      verified), qaSha (head the verdict applies to, post evidence
///      push), qaVerdict (pass|fail), qaNotes, qaRound. The reviewer
///      self-skips until QA is current (ReviewerDispatcher qaEnabled
///      guard); PRWatcher's merge gate requires pass at the head and a
///      fail requeues a rework round with the QA notes as context.
///
/// Dedupe mirrors the reviewer: per-head (qaSha), Error outcomes never
/// dedupe, and the watcher's circuit breaker bounds retries. QA-infra
/// failures (no harness, hung run, non-fast-forward push) park the task
/// Blocked with blockedKind=qa-unavailable after MaxQaAttempts at a head
/// — an operator decision, never a silent skip.
/// </summary>
public sealed class QaDispatcher
{
    public const string VerdictPass = "pass";
    public const string VerdictFail = "fail";
    public const string VerdictError = "error";

    /// <summary>Blocked marker when QA cannot run at all (no harness,
    /// repeated runner errors) — operator-decision block, NOT the
    /// reviewer-unavailable auto-resume path.</summary>
    public const string BlockedKindQaUnavailable = "qa-unavailable";

    /// <summary>QA rounds per head before the task parks as
    /// qa-unavailable.</summary>
    public const int MaxQaAttempts = 2;

    /// <summary>QA playthroughs build + run the product — far longer
    /// than a review. Bounded so a hung QA run can't freeze the watch.</summary>
    private static readonly TimeSpan QaRunTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Only these path prefixes may appear in a QA evidence
    /// commit. The dispatcher refuses to push anything else.</summary>
    private static readonly string[] EvidencePathPrefixes = { "test-results/" };

    private readonly IIssueStore _issues;
    private readonly GitHubService _gitHub;
    private readonly GitWorktreeService _worktrees;
    private readonly IAgentRunner _agentRunner;
    private readonly string? _projectId;
    private readonly IDashboardEventBus? _events;
    private readonly ILogger<QaDispatcher> _logger;

    public QaDispatcher(
        IIssueStore issues,
        GitHubService gitHub,
        GitWorktreeService worktrees,
        IAgentRunner agentRunner,
        ILogger<QaDispatcher> logger,
        string? projectId = null,
        IDashboardEventBus? events = null)
    {
        _issues = issues;
        _gitHub = gitHub;
        _worktrees = worktrees;
        _agentRunner = agentRunner;
        _logger = logger;
        _projectId = projectId;
        _events = events;
    }

    public sealed record QaOutcome(string Verdict, string Notes, string HeadSha, string? Error = null);

    /// <summary>Run QA against the watched task's PR head once. Returns
    /// null when QA is already current at the head (dedupe) or a fail
    /// verdict already stands at the head (the watcher turns that into
    /// a rework round). Never throws for LLM/git failures — those come
    /// back as Error outcomes counted by the attempt budget.</summary>
    public async Task<QaOutcome?> VerifyOnceAsync(IssueRecord task, CancellationToken cancellationToken = default,
        Func<PullRequest, (string Sha, string Ref)>? headOverride = null)
    {
        var prText = task.GetMetadata("prNumber");
        if (!int.TryParse(prText, out var prNumber))
        {
            _logger.LogError("QA: watched task {Id} missing prNumber", task.Id);
            return new QaOutcome(VerdictError, "", "", "missing prNumber");
        }

        PullRequest pr;
        try
        {
            pr = await _gitHub.GetPullRequestAsync(prNumber, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QA: PR #{Pr} could not be fetched", prNumber);
            return new QaOutcome(VerdictError, "", "", $"GetPullRequest: {ex.Message}");
        }
        // Test seam: Octokit's PR Head is init-only (same constraint as
        // the reviewer's headShaOverride).
        var (headSha, branch) = headOverride is not null
            ? headOverride(pr)
            : (pr.Head.Sha, pr.Head.Ref);

        // Per-head dedupe: QA already ran at this head. A standing fail
        // verdict is the watcher's rework trigger — QA re-runs only on
        // the NEXT head (the rework push).
        var qaSha = task.GetMetadata("qaSha");
        var qaVerdict = task.GetMetadata("qaVerdict");
        if (string.Equals(qaSha, headSha, StringComparison.Ordinal) && !string.IsNullOrEmpty(qaVerdict))
        {
            return null;
        }

        // Attempt budget per head: QA infra failures park the task for
        // the operator instead of burning LLM runs forever.
        var attempts = string.Equals(task.GetMetadata("qaAttemptSha"), headSha, StringComparison.Ordinal)
            ? int.TryParse(task.GetMetadata("qaAttempts"), out var a) ? a : 0
            : 0;
        if (attempts >= MaxQaAttempts)
        {
            await _issues.TransitionAsync(task.Id, IssueStatus.Blocked,
                $"QA stage unavailable after {attempts} attempts at head {headSha[..Math.Min(7, headSha.Length)]} — operator review required",
                new Dictionary<string, object> { ["blockedKind"] = BlockedKindQaUnavailable }, cancellationToken);
            _events?.Publish(new DashboardEvent(
                DateTime.UtcNow, DashboardEventKind.TaskTransition,
                task.Id, $"QA unavailable ({attempts} attempts) — parked for the operator"));
            return new QaOutcome(VerdictError, "", headSha, "qa attempt budget exhausted — parked");
        }

        var round = (int.TryParse(task.GetMetadata("qaRound"), out var r) ? r : 0) + 1;
        await _issues.TransitionAsync(task.Id, task.Status, $"QA round {round} started",
            new Dictionary<string, object>
            {
                ["qaStartedAt"] = DateTime.UtcNow.ToString("O"),
                ["qaAttemptSha"] = headSha,
                ["qaAttempts"] = (attempts + 1).ToString(),
            }, cancellationToken);

        try
        {
            return await RunQaAsync(task, prNumber, branch, headSha, round, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "QA: run for task {Id} (PR #{Pr}) faulted", task.Id, prNumber);
            return new QaOutcome(VerdictError, "", headSha, ex.Message);
        }
    }

    private async Task<QaOutcome> RunQaAsync(
        IssueRecord task, int prNumber, string branch, string headSha, int round, CancellationToken ct)
    {
        // Dedicated QA worktree on its own agent-namespace branch, synced
        // to the PR head. The dev worktree is untouched — a rework round
        // may be running in it.
        var qaTaskId = "qa-" + task.Id;
        var worktreePath = await _worktrees.CreateAsync(
            qaTaskId, _worktrees.DefaultBranch, ct, branchOverride: $"agent/qa-{task.Id}");
        await _worktrees.SyncWorktreeToRefAsync(worktreePath, qaTaskId, $"origin/{branch}", ct);
        var syncedSha = await _worktrees.GetHeadShaAsync(worktreePath, ct);
        if (!string.Equals(syncedSha, headSha, StringComparison.Ordinal))
        {
            return new QaOutcome(VerdictError, "", headSha,
                $"worktree synced to {syncedSha[..Math.Min(7, syncedSha.Length)]}, expected {headSha[..Math.Min(7, headSha.Length)]}");
        }

        var context = new Dictionary<string, object>
        {
            ["issueId"] = task.Id,
            ["worktreePath"] = worktreePath,
        };
        if (!string.IsNullOrWhiteSpace(_projectId)) context["projectId"] = _projectId;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(QaRunTimeout);
        AgentRunResult result;
        try
        {
            result = await _agentRunner.RunAsync(
                AgentType.QA, BuildPrompt(task, prNumber, branch, headSha),
                sessionId: null, context: context, ct: timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new QaOutcome(VerdictError, "", headSha, $"QA run timed out after {QaRunTimeout.TotalMinutes:F0}m");
        }

        var (verdict, notes) = ParseQaOutput(result.Text);
        if (verdict is null)
        {
            return new QaOutcome(VerdictError, "", headSha,
                "no QA_VERDICT marker in the run's final message");
        }

        // Evidence enforcement: the dispatcher ships the evidence, never
        // the agent. A pass without raster evidence files is not QA
        // (operator correction 2026-08-24); anything outside the
        // evidence paths refuses the push.
        var dirty = await _worktrees.ListDirtyFilesAsync(worktreePath, ct);
        var nonEvidence = dirty.Where(f =>
            !EvidencePathPrefixes.Any(p => f.StartsWith(p, StringComparison.Ordinal))).ToList();
        if (nonEvidence.Count > 0)
        {
            return new QaOutcome(VerdictError, "", headSha,
                $"QA run touched non-evidence paths (refused to ship): {string.Join(", ", nonEvidence.Take(5))}");
        }
        var rasterEvidence = dirty.Where(f =>
            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)).ToList();
        if (verdict == VerdictPass && rasterEvidence.Count == 0)
        {
            return new QaOutcome(VerdictError, "", headSha,
                "pass verdict without raster screenshot evidence (png/jpg under test-results/) — not QA");
        }

        var qaHead = headSha;
        if (dirty.Count > 0)
        {
            await _worktrees.CommitAllAsync(worktreePath,
                $"QA({task.Id}): playthrough evidence for {headSha[..Math.Min(7, headSha.Length)]}", ct);
            await _worktrees.PushHeadToRefAsync(worktreePath, branch, ct);
            qaHead = await _worktrees.GetHeadShaAsync(worktreePath, ct);
        }

        await _issues.TransitionAsync(task.Id, task.Status, $"QA {verdict} (round {round})",
            new Dictionary<string, object>
            {
                ["qaSha"] = qaHead,
                ["qaForSha"] = headSha,
                ["qaVerdict"] = verdict,
                ["qaNotes"] = notes.Length > 1000 ? notes[..1000] : notes,
                ["qaRound"] = round.ToString(),
                ["qaAttempts"] = null!,
                ["qaAttemptSha"] = null!,
                ["qaStartedAt"] = null!,
            }, ct);
        _events?.Publish(new DashboardEvent(
            DateTime.UtcNow, DashboardEventKind.TaskTransition,
            task.Id, $"QA round {round}: {verdict} at {qaHead[..Math.Min(7, qaHead.Length)]}"));
        _logger.LogInformation("QA (task {Id}, PR #{Pr}): {Verdict} at {Sha} (round {Round})",
            task.Id, prNumber, verdict, qaSha7(qaHead), round);
        return new QaOutcome(verdict, notes, qaHead);

        static string qaSha7(string sha) => sha[..Math.Min(7, sha.Length)];
    }

    /// <summary>The QA completion contract: the final message leads with
    /// a QA_VERDICT marker line; everything after is the notes.</summary>
    internal static (string? Verdict, string Notes) ParseQaOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, "");
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("QA_VERDICT:", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line["QA_VERDICT:".Length..].Trim().ToLowerInvariant();
            var verdict = value.StartsWith(VerdictPass, StringComparison.Ordinal) ? VerdictPass
                : value.StartsWith(VerdictFail, StringComparison.Ordinal) ? VerdictFail
                : null;
            if (verdict is null) return (null, "");
            var markerAt = text.IndexOf(line, StringComparison.Ordinal);
            var notes = text[(markerAt + line.Length)..].Trim();
            return (verdict, notes);
        }
        return (null, "");
    }

    private static string BuildPrompt(IssueRecord task, int prNumber, string branch, string headSha)
    {
        var sb = new StringBuilder();
        sb.Append("QA-verify PR #").Append(prNumber).Append(" (branch ").Append(branch)
            .Append(", head ").Append(headSha[..Math.Min(7, headSha.Length)]).AppendLine(").");
        sb.Append("Your worktree IS the PR branch checkout at that head.\n\n");
        sb.Append("Task: ").Append(task.Id).Append(" — ").AppendLine(task.Title);
        if (!string.IsNullOrWhiteSpace(task.Description))
            sb.Append("\nTask description (acceptance criteria live here — verify EACH):\n```\n")
                .AppendLine(task.Description).AppendLine("```");
        sb.AppendLine("""


            Rules:
            - Find the repo's QA/playtest documentation first (docs/, scripts/, README) and run the documented harness. For game projects: actually PLAY the build via its automation interface (e.g. an MCP server) — API-level state reads alone are not playing.
            - Capture RASTER screenshot evidence (PNG/JPG) of the running product at the moments that prove each acceptance criterion, into test-results/qa/<this task id>/ (create it). JSON/SVG/ASCII state dumps are never screenshots.
            - Capture facilities, in preference order: (1) an in-engine/in-app capture hook if the branch ships one (use it — even when the hook IS the change under review; a working hook is the proof), (2) the repo's documented screenshot tooling, (3) host window-capture of the running product window (grim/scrot/xwd/portal) when a display is available. Build the product first if the runtime needs its assemblies (e.g. dotnet build for a Godot C# client).
            - You may ONLY create files under test-results/. Never edit source, tests, project files, or docs. Do NOT git commit or push — the orchestrator ships your evidence (and refuses anything outside test-results/).
            - If the harness can't run (missing binary, missing docs, broken build), do not fake a result — end with QA_VERDICT: fail and name exactly what's missing.

            When done, your final message MUST lead with exactly one verdict line:
            QA_VERDICT: pass
            or
            QA_VERDICT: fail
            followed by: what you ran, the evidence files you captured (paths), what you observed, and per-criterion pass/fail.
            """);
        return sb.ToString();
    }
}
