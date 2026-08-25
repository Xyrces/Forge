using System.Diagnostics;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Reviewer;
using Microsoft.Extensions.Logging.Abstractions;
using Octokit;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// The watch-lane QA stage: worktree sync at the PR head, verdict
/// parsing, dispatcher-owned evidence shipping (evidence-paths-only
/// enforcement, never the agent pushing), metadata recording, per-head
/// dedupe, and the attempt budget that parks QA-unavailable tasks.
/// </summary>
public sealed class QaDispatcherTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _bareDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly GitWorktreeService _worktrees;

    public QaDispatcherTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("qa-dispatch");
        _bareDir = _workDir + "-bare.git";
        Directory.CreateDirectory(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _issues = new IssueStore(_dbPath);
        _worktrees = new GitWorktreeService(
            new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance);
        InitRepo();
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { Directory.Delete(_workDir, recursive: true); } catch { }
        try { Directory.Delete(_bareDir, recursive: true); } catch { }
    }

    private static int RunGit(string dir, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static string GitOut(string dir, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return stdout;
    }

    private void InitRepo()
    {
        RunGit(_workDir, "init -q -b main");
        RunGit(_workDir, "config user.email test@example.com");
        RunGit(_workDir, "config user.name Test");
        File.WriteAllText(Path.Combine(_workDir, "README.md"), "# init");
        RunGit(_workDir, "add -A");
        RunGit(_workDir, "commit -q -m initial");
        RunGit(_workDir, $"clone --bare {_workDir} {_bareDir}");
        RunGit(_workDir, $"remote add origin {_bareDir}");
        RunGit(_workDir, "fetch origin");

        // The PR branch: agent/task-1 with one code commit + one VISUAL
        // commit over main (Client/ is the visual prefix the dispatcher
        // tests configure — mixed diff, highest tier wins ⇒ visual).
        RunGit(_workDir, "checkout -q -b agent/task-1");
        File.WriteAllText(Path.Combine(_workDir, "feature.txt"), "the change");
        Directory.CreateDirectory(Path.Combine(_workDir, "Client"));
        File.WriteAllText(Path.Combine(_workDir, "Client", "scene.txt"), "the scene");
        RunGit(_workDir, "add -A");
        RunGit(_workDir, "commit -q -m feature");
        RunGit(_workDir, "push -q -u origin agent/task-1");
        RunGit(_workDir, "checkout -q main");

        // The docs-only branch: agent/task-docs — every path is in the
        // tier-3 set (docs/, **.md, .gitignore, test-results/).
        RunGit(_workDir, "checkout -q -b agent/task-docs");
        Directory.CreateDirectory(Path.Combine(_workDir, "docs", "QA"));
        File.WriteAllText(Path.Combine(_workDir, "docs", "QA", "policy.md"), "# QA policy");
        File.WriteAllText(Path.Combine(_workDir, ".gitignore"), "test-results/");
        RunGit(_workDir, "add -A");
        RunGit(_workDir, "commit -q -m docs");
        RunGit(_workDir, "push -q -u origin agent/task-docs");
        RunGit(_workDir, "checkout -q main");
    }

    private static string PrHeadSha(string dir) => GitOut(dir, "rev-parse origin/agent/task-1");
    private static string DocsHeadSha(string dir) => GitOut(dir, "rev-parse origin/agent/task-docs");

    private sealed class FakeGitHub : GitHubService
    {
        public FakeGitHub() : base("o", "r", null) { }
        public override Task<PullRequest> GetPullRequestAsync(int number, CancellationToken cancellationToken = default)
            => Task.FromResult(new PullRequest(number));
    }

    /// <summary>Scripted agent: optionally drops files into the QA
    /// worktree (from the run context), then returns the verdict text.</summary>
    private sealed class FakeRunner : IAgentRunner
    {
        public List<(string Path, string Content)> Drops { get; } = new();
        public string Reply { get; set; } = "QA_VERDICT: pass\nplayed the build; evidence captured";
        public int Calls;

        public Task<AgentRunResult> RunAsync(
            AgentType role, string prompt, string? sessionId,
            IReadOnlyDictionary<string, object>? context, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(AgentType.QA, role);
            var worktree = (string)context!["worktreePath"];
            foreach (var (path, content) in Drops)
            {
                var full = Path.Combine(worktree, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }
            return Task.FromResult(new AgentRunResult(Reply, null, 0, 0, TimeSpan.FromSeconds(1)));
        }
    }

    private async Task<IssueRecord> SeedTask()
    {
        var task = await _issues.CreateAsync(new Forge.Core.NewIssue("task", "add the feature",
            Metadata: new Dictionary<string, object> { ["prNumber"] = "42" }));
        await _issues.TransitionAsync(task.Id, IssueStatus.InProgress, null,
            new Dictionary<string, object> { ["branch"] = "agent/task-1" });
        return (await _issues.GetAsync(task.Id))!;
    }

    private QaDispatcher NewDispatcher(FakeRunner runner, IReadOnlyList<string>? visualPaths = null) => new(
        _issues, new FakeGitHub(), _worktrees, runner,
        NullLogger<QaDispatcher>.Instance, projectId: "proj",
        visualPaths: visualPaths ?? new[] { "Client/" });

    [Fact]
    public void ParseQaOutput_PassAndFail()
    {
        var (v1, n1) = QaDispatcher.ParseQaOutput("QA_VERDICT: pass\nlooks good");
        Assert.Equal("pass", v1);
        Assert.Equal("looks good", n1);
        var (v2, _) = QaDispatcher.ParseQaOutput("preamble\nQA_VERDICT: fail\nbroken menu");
        Assert.Equal("fail", v2);
        var (v3, _) = QaDispatcher.ParseQaOutput("no marker here");
        Assert.Null(v3);
        var (v4, _) = QaDispatcher.ParseQaOutput("QA_VERDICT: maybe");
        Assert.Null(v4);
    }

    [Fact]
    public async Task Pass_WithRasterEvidence_CommitsPushes_RecordsMetadata_ThenDedupes()
    {
        var runner = new FakeRunner();
        runner.Drops.Add(("test-results/qa/task-1/01-boot.png", "fake-png-bytes"));
        runner.Drops.Add(("test-results/qa/task-1/notes.md", "observations"));
        var task = await SeedTask();
        var codeHead = PrHeadSha(_workDir);
        var dispatcher = NewDispatcher(runner);

        var outcome = await dispatcher.VerifyOnceAsync(task, CancellationToken.None,
            headOverride: _ => (codeHead, "agent/task-1"));

        Assert.NotNull(outcome);
        Assert.True(outcome!.Verdict == QaDispatcher.VerdictPass, $"expected pass, got {outcome.Verdict}: {outcome.Error}");
        Assert.Equal(1, runner.Calls);
        // The evidence commit landed on the PR branch (head moved).
        var newHead = PrHeadSha(_workDir);
        Assert.NotEqual(codeHead, newHead);
        Assert.Equal(newHead, outcome.HeadSha);
        var files = GitOut(_workDir, "ls-tree -r --name-only origin/agent/task-1");
        Assert.Contains("test-results/qa/task-1/01-boot.png", files);
        // Metadata: the verdict applies to the post-push head; qaForSha
        // keeps the code head that was verified.
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(newHead, after.GetMetadata("qaSha"));
        Assert.Equal(codeHead, after.GetMetadata("qaForSha"));
        Assert.Equal("pass", after.GetMetadata("qaVerdict"));
        Assert.Equal("1", after.GetMetadata("qaRound"));
        Assert.Equal("visual", after.GetMetadata("qaTier"));
        Assert.Null(after.GetMetadata("qaAttempts"));

        // Second call at the same head: deduped, no new run.
        var second = await dispatcher.VerifyOnceAsync(
            (await _issues.GetAsync(task.Id))!, CancellationToken.None,
            headOverride: _ => (newHead, "agent/task-1"));
        Assert.Null(second);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task Pass_WithoutRasterEvidence_IsNotQa()
    {
        var runner = new FakeRunner();
        runner.Drops.Add(("test-results/qa/task-1/state-dump.json", "{}"));
        var task = await SeedTask();
        var codeHead = PrHeadSha(_workDir);
        var dispatcher = NewDispatcher(runner);

        var outcome = await dispatcher.VerifyOnceAsync(task, CancellationToken.None,
            headOverride: _ => (codeHead, "agent/task-1"));

        Assert.NotNull(outcome);
        Assert.Equal(QaDispatcher.VerdictError, outcome!.Verdict);
        Assert.Contains("raster", outcome.Error);
        // Nothing shipped; the attempt budget counts the error.
        Assert.Equal(codeHead, PrHeadSha(_workDir));
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal("1", after.GetMetadata("qaAttempts"));
        Assert.Null(after.GetMetadata("qaVerdict"));
        // Error outcomes are never silent: the reason is stamped on the
        // task (TaskDetail-visible) — the 2026-08-24 task-740 loop was
        // invisible precisely because these paths left no trace.
        Assert.Contains("raster", after.GetMetadata("qaLastError"));
        Assert.NotNull(after.GetMetadata("qaLastErrorAt"));
    }

    [Fact]
    public async Task LandedVerdict_ClearsTheErrorStamp()
    {
        // First attempt errors (no marker) → qaLastError stamped; the
        // retry at the same head passes → the stamp clears with the
        // landed verdict (no stale error next to a green verdict).
        var runner = new FakeRunner { Reply = "no marker here" };
        var task = await SeedTask();
        var codeHead = PrHeadSha(_workDir);
        var dispatcher = NewDispatcher(runner);

        var errored = await dispatcher.VerifyOnceAsync(task, CancellationToken.None,
            headOverride: _ => (codeHead, "agent/task-1"));
        Assert.Equal(QaDispatcher.VerdictError, errored!.Verdict);
        Assert.NotNull(((await _issues.GetAsync(task.Id))!).GetMetadata("qaLastError"));

        runner.Reply = "QA_VERDICT: pass\nplayed it";
        runner.Drops.Add(("test-results/qa/task-1/01.png", "png"));
        var passed = await dispatcher.VerifyOnceAsync(
            (await _issues.GetAsync(task.Id))!, CancellationToken.None,
            headOverride: _ => (codeHead, "agent/task-1"));
        Assert.Equal(QaDispatcher.VerdictPass, passed!.Verdict);
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Null(after.GetMetadata("qaLastError"));
        Assert.Null(after.GetMetadata("qaLastErrorAt"));
    }

    [Fact]
    public async Task ClearedBudget_ReAttemptsQaAtTheSameHead()
    {
        // The operator-requeue contract (task-740 loop): a
        // qa-unavailable park burns the per-head budget; clearing the
        // budget keys (what POST /api/tasks/{id}/requeue now does)
        // lets QA re-attempt at the SAME head instead of instantly
        // re-blocking.
        var runner = new FakeRunner { Reply = "no verdict marker at all" };
        var task = await SeedTask();
        var codeHead = PrHeadSha(_workDir);
        var dispatcher = NewDispatcher(runner);

        for (var i = 0; i < QaDispatcher.MaxQaAttempts; i++)
        {
            await dispatcher.VerifyOnceAsync(
                (await _issues.GetAsync(task.Id))!, CancellationToken.None,
                headOverride: _ => (codeHead, "agent/task-1"));
        }
        Assert.Equal(QaDispatcher.MaxQaAttempts, runner.Calls);

        // Budget exhausted: the next launch parks without running.
        var parked = await dispatcher.VerifyOnceAsync(
            (await _issues.GetAsync(task.Id))!, CancellationToken.None,
            headOverride: _ => (codeHead, "agent/task-1"));
        Assert.Equal(QaDispatcher.VerdictError, parked!.Verdict);
        Assert.Equal(QaDispatcher.MaxQaAttempts, runner.Calls);
        Assert.Equal(IssueStatus.Blocked, ((await _issues.GetAsync(task.Id))!).Status);

        // Operator requeue: Pending + the QA budget keys cleared.
        await _issues.TransitionAsync(task.Id, IssueStatus.Pending, "operator requeue",
            new Dictionary<string, object>
            {
                ["qaAttempts"] = null!,
                ["qaAttemptSha"] = null!,
                ["qaStartedAt"] = null!,
                ["blockedKind"] = null!,
            });

        var retry = await dispatcher.VerifyOnceAsync(
            (await _issues.GetAsync(task.Id))!, CancellationToken.None,
            headOverride: _ => (codeHead, "agent/task-1"));
        Assert.Equal(QaDispatcher.VerdictError, retry!.Verdict); // still no marker — but it RAN
        Assert.Equal(QaDispatcher.MaxQaAttempts + 1, runner.Calls);
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal("1", after.GetMetadata("qaAttempts"));
    }

    [Fact]
    public async Task NonEvidencePaths_RefuseThePush()
    {
        var runner = new FakeRunner();
        runner.Drops.Add(("test-results/qa/task-1/01.png", "png"));
        runner.Drops.Add(("src/Hacked.cs", "class Hacked {}"));
        var task = await SeedTask();
        var codeHead = PrHeadSha(_workDir);
        var dispatcher = NewDispatcher(runner);

        var outcome = await dispatcher.VerifyOnceAsync(task, CancellationToken.None,
            headOverride: _ => (codeHead, "agent/task-1"));

        Assert.Equal(QaDispatcher.VerdictError, outcome!.Verdict);
        Assert.Contains("non-evidence", outcome.Error);
        Assert.Equal(codeHead, PrHeadSha(_workDir));
    }

    [Fact]
    public async Task AttemptBudgetExhausted_ParksQaUnavailable()
    {
        var runner = new FakeRunner { Reply = "no verdict marker at all" };
        var task = await SeedTask();
        var codeHead = PrHeadSha(_workDir);
        var dispatcher = NewDispatcher(runner);

        for (var i = 0; i < QaDispatcher.MaxQaAttempts; i++)
        {
            var outcome = await dispatcher.VerifyOnceAsync(
                (await _issues.GetAsync(task.Id))!, CancellationToken.None,
                headOverride: _ => (codeHead, "agent/task-1"));
            Assert.Equal(QaDispatcher.VerdictError, outcome!.Verdict);
        }

        // Third call: budget exhausted → Blocked with the qa-unavailable marker.
        var third = await dispatcher.VerifyOnceAsync(
            (await _issues.GetAsync(task.Id))!, CancellationToken.None,
            headOverride: _ => (codeHead, "agent/task-1"));
        Assert.Equal(QaDispatcher.VerdictError, third!.Verdict);
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(IssueStatus.Blocked, after.Status);
        Assert.Equal(QaDispatcher.BlockedKindQaUnavailable, after.GetMetadata("blockedKind"));
        // The park is audited too: the budget-exhaustion reason is on
        // the task, not just in the transition note.
        Assert.Contains("budget exhausted", after.GetMetadata("qaLastError"));
        Assert.NotNull(after.GetMetadata("qaLastErrorAt"));
    }

    [Fact]
    public async Task FailVerdict_RecordedAtHead_NoRerunUntilNewHead()
    {
        var runner = new FakeRunner { Reply = "QA_VERDICT: fail\nmenu button does nothing" };
        runner.Drops.Add(("test-results/qa/task-1/01-fail.png", "png"));
        var task = await SeedTask();
        var codeHead = PrHeadSha(_workDir);
        var dispatcher = NewDispatcher(runner);

        var outcome = await dispatcher.VerifyOnceAsync(task, CancellationToken.None,
            headOverride: _ => (codeHead, "agent/task-1"));
        Assert.Equal(QaDispatcher.VerdictFail, outcome!.Verdict);
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal("fail", after.GetMetadata("qaVerdict"));
        Assert.Contains("menu button", after.GetMetadata("qaNotes"));

        // Same head: no re-run (the watcher turns the fail into a
        // rework round; QA re-runs on the rework push).
        var second = await dispatcher.VerifyOnceAsync(after, CancellationToken.None,
            headOverride: _ => (outcome.HeadSha, "agent/task-1"));
        Assert.Null(second);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task Tier3DocsOnly_StampsNotApplicable_NoRunNoAttemptSpent()
    {
        var runner = new FakeRunner();
        var task = await SeedTask();
        var docsHead = DocsHeadSha(_workDir);
        var dispatcher = NewDispatcher(runner);

        var outcome = await dispatcher.VerifyOnceAsync(task, CancellationToken.None,
            headOverride: _ => (docsHead, "agent/task-docs"));

        Assert.NotNull(outcome);
        Assert.Equal(QaDispatcher.VerdictNotApplicable, outcome!.Verdict);
        Assert.Equal(docsHead, outcome.HeadSha);
        // No agent run, no attempt spent, no evidence commit — the head
        // is untouched.
        Assert.Equal(0, runner.Calls);
        Assert.Equal(docsHead, DocsHeadSha(_workDir));
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal(docsHead, after.GetMetadata("qaSha"));
        Assert.Equal(docsHead, after.GetMetadata("qaForSha"));
        Assert.Equal("not-applicable", after.GetMetadata("qaVerdict"));
        Assert.Equal("docs", after.GetMetadata("qaTier"));
        Assert.Contains("docs-only diff", after.GetMetadata("qaNotes"));
        Assert.Null(after.GetMetadata("qaAttempts"));
        Assert.Null(after.GetMetadata("qaAttemptSha"));
        Assert.Null(after.GetMetadata("qaStartedAt"));

        // Dedupe: the stamped verdict at the head suppresses re-evaluation.
        var second = await dispatcher.VerifyOnceAsync(after, CancellationToken.None,
            headOverride: _ => (docsHead, "agent/task-docs"));
        Assert.Null(second);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Tier2Code_AnyFileEvidenceAccepted_RasterNotDemanded()
    {
        // No visual prefixes configured ⇒ nothing visual: the branch's
        // Client/ + feature.txt diff is tier 2. A state-assertion dump
        // (JSON — never acceptable as tier-1 evidence) satisfies the
        // tier-2 any-file bar.
        var runner = new FakeRunner();
        runner.Drops.Add(("test-results/qa/task-1/state-dump.json", "{}"));
        var task = await SeedTask();
        var codeHead = PrHeadSha(_workDir);
        var dispatcher = NewDispatcher(runner, Array.Empty<string>());

        var outcome = await dispatcher.VerifyOnceAsync(task, CancellationToken.None,
            headOverride: _ => (codeHead, "agent/task-1"));

        Assert.NotNull(outcome);
        Assert.True(outcome!.Verdict == QaDispatcher.VerdictPass, $"expected pass, got {outcome.Verdict}: {outcome.Error}");
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Equal("pass", after.GetMetadata("qaVerdict"));
        Assert.Equal("code", after.GetMetadata("qaTier"));
        // The evidence commit landed (head moved) — dispatcher-shipped.
        Assert.NotEqual(codeHead, PrHeadSha(_workDir));
    }

    [Fact]
    public async Task Tier2Code_ZeroEvidencePass_DiscardedAsNotQa()
    {
        var runner = new FakeRunner(); // drops nothing
        var task = await SeedTask();
        var codeHead = PrHeadSha(_workDir);
        var dispatcher = NewDispatcher(runner, Array.Empty<string>());

        var outcome = await dispatcher.VerifyOnceAsync(task, CancellationToken.None,
            headOverride: _ => (codeHead, "agent/task-1"));

        Assert.NotNull(outcome);
        Assert.Equal(QaDispatcher.VerdictError, outcome!.Verdict);
        Assert.Contains("evidence files", outcome.Error);
        Assert.Equal(codeHead, PrHeadSha(_workDir)); // nothing shipped
        var after = (await _issues.GetAsync(task.Id))!;
        Assert.Null(after.GetMetadata("qaVerdict"));
        Assert.Equal("code", after.GetMetadata("qaTier"));
        Assert.Contains("evidence files", after.GetMetadata("qaLastError"));
    }
}
