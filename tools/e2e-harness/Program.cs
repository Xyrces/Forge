using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Projects;
using Forge.Reviewer;

namespace Forge.Tools.E2E;

/// <summary>
/// E2E harness — proves the orchestrator can take a spec and
/// open a PR with the right code on a fresh local repo. No
/// GitHub token, no real network calls. Runs in-process so the
/// harness has direct access to the LocalGitHubService's
/// PR store for assertions.
///
/// <para>
/// Pipeline:
/// <list type="number">
///   <item>Create a fresh bare git + a clone with a stub
///   scaffold (Calculator.cs + CalculatorTests.cs + .csproj).</item>
///   <item>Wire up the orchestrator's components with a
///   <see cref="LocalGitHubService"/> pointed at the bare
///   remote.</item>
///   <item>Enqueue a task directly via the
///   <see cref="OrchestratorAgent"/> (no intake path; that's
///   covered by IntakeAgentTests).</item>
///   <item>Wait for the orchestrator to dispatch the task +
///   run the engineering workflow + open the PR.</item>
///   <item>Assert the PR exists + the diff contains the
///   expected files.</item>
/// </list>
/// </para>
///
/// <para>
/// Use: <c>dotnet run --project tools/e2e-harness</c> from the
/// repo root.
/// </para>
///
/// <para>
/// Bypasses the LLM (M3) by writing the file changes directly
/// to the worktree during a custom <see cref="FakeAgentRunner"/>.
/// This keeps the harness fast (a few seconds) and
/// deterministic — we're verifying the orchestrator's wiring,
/// not the model's code quality. Run the real LLM-driven
/// pipeline by replacing <see cref="FakeAgentRunner"/> with a
/// MAF agent runner pointing at the kilo gateway; the
/// harness is otherwise identical.
/// </para>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var repoRoot = args.FirstOrDefault(a => a.StartsWith("--repo-root="))?.Split('=', 2)[1]
            ?? FindRepoRoot() ?? throw new InvalidOperationException("Cannot find repo root");
        var workspaceRoot = Path.Combine(repoRoot, ".portHorizon", "e2e");
        // Clean up any prior workspace. git's worktree metadata
        // can leave locked files; ask git to remove the
        // worktrees first, then delete the directory.
        if (Directory.Exists(workspaceRoot))
        {
            var cloneDir = Path.Combine(workspaceRoot, "clone");
            if (Directory.Exists(cloneDir))
            {
                try { Git.Run("worktree remove --force .", cloneDir); } catch { /* ignore */ }
            }
            try { Directory.Delete(workspaceRoot, recursive: true); } catch { /* tolerate */ }
        }
        Directory.CreateDirectory(workspaceRoot);

        Console.WriteLine($"E2E harness: workspace={workspaceRoot}");

        // 1. Set up the bare git + clone with scaffold.
        var bare = Path.Combine(workspaceRoot, "remote.git");
        var clone = Path.Combine(workspaceRoot, "clone");
        // git init creates the directory, but Process.Start
        // pre-checks the cwd exists. Create the dirs up-front.
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(clone);
        Git.Run($"init -q --bare {bare}", workspaceRoot);
        Git.Run("init -q -b main", clone);
        Git.Run("config user.email e2e@local", clone);
        Git.Run("config user.name e2e-harness", clone);
        Git.Run($"remote add origin {bare}", clone);
        WriteScaffold(clone);
        Git.Run("add .", clone);
        Git.Run("commit -q -m scaffold", clone);
        Git.Run("push -q -u origin main", clone);

        // 2. Build the LocalGitHubService + wire up
        // orchestrator components.
        var dbPath = Path.Combine(workspaceRoot, "issues.db");
        var issues = new IssueStore(dbPath);
        var designArtifacts = new DesignArtifactStore(dbPath);
        var designerRuns = new DesignerRunStore(dbPath);
        var artOutputs = new ArtOutputStore(dbPath);
        // P5.5: harness uses a no-op extractor + ephemeral
        // extraction store; we don't want the harness to call
        // the real LLM. The CommitPushPrExecutor's caller is
        // blocked by the worktree cleanup later anyway (the
        // harness short-circuits before PR open by not
        // configuring a real GitHubService), so this code
        // path is only reached in the closed-loop tests.
        var harnessExtractionStore = new Orchestrator.MemoryExtractionStore(
            Path.Combine(workspaceRoot, "extraction.db"));
        Orchestrator.IMemoryExtractor harnessExtractor =
            new Orchestrator.NoOpMemoryExtractor();
        var artistRuns = new ArtistRunStore(dbPath);
        var groomerRuns = new IssueGroomerRunStore(dbPath);
        var recoveryReports = new RecoveryReportStore(dbPath);
        var intakeStore = new Core.IntakeStore(issues);
        var agentsStore = new Core.AgentStore(issues);
        var skillsStore = new Core.SkillStore(issues);
        var sprints = new Core.SprintStore(issues);
        var messageBus = new AgentMessageBus();
        var worktrees = new GitWorktreeService(
            new WorkspaceOptions { Root = clone, WorktreeRoot = ".portHorizon/worktrees", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance);
        var gitHub = new LocalGitHubService(bare, "local", "e2e");
        var gitHubForRecovery = new GitHubRecoveryAdapter(gitHub);
        var roleRegistry = new RoleAgentRegistry();
        var eventBus = new InMemoryDashboardEventBus();
        var recovery = new StartupRecovery(
            issues, recoveryReports, worktrees, gitHubForRecovery, eventBus,
            NullLogger<StartupRecovery>.Instance);

        // Wire GitWorktreeService's push hook into the
        // LocalGitHubService: when the orchestrator's
        // worktree service pushes a branch to the bare remote,
        // record the head SHA so the harness's PR store has
        // it. We hook via a small reflective adapter since
        // GitWorktreeService.PushAsync doesn't expose a
        // callback.
        // The bridge: capture the head SHA right after push via
        // a wrapper service that records the SHA from the bare
        // remote. We do this by tailing the bare repo's refs.
        var bridge = new PushBridge(bare, gitHub);

        // 3. Agent runner. By default the harness uses a
        // FakeAgentRunner that hard-codes the expected files
        // (fast, deterministic, no LLM cost). Pass --real-llm
        // to swap in a real MafAgentRunner pointed at the kilo
        // gateway. The LLM is asked to write the same files the
        // FakeAgentRunner writes; the assertions are unchanged.
        var specBody = """
            Implement a small Calculator class in MyApp with a static Add method that returns the sum of two ints.
            Then add an xUnit test in MyApp/CalculatorTests.cs that asserts Add(2, 3) == 5.

            Acceptance criteria:
            - Calculator.cs exists with the Add method
            - CalculatorTests.cs exists with the Add_TwoPositiveNumbers_ReturnsSum test
            """;
        var useRealLlm = args.Any(a => a == "--real-llm");
        var useHeadroom = args.Any(a => a == "--headroom");
        IAgentRunner runner;
        CostTracker? costTracker = null;
if (useRealLlm)
            {
                var llmApiKey = Environment.GetEnvironmentVariable("LLM_API_KEY")
                    ?? throw new InvalidOperationException("--real-llm requires LLM_API_KEY env var");
                var llmBase = Environment.GetEnvironmentVariable("LLM_BASE_URL") ?? "https://api.kilo.ai/api/gateway";
                var llmModel = Environment.GetEnvironmentVariable("LLM_MODEL") ?? "minimax/minimax-m3";
                var llmOptions = new Configuration.LlmOptions
                {
                    DefaultProvider = "kilo-gateway",
                    Providers = new List<Configuration.LlmProviderOptions>
                    {
                        new()
                        {
                            Name = "kilo-gateway",
                            BaseUrl = llmBase,
                            ApiKey = llmApiKey,
                            DefaultModel = llmModel,
                        },
                    },
                    Roles = new Dictionary<string, Configuration.LlmRoleModelOptions>
                    {
                        ["CoreDev"] = new() { ProviderName = "kilo-gateway", Model = llmModel },
                    },
                };
                var llmConfig = LlmConfigAdapter.FromOptions(llmOptions);
                var chatClientFactory = new OpenAICompatibleChatClientFactory();
                // Track usage on every --real-llm run. CostTracker is
                // cheap (in-process dict + lock); we want both the
                // baseline and the Headroom-enabled runs to print
                // the same summary so they can be compared.
                costTracker = new CostTracker();
                chatClientFactory.CostTracker = costTracker;
                if (useHeadroom)
                {
                    var headroomUrl = Environment.GetEnvironmentVariable("HEADROOM_PROXY_URL") ?? "http://127.0.0.1:8787";
                    chatClientFactory.HeadroomProxyBaseUrl = headroomUrl;
                    Console.WriteLine($"  runner = MafAgentRunner (LLM-driven, Headroom proxy={headroomUrl})");
                }
                else
                {
                    Console.WriteLine("  runner = MafAgentRunner (LLM-driven, no Headroom)");
                }
                runner = new MafAgentRunner(
                    chatClientFactory, llmConfig, roleRegistry,
                    NullLogger<MafAgentRunner>.Instance);
            }
        else
        {
            runner = new FakeAgentRunner(worktrees, specBody);
            Console.WriteLine("  runner = FakeAgentRunner (deterministic, no LLM)");
        }

        // 4. Build the orchestrator.
        var harnessBundle = new ProjectDispatchBundle(
            project: new ProjectOptions
            {
                Id = "harness",
                Name = "Harness",
                RepoUrl = "",
                DefaultBranch = "main",
                Root = clone,
            },
            issueStore: issues,
            agents: agentsStore,
            sprints: sprints,
            designArtifacts: designArtifacts,
            artOutputs: artOutputs,
            worktrees: worktrees,
            gitHub: gitHub,
            prWatcher: new PRWatcher(gitHub, worktrees, issues,
                TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), eventBus,
                NullLogger<PRWatcher>.Instance),
            events: eventBus,
            logger: NullLogger<ProjectDispatchBundle>.Instance);

        var projectStore = new ProjectStore(issues);
        await projectStore.UpsertAsync(new NewProject(
            Id: "harness", Name: "Harness", RepoUrl: clone, DefaultBranch: "main"));

        var bundleFactory = new ProjectDispatchBundleFactory(
            options: new AgentOptions { GitHub = new GitHubOptions() },
            dataRoot: Path.GetDirectoryName(dbPath)!,
            projectStore: projectStore,
            cloner: new ProjectCloner(Path.GetDirectoryName(dbPath)!, NullLogger<ProjectCloner>.Instance),
            runner: runner,
            roleRegistry: roleRegistry,
            dispatcher: new InProcessDispatcher(
                async (issue, bundle, ct) =>
                {
                    var wf = new Orchestrator.Workflow.EngineeringDispatchWorkflow(
                        issues, runner, worktrees, gitHub, roleRegistry,
                        new WorkspaceOptions { Root = clone, WorktreeRoot = ".portHorizon/worktrees", DefaultBranch = "main" },
                        eventBus, agent => messageBus.Drain(agent),
                        designArtifacts, artOutputs,
                        harnessExtractor, harnessExtractionStore,
                        NullLogger<Orchestrator.Workflow.EngineeringDispatchWorkflow>.Instance);
                    await wf.RunAsync(issue, ct);
                },
                NullLogger<InProcessDispatcher>.Instance),
            messageBus: messageBus,
            events: eventBus,
            loggerFactory: NullLoggerFactory.Instance);

        var orchestrator = new OrchestratorAgent(
            projectStore,
            bundleFactory,
            runner, roleRegistry,
            messageBus,
            new InProcessDispatcher(
                async (issue, bundle, ct) =>
                {
                    var wf = new Orchestrator.Workflow.EngineeringDispatchWorkflow(
                        issues, runner, worktrees, gitHub, roleRegistry,
                        new WorkspaceOptions { Root = clone, WorktreeRoot = ".portHorizon/worktrees", DefaultBranch = "main" },
                        eventBus, agent => messageBus.Drain(agent),
                        designArtifacts, artOutputs,
                        harnessExtractor, harnessExtractionStore,
                        NullLogger<Orchestrator.Workflow.EngineeringDispatchWorkflow>.Instance);
                    await wf.RunAsync(issue, ct);
                },
                NullLogger<InProcessDispatcher>.Instance),
            eventBus,
            NullLogger<OrchestratorAgent>.Instance);

        // 5. Skip the design + artist + groomer stages by
        // creating a "groomed" task directly. The orchestrator
        // will pick it up + dispatch.
        var task = await issues.CreateAsync(new NewIssue(
            Type: "task",
            Title: "Add Calculator with Add method + xUnit test",
            Description: specBody,
            Priority: 2));
        // Walk to ReadyForDesign → Designed → AssetReady → ReadyForGroom
        // → Groomed (the orchestrator's Groomer agent picks up
        // Groomed tasks; we short-circuit by claiming + setting
        // status to Completed manually). For the harness we
        // only need the engineering dispatch to fire; we
        // pre-stage the task at ReadyForDesign + pretend
        // Designer + Artist + Groomer ran (no design artifacts,
        // no art outputs — engineering dispatch ignores those
        // for tasks with no spec).
        await issues.TransitionAsync(task.Id, IssueStatus.Pending, error: null);
        // Bypass the Groomer: skip directly to Pending for dispatch.

        // 6. Run a single dispatch cycle. The orchestrator will
        // claim the task + run the engineering workflow +
        // open the PR via LocalGitHubService.
        Console.WriteLine("  running one dispatch cycle...");
        var result = await orchestrator.DispatchSingleTaskAsync(
            (await issues.GetAsync(task.Id))!,
            harnessBundle,
            CancellationToken.None);
        Console.WriteLine($"  dispatch result: success={result.Success} message={result.Message}");

        // 7. Assertions.
        var prs = gitHub.PrStore.AllPrs.ToList();
        if (prs.Count == 0)
        {
            Console.WriteLine("  FAIL: no PRs were opened");
            return 1;
        }
        var pr = prs[0];
        var meta = gitHub.PrStore.PrInfo[pr.Number];
        Console.WriteLine($"  PR #{pr.Number}: title=\"{meta.Title}\" head={meta.HeadBranch} base={meta.BaseBranch}");

        // State-driven watching: no pr-watch row exists — the dev
        // task itself carries prNumber and is driven directly.
        var watchTask = (await issues.GetAsync(task.Id, default))!;
        if (watchTask.GetMetadata("prNumber") != pr.Number.ToString())
        {
            Console.WriteLine($"  FAIL: task {task.Id} has no prNumber metadata (state-driven watching)");
            return 1;
        }
        Console.WriteLine($"  watched task {watchTask.Id} (prNumber={watchTask.GetMetadata("prNumber")})");

        // Read the agent's worktree (the orchestrator's
        // GitWorktreeService creates a worktree per task). The
        // branch is already checked out there; we don't need
        // to fetch/checkout in the main clone (and we can't,
        // because the branch is "already used by worktree").
        var worktree = Path.Combine(clone, ".portHorizon", "worktrees", "task-1");
        if (!Directory.Exists(worktree))
        {
            Console.WriteLine($"  FAIL: agent worktree not found at {worktree}");
            return 1;
        }
        // Get the head SHA from the bare remote's refs/heads.
        // We can't read pr.Head.Sha because LocalGitHubService's
        // PR is a bare Octokit object (Head is empty); the SHA
        // lives on the local remote ref.
        var headSha = File.ReadAllText(Path.Combine(bare, "refs", "heads", meta.HeadBranch)).Trim();
        Console.WriteLine($"  head SHA: {headSha}");

        // Step 1: assert the PR diff is correct.
        var diff = Git.Capture($"diff --stat main..{meta.HeadBranch}", worktree);
        Console.WriteLine("  diff:\n" + diff);

        var calcPath = Path.Combine(worktree, "Calculator.cs");
        var testsPath = Path.Combine(worktree, "CalculatorTests.cs");
        var errors = new List<string>();
        if (!File.Exists(calcPath))
            errors.Add("Calculator.cs not in agent's branch");
        else
        {
            var content = File.ReadAllText(calcPath);
            if (!content.Contains("class Calculator") || !content.Contains("Add(int a, int b)"))
                errors.Add("Calculator.cs is missing the Add method");
        }
        if (!File.Exists(testsPath))
            errors.Add("CalculatorTests.cs not in agent's branch");
        else
        {
            var content = File.ReadAllText(testsPath);
            if (!content.Contains("Add_TwoPositiveNumbers_ReturnsSum"))
                errors.Add("CalculatorTests.cs is missing the Add_TwoPositiveNumbers_ReturnsSum test");
        }

        if (errors.Count > 0)
        {
            Console.WriteLine("  FAIL:");
            foreach (var e in errors) Console.WriteLine("    - " + e);
            return 1;
        }

        // Step 2: drive the closed loop. Mark CI green, set up
        // an approved review override, and let the PRWatcher
        // poll until GreenAndApproved → merge.
        Console.WriteLine("  driving closed loop: mark CI green + approve review + drive PRWatcher");
        gitHub.PrStore.MarkCiGreen(headSha);
        var prWatcher = new PRWatcher(
            gitHub, worktrees, issues,
            TimeSpan.FromMilliseconds(50),    // poll interval
            TimeSpan.FromMinutes(5),          // stale after
            eventBus,
            NullLogger<PRWatcher>.Instance);
        // reviewsOverride returns Approved; PRWatcher sees
        // GreenAndApproved on its first poll. changedFilesOverride:
        // the fabricated Octokit PullRequest can't carry ChangedFiles
        // (sealed, init-only — see LocalPrStore.CreatePr), so the
        // empty-diff supersede guard would read 0 and close EVERY
        // harness PR as superseded; compute the real count from the
        // bare remote (broke the closed loop 2026-08-14).
        var watchResult = await prWatcher.ProcessWatchedTaskAsync(
            watchTask, CancellationToken.None,
            reviewsOverride: _ => new[] { Octokit.PullRequestReviewState.Approved },
            headShaOverride: _ => headSha,
            changedFilesOverride: p => CountChangedFiles(gitHub, p.Number));
        Console.WriteLine($"  PRWatcher.ProcessWatchedTaskAsync exit code = {watchResult}");

        // Step 3: verify the closed loop closed. The PR should
        // have been merged and the task Completed.
        if (!gitHub.PrStore.WasMerged(pr.Number))
        {
            errors.Add("PR was not merged (LocalGitHubService.MergePullRequestAsync never returned true)");
        }
        var finalTask = (await issues.GetAsync(task.Id))!;
        if (finalTask.Status != IssueStatus.Completed)
            errors.Add($"original task is {finalTask.Status}, expected Completed");
        if (finalTask.GetMetadata("prNumber") != pr.Number.ToString())
            errors.Add($"original task has prNumber={finalTask.GetMetadata("prNumber")}, expected {pr.Number}");

        if (errors.Count > 0)
        {
            Console.WriteLine("  FAIL:");
            foreach (var e in errors) Console.WriteLine("    - " + e);
            return 1;
        }
        Console.WriteLine("  PASS: PR #" + pr.Number + " contains Calculator.cs + CalculatorTests.cs + closed loop merged + tasks Completed.");

        // Cost summary: only meaningful when --real-llm (with
        // or without Headroom). We always print it when a real
        // LLM was used; with --headroom we also pull the proxy's
        // /stats for pre-compression comparison.
        if (useRealLlm && costTracker is not null)
        {
            var snap = costTracker.Snapshot();
            Console.WriteLine();
            Console.WriteLine("  cost summary (orchestrator-observed, post-compression):");
            Console.WriteLine($"    calls      = {snap.CallCount}");
            Console.WriteLine($"    input tok  = {snap.TotalInputTokens:N0}");
            Console.WriteLine($"    output tok = {snap.TotalOutputTokens:N0}");
        }
        if (useRealLlm && useHeadroom)
        {
            try
            {
                var headroomUrl = Environment.GetEnvironmentVariable("HEADROOM_PROXY_URL") ?? "http://127.0.0.1:8787";
                var headroomStats = await HeadroomStats.FetchAsync(headroomUrl);
                if (headroomStats is not null)
                {
                    Console.WriteLine();
                    Console.WriteLine("  Headroom /stats (proxy-side):");
                    Console.WriteLine($"    mode                       = {headroomStats.Mode}");
                    Console.WriteLine($"    api requests               = {headroomStats.ApiRequests}");
                    Console.WriteLine($"    tokens.input               = {headroomStats.InputTokens:N0}");
                    Console.WriteLine($"    tokens.proxy_total_before   = {headroomStats.ProxyTotalBeforeCompression:N0}");
                    Console.WriteLine($"    tokens.saved               = {headroomStats.TokensSaved:N0}");
                    Console.WriteLine($"    tokens.savings_percent     = {headroomStats.SavingsPercent:F2}%");
                    Console.WriteLine($"    compression.requests       = {headroomStats.RequestsCompressed}");
                    Console.WriteLine($"    compression.avg_pct        = {headroomStats.AvgCompressionPct:F2}%");
                    Console.WriteLine($"    cost.without_headroom_usd  = {headroomStats.CostWithoutHeadroom:F6}");
                    Console.WriteLine($"    cost.with_headroom_usd     = {headroomStats.CostWithHeadroom:F6}");
                    Console.WriteLine($"    cost.total_saved_usd       = {headroomStats.CostSaved:F6}");
                    Console.WriteLine($"    latency.average_ms         = {headroomStats.LatencyAvgMs:F1}");
                    Console.WriteLine($"    latency.max_ms             = {headroomStats.LatencyMaxMs:F1}");
                }
                else
                {
                    Console.WriteLine($"  (Headroom /stats fetch failed from {headroomUrl})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  (Headroom /stats error: {ex.Message})");
            }
        }
        return 0;
    }

    private static int CountChangedFiles(Forge.LocalGitHubService gitHub, int prNumber)
    {
        var info = gitHub.PrStore.PrInfo[prNumber];
        var output = Git.Capture(
            $"--git-dir=\"{gitHub.LocalRemotePath}\" diff --name-only {info.BaseBranch}...{info.HeadBranch}",
            gitHub.LocalRemotePath);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    private static void WriteScaffold(string clone)    {
        File.WriteAllText(Path.Combine(clone, "MyApp.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
                <PackageReference Include="xunit" Version="2.9.2" />
              </ItemGroup>
            </Project>
            """);
    }

    private static string? FindRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git"))
                && File.Exists(Path.Combine(dir, "Forge.Core", "Forge.Core.csproj")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}

/// <summary>
/// Watches the local bare git's refs/heads/ directory; when a
/// new branch ref appears, fetches the SHA + calls
/// <see cref="LocalGitHubService.RegisterPushedBranch"/>.
/// </summary>
internal sealed class PushBridge : IDisposable
{
    private readonly Thread _thread;
    private readonly string _bare;
    private readonly LocalGitHubService _service;
    private volatile bool _running = true;
    private readonly HashSet<string> _seen = new();

    public PushBridge(string barePath, LocalGitHubService service)
    {
        _bare = barePath;
        _service = service;
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    private void Loop()
    {
        var refsDir = Path.Combine(_bare, "refs", "heads");
        while (_running)
        {
            try
            {
                if (Directory.Exists(refsDir))
                {
                    foreach (var file in Directory.EnumerateFiles(refsDir, "*", SearchOption.AllDirectories))
                    {
                        var branch = Path.GetRelativePath(refsDir, file).Replace('\\', '/');
                        if (_seen.Contains(branch)) continue;
                        _seen.Add(branch);
                        var sha = File.ReadAllText(file).Trim();
                        _service.RegisterPushedBranch(branch, sha);
                    }
                }
            }
            catch { /* tolerate transient state */ }
            Thread.Sleep(200);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(2000);
    }
}

/// <summary>
/// Fake <see cref="IAgentRunner"/> that writes the spec'd files
/// (Calculator + tests) into the worktree when invoked. Used by
/// the harness to bypass the LLM. The orchestrator's
/// <c>EngineeringDispatchWorkflow</c> passes
/// <c>context.worktreePath</c> in; we use that to find the
/// worktree.
/// </summary>
internal sealed class FakeAgentRunner : IAgentRunner
{
    private readonly GitWorktreeService _worktrees;
    private readonly string _specBody;

    public FakeAgentRunner(GitWorktreeService worktrees, string specBody)
    {
        _worktrees = worktrees;
        _specBody = specBody;
    }

    public Task<AgentRunResult> RunAsync(
        AgentType role, string prompt,
        string? sessionId = null,
        IReadOnlyDictionary<string, object>? context = null,
        CancellationToken cancellationToken = default)
    {
        var worktreePath = context is not null && context.TryGetValue("worktreePath", out var v) && v is string s
            ? s : throw new InvalidOperationException("worktreePath missing from context");
        // Write the spec'd files. In a real run the LLM would
        // generate these; here we hard-code the harness's
        // expected files.
        File.WriteAllText(Path.Combine(worktreePath, "Calculator.cs"), """
            namespace MyApp;

            public static class Calculator
            {
                public static int Add(int a, int b) => a + b;
            }
            """);
        File.WriteAllText(Path.Combine(worktreePath, "CalculatorTests.cs"), """
            using MyApp;
            using Xunit;

            public class CalculatorTests
            {
                [Fact]
                public void Add_TwoPositiveNumbers_ReturnsSum()
                {
                    Assert.Equal(5, Calculator.Add(2, 3));
                }
            }
            """);
        return Task.FromResult(new AgentRunResult(
            Text: _specBody,
            SessionId: "fake-session",
            InputTokens: 0,
            OutputTokens: 0,
            Elapsed: TimeSpan.FromSeconds(0.1)));
    }
}

/// <summary>
/// Reads Headroom's /stats endpoint and exposes the fields
/// the benchmark harness cares about. Returns null on failure
/// (the harness treats the Headroom section as best-effort).
/// </summary>
internal sealed record HeadroomStats(
    string Mode,
    long ApiRequests,
    long InputTokens,
    long ProxyTotalBeforeCompression,
    long TokensSaved,
    double SavingsPercent,
    long RequestsCompressed,
    double AvgCompressionPct,
    double CostWithoutHeadroom,
    double CostWithHeadroom,
    double CostSaved,
    double LatencyAvgMs,
    double LatencyMaxMs)
{
    public static async Task<HeadroomStats?> FetchAsync(string proxyUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await http.GetAsync($"{proxyUrl.TrimEnd('/')}/stats");
            if (!resp.IsSuccessStatusCode) return null;
            using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;
            // The Headroom /stats shape is wide; we pluck the
            // specific fields we report on.
            var summary = root.TryGetProperty("summary", out var s) ? s : root;
            var tokens = root.TryGetProperty("tokens", out var t) ? t : default;
            var compression = root.TryGetProperty("compression", out var c) ? c : default;
            var cost = root.TryGetProperty("cost", out var k) ? k : default;
            var latency = root.TryGetProperty("latency", out var l) ? l : default;
            return new HeadroomStats(
                Mode: summary.TryGetProperty("mode", out var m) ? m.GetString() ?? "" : "",
                ApiRequests: summary.TryGetProperty("api_requests", out var ar) ? ar.GetInt64() : 0,
                InputTokens: tokens.TryGetProperty("input", out var ti) ? ti.GetInt64() : 0,
                ProxyTotalBeforeCompression: tokens.TryGetProperty("proxy_total_before_compression", out var ptbc) ? ptbc.GetInt64() : 0,
                TokensSaved: tokens.TryGetProperty("saved", out var ts) ? ts.GetInt64() : 0,
                SavingsPercent: tokens.TryGetProperty("savings_percent", out var sp) ? sp.GetDouble() : 0,
                RequestsCompressed: compression.TryGetProperty("requests_compressed", out var rc) ? rc.GetInt64() : 0,
                AvgCompressionPct: compression.TryGetProperty("avg_compression_pct", out var ac) ? ac.GetDouble() : 0,
                CostWithoutHeadroom: cost.TryGetProperty("without_headroom_usd", out var cwh) ? cwh.GetDouble() : 0,
                CostWithHeadroom: cost.TryGetProperty("with_headroom_usd", out var cwh2) ? cwh2.GetDouble() : 0,
                CostSaved: cost.TryGetProperty("total_saved_usd", out var cs) ? cs.GetDouble() : 0,
                LatencyAvgMs: latency.TryGetProperty("average_ms", out var lam) ? lam.GetDouble() : 0,
                LatencyMaxMs: latency.TryGetProperty("max_ms", out var lmx) ? lmx.GetDouble() : 0);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Thin wrapper around git CLI for the harness.
/// </summary>
internal static class Git
{    public static void Run(string args, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        // P5.7: Read stdout/stderr FIRST, then WaitForExit.
        // The previous order (WaitForExit then ReadToEnd) deadlocks
        // on Windows once the output exceeds the ~4KB pipe buffer:
        // the child blocks writing, the parent waits for exit,
        // neither progresses. This is fine for small git output
        // (e.g. a 1-line status) but breaks for `git diff --stat`
        // when the agent commits bin/obj artifacts (109-file
        // diffs are common with the real LLM).
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {args} exited {p.ExitCode}\n{stdout}\n{stderr}");
    }

    public static string Capture(string args, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        // P5.7: same fix as Git.Run — read stdout first, then
        // WaitForExit, to avoid the Windows pipe-buffer deadlock.
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        // Drain stderr so it doesn't fill its own buffer.
        _ = p.StandardError.ReadToEnd();
        return stdout;
    }
}