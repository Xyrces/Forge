using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Projects;
using Forge.Reviewer;
using Microsoft.Extensions.Logging.Abstractions;

namespace Forge.Tools.E2E;

internal static class BenchmarkHarness
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--benchmark-self-test-graders", StringComparer.Ordinal))
            return await BenchmarkGraderSelfTest.RunAsync(CancellationToken.None);

        var resultPath = ReadOption(args, "--benchmark-result");
        if (string.IsNullOrWhiteSpace(resultPath) || !Path.IsPathFullyQualified(resultPath))
        {
            Console.Error.WriteLine("Benchmark mode requires --benchmark-result=<absolute path>.");
            return 2;
        }

        var mode = ReadOption(args, "--benchmark-mode") ?? "fake";
        var caseId = ReadOption(args, "--benchmark-case") ?? "unknown";
        var sensitiveValue = Environment.GetEnvironmentVariable("LLM_API_KEY");
        var model = mode == "live" ? Environment.GetEnvironmentVariable("LLM_MODEL") : null;
        var provider = mode == "live" ? Environment.GetEnvironmentVariable("LLM_PROVIDER") : null;
        var result = new BenchmarkResult
        {
            CaseId = caseId,
            Mode = mode,
            Model = model,
            Provider = provider,
            Usage = new BenchmarkResultUsage(
                Calls: 0,
                CompletedCalls: 0,
                FailedCalls: 0,
                InFlightCalls: 0,
                MissingUsageCalls: 0,
                InputTokens: 0,
                OutputTokens: 0,
                CachedInputTokens: 0,
                CacheWriteInputTokens: 0,
                KnownUsageEstimatedUsd: 0m,
                EstimatedCostUsd: 0m,
                AccountingComplete: true),
        };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var options = ParseOptions(args);
            var fixture = BenchmarkFixture.Get(options.CaseId);
            result = result with
            {
                CaseId = fixture.Id,
                Mode = options.Mode,
                Model = options.Mode == "live" ? model : null,
                Provider = options.Mode == "live" ? provider : null,
            };

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
            await ExecuteAsync(options, fixture, result, timeout.Token);
            result.Success = result.Checks.Count > 0 && result.Checks.All(static c => c.Passed);
            result.Outcome = result.Success
                ? options.Mode == "fake" ? "wiring-only-pass" : "accepted"
                : "acceptance-failed";
            return result.Success ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            result.Outcome = "timed-out";
            result.Error = "Benchmark exceeded its configured timeout.";
            return 1;
        }
        catch (Exception ex)
        {
            result.Outcome = "harness-error";
            result.Error = SanitizeError(ex.Message, sensitiveValue);
            Console.Error.WriteLine($"Benchmark failed: {result.Error}");
            return 1;
        }
        finally
        {
            result.ElapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            if (result.Meter is not null)
                result.Usage = BenchmarkResultUsage.From(result.Meter.Snapshot);
            try
            {
                var directory = Path.GetDirectoryName(resultPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(result, BenchmarkJsonContext.Default.BenchmarkResult);
                await File.WriteAllTextAsync(resultPath, json + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not write benchmark result: {SanitizeError(ex.Message, sensitiveValue)}");
            }
        }
    }

    private static async Task ExecuteAsync(
        BenchmarkOptions options,
        BenchmarkFixture fixture,
        BenchmarkResult result,
        CancellationToken cancellationToken)
    {
        var workspaceRoot = Path.Combine(options.RepoRoot, ".portHorizon", "e2e");
        if (Directory.Exists(workspaceRoot))
            throw new InvalidOperationException(
                $"Benchmark workspace already exists: {workspaceRoot}. Use a fresh unique --repo-root; benchmark mode never deletes prior state.");

        Directory.CreateDirectory(workspaceRoot);
        Console.WriteLine($"Benchmark {fixture.Id} ({options.Mode}): workspace={workspaceRoot}");

        var bare = Path.Combine(workspaceRoot, "remote.git");
        var clone = Path.Combine(workspaceRoot, "clone");
        Directory.CreateDirectory(bare);
        Directory.CreateDirectory(clone);
        Git.Run($"init -q --bare \"{bare}\"", workspaceRoot);
        Git.Run("init -q -b main", clone);
        Git.Run("config user.email benchmark@local", clone);
        Git.Run("config user.name forge-benchmark", clone);
        Git.Run($"remote add origin \"{bare}\"", clone);
        fixture.WriteScaffold(clone);
        Git.Run("add .", clone);
        Git.Run("commit -q -m scaffold", clone);
        Git.Run("push -q -u origin main", clone);
        var initialSha = Git.Capture("rev-parse HEAD", clone).Trim();

        var dbPath = Path.Combine(workspaceRoot, "state", "issues.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var issues = new IssueStore(dbPath);
        using var agentRuns = new AgentRunStore(issues.Db);
        var designArtifacts = new DesignArtifactStore(dbPath);
        var artOutputs = new ArtOutputStore(dbPath);
        var recoveryStore = new Orchestrator.MemoryExtractionStore(Path.Combine(workspaceRoot, "state", "extraction.db"));
        Orchestrator.IMemoryExtractor extractor = new Orchestrator.NoOpMemoryExtractor();
        var agentsStore = new AgentStore(issues);
        var sprints = new SprintStore(issues);
        var messageBus = new AgentMessageBus();
        var workspaceOptions = new WorkspaceOptions
        {
            Root = clone,
            WorktreeRoot = ".portHorizon/worktrees",
            DefaultBranch = "main",
        };
        var worktrees = new GitWorktreeService(workspaceOptions, NullLogger<GitWorktreeService>.Instance);
        var gitHub = new LocalGitHubService(bare, "local", "benchmark");
        var roleRegistry = new RoleAgentRegistry();
        var eventBus = new InMemoryDashboardEventBus();
        using var bridge = new PushBridge(bare, gitHub);

        var runner = CreateRunner(
            options, fixture, roleRegistry, result, workspaceRoot, issues, agentRuns);
        if (options.Mode == "live") ScrubModelCredentialsFromEnvironment();
        var dispatcher = new InProcessDispatcher(
            async (issue, _, ct) =>
            {
                var workflow = new Orchestrator.Workflow.EngineeringDispatchWorkflow(
                    issues, runner, worktrees, gitHub, roleRegistry, workspaceOptions,
                    eventBus, agent => messageBus.Drain(agent), designArtifacts, artOutputs,
                    extractor, recoveryStore,
                    NullLogger<Orchestrator.Workflow.EngineeringDispatchWorkflow>.Instance);
                await workflow.RunAsync(issue, ct);
            },
            NullLogger<InProcessDispatcher>.Instance);

        var projectStore = new ProjectStore(issues);
        await projectStore.UpsertAsync(new NewProject(
            "benchmark", "Benchmark", clone, "main"), cancellationToken);
        var bundleFactory = new ProjectDispatchBundleFactory(
            new AgentOptions { GitHub = new GitHubOptions() },
            Path.GetDirectoryName(dbPath)!, projectStore,
            new ProjectCloner(Path.GetDirectoryName(dbPath)!, NullLogger<ProjectCloner>.Instance),
            runner, roleRegistry, dispatcher, messageBus, eventBus, NullLoggerFactory.Instance);
        var bundle = new ProjectDispatchBundle(
            new ProjectOptions
            {
                Id = "benchmark", Name = "Benchmark", RepoUrl = "", DefaultBranch = "main", Root = clone,
            },
            issues, agentsStore, sprints, designArtifacts, artOutputs, worktrees, gitHub,
            new PRWatcher(gitHub, worktrees, issues, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30),
                eventBus, NullLogger<PRWatcher>.Instance),
            eventBus, NullLogger<ProjectDispatchBundle>.Instance);
        var orchestrator = new OrchestratorAgent(
            projectStore, bundleFactory, runner, roleRegistry, messageBus, dispatcher,
            eventBus, NullLogger<OrchestratorAgent>.Instance);

        var task = await issues.CreateAsync(new NewIssue(
            "task", fixture.Title, fixture.Prompt, Priority: 2), cancellationToken);
        await issues.TransitionAsync(task.Id, IssueStatus.Pending, error: null, ct: cancellationToken);
        var dispatchResult = await orchestrator.DispatchSingleTaskAsync(
            (await issues.GetAsync(task.Id, cancellationToken))!, bundle, cancellationToken);
        result.Checks.Add(new BenchmarkCheck("dispatch", dispatchResult.Success, dispatchResult.Message));

        var prs = gitHub.PrStore.AllPrs.ToList();
        result.Checks.Add(new BenchmarkCheck("pull request opened", prs.Count == 1,
            $"observed {prs.Count} local pull request(s)"));
        if (prs.Count != 1)
        {
            UpdateTaskResult(result, await issues.GetAsync(task.Id, cancellationToken));
            return;
        }

        var pr = prs[0];
        var prInfo = gitHub.PrStore.PrInfo[pr.Number];
        var headRef = Path.Combine(bare, "refs", "heads", prInfo.HeadBranch.Replace('/', Path.DirectorySeparatorChar));
        var headSha = File.ReadAllText(headRef).Trim();
        var worktree = worktrees.WorktreePathFor(task.Id);
        ValidateCommittedScope(worktree, bare, initialSha, headSha, fixture, result);

        if (result.Checks.Any(static c => !c.Passed))
        {
            UpdateTaskResult(result, await issues.GetAsync(task.Id, cancellationToken));
            return;
        }

        var graderRoot = Path.Combine(workspaceRoot, "trusted-grader");
        var graderReport = Path.Combine(workspaceRoot, "grader-report.json");
        fixture.WriteTrustedGrader(graderRoot, worktree, graderReport);
        var graderExit = await RunGraderAsync(graderRoot, workspaceRoot, cancellationToken);
        if (!File.Exists(graderReport))
        {
            result.Checks.Add(new BenchmarkCheck("trusted grader", false,
                $"grader exited {graderExit} without a report"));
        }
        else
        {
            var reportJson = await File.ReadAllTextAsync(graderReport, cancellationToken);
            var report = JsonSerializer.Deserialize(reportJson, BenchmarkJsonContext.Default.GraderReport)
                ?? throw new InvalidOperationException("Trusted grader returned an empty report.");
            result.Checks.AddRange(report.Checks);
            result.Checks.Add(new BenchmarkCheck("trusted grader process", graderExit == 0,
                $"grader exit code {graderExit}"));
        }

        if (result.Checks.Any(static c => !c.Passed))
        {
            UpdateTaskResult(result, await issues.GetAsync(task.Id, cancellationToken));
            return;
        }

        gitHub.PrStore.MarkCiGreen(headSha);
        var watcher = new PRWatcher(
            gitHub, worktrees, issues, TimeSpan.FromMilliseconds(50), TimeSpan.FromMinutes(5),
            eventBus, NullLogger<PRWatcher>.Instance);
        var watchedTask = (await issues.GetAsync(task.Id, cancellationToken))!;
        await watcher.ProcessWatchedTaskAsync(
            watchedTask, cancellationToken,
            reviewsOverride: _ => [Octokit.PullRequestReviewState.Approved],
            headShaOverride: _ => headSha,
            changedFilesOverride: p => CountChangedFiles(gitHub, p.Number));

        var finalTask = await issues.GetAsync(task.Id, cancellationToken);
        UpdateTaskResult(result, finalTask);
        var merged = gitHub.PrStore.WasMerged(pr.Number);
        result.Checks.Add(new BenchmarkCheck("simulated review closed loop",
            merged && finalTask?.Status == IssueStatus.Completed,
            $"merged={gitHub.PrStore.WasMerged(pr.Number)}, task={finalTask?.Status}"));

        if (merged)
        {
            var acceptedHeadAfterMerge = File.ReadAllText(headRef).Trim();
            result.Checks.Add(new BenchmarkCheck(
                "remote head stable through watch",
                acceptedHeadAfterMerge == headSha,
                $"before={headSha}, after={acceptedHeadAfterMerge}"));
            ValidateRemoteScope(
                bare, initialSha, acceptedHeadAfterMerge, fixture,
                "post-watch accepted head scope", result);
            var mergedTree = Path.Combine(workspaceRoot, "merged-tree");
            // LocalGitHubService records the merge decision but deliberately
            // does not rewrite the bare remote's base ref. Re-clone the exact
            // accepted remote head so this check grades committed bytes rather
            // than the agent's original worktree.
            Git.Run($"clone -q \"{bare}\" \"{mergedTree}\"", workspaceRoot);
            Git.Run($"checkout -q --detach {headSha}", mergedTree);
            var gradedHead = Git.Capture("rev-parse HEAD", mergedTree).Trim();
            result.Checks.Add(new BenchmarkCheck(
                "accepted remote head snapshot",
                gradedHead == headSha,
                $"expected={headSha}, graded={gradedHead}"));
            var mergedGraderRoot = Path.Combine(workspaceRoot, "trusted-grader-merged");
            var mergedReportPath = Path.Combine(workspaceRoot, "merged-grader-report.json");
            fixture.WriteTrustedGrader(mergedGraderRoot, mergedTree, mergedReportPath);
            var mergedExit = await RunGraderAsync(mergedGraderRoot, workspaceRoot, cancellationToken);
            var mergedPassed = false;
            if (File.Exists(mergedReportPath))
            {
                var mergedJson = await File.ReadAllTextAsync(mergedReportPath, cancellationToken);
                var mergedReport = JsonSerializer.Deserialize(mergedJson, BenchmarkJsonContext.Default.GraderReport);
                mergedPassed = mergedExit == 0 && mergedReport?.Checks.All(static c => c.Passed) == true;
            }
            result.Checks.Add(new BenchmarkCheck("accepted remote head acceptance", mergedPassed,
                $"trusted grader exit code {mergedExit} against accepted remote head {headSha}"));
        }
    }

    private static IAgentRunner CreateRunner(
        BenchmarkOptions options,
        BenchmarkFixture fixture,
        RoleAgentRegistry roleRegistry,
        BenchmarkResult result,
        string workspaceRoot,
        IssueStore issues,
        AgentRunStore agentRuns)
    {
        if (options.Mode == "fake")
            return new BenchmarkFakeAgentRunner(fixture);

        var apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY")
            ?? throw new InvalidOperationException("Live benchmark mode requires LLM_API_KEY.");
        var providerName = Environment.GetEnvironmentVariable("LLM_PROVIDER")
            ?? throw new InvalidOperationException("Live benchmark mode requires LLM_PROVIDER.");
        var baseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL")
            ?? throw new InvalidOperationException("Live benchmark mode requires LLM_BASE_URL.");
        var model = Environment.GetEnvironmentVariable("LLM_MODEL")
            ?? throw new InvalidOperationException("Live benchmark mode requires LLM_MODEL.");
        var llmOptions = new LlmOptions
        {
            DefaultProvider = providerName,
            Providers =
            [
                new LlmProviderOptions
                {
                    Name = providerName, BaseUrl = baseUrl, ApiKey = apiKey, DefaultModel = model,
                },
            ],
            Roles = new Dictionary<string, LlmRoleModelOptions>
            {
                ["CoreDev"] = new() { ProviderName = providerName, Model = model },
            },
        };
        var innerFactory = new BenchmarkNoRetryChatClientFactory();
        var meteringOptions = new BenchmarkMeteringOptions(
            AttemptId: Guid.NewGuid().ToString("N"),
            ScenarioId: fixture.Id,
            Provider: providerName,
            Model: model,
            MaxCalls: options.MaxCalls,
            MaxInputTokensPerCall: options.MaxInputTokens,
            MaxOutputTokensPerCall: options.MaxOutputTokens,
            InputUsdPerMillionTokens: options.InputUsdPerMillion,
            OutputUsdPerMillionTokens: options.OutputUsdPerMillion);
        var meteredFactory = new BenchmarkMeteringFactory(
            innerFactory,
            meteringOptions,
            Path.Combine(workspaceRoot, "state", "usage-ledger.json"),
            snapshot => result.Usage = BenchmarkResultUsage.From(snapshot));
        result.Meter = meteredFactory;
        result.Usage = BenchmarkResultUsage.From(meteredFactory.Snapshot);
        MafAgentRunner.DiagnosticLogPath = Path.Combine(
            workspaceRoot, "state", "logs", "agent.log");
        return new MafAgentRunner(meteredFactory, LlmConfigAdapter.FromOptions(llmOptions), roleRegistry,
            NullLogger<MafAgentRunner>.Instance, issues: issues, runs: agentRuns);
    }

    private static void ValidateCommittedScope(
        string worktree,
        string bare,
        string initialSha,
        string headSha,
        BenchmarkFixture fixture,
        BenchmarkResult result)
    {
        if (!Directory.Exists(worktree))
        {
            result.Checks.Add(new BenchmarkCheck("agent worktree", false, "worktree was not created"));
            return;
        }

        var worktreeHead = Git.Capture("rev-parse HEAD", worktree).Trim();
        result.Checks.Add(new BenchmarkCheck("pushed head identity", worktreeHead == headSha,
            $"worktree={worktreeHead}, remote={headSha}"));

        var commits = Git.Capture($"--git-dir=\"{bare}\" rev-list --count {initialSha}..{headSha}", bare).Trim();
        result.Checks.Add(new BenchmarkCheck("committed solution", int.TryParse(commits, out var count) && count > 0,
            $"remote commits ahead of immutable scaffold: {commits}"));

        var status = Git.Capture("status --porcelain --untracked-files=all", worktree).Trim();
        result.Checks.Add(new BenchmarkCheck("clean worktree", status.Length == 0,
            status.Length == 0 ? "no uncommitted changes" : status));

        ValidateRemoteScope(bare, initialSha, headSha, fixture, "allowed file scope", result);
    }

    private static void ValidateRemoteScope(
        string bare,
        string initialSha,
        string headSha,
        BenchmarkFixture fixture,
        string checkName,
        BenchmarkResult result)
    {
        var changed = Git.Capture(
                $"--git-dir=\"{bare}\" diff --name-only {initialSha}...{headSha}", bare)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var allowed = changed.Length == 1 && string.Equals(
            changed[0].Replace('\\', '/'), fixture.ImplementationPath, StringComparison.Ordinal);
        result.Checks.Add(new BenchmarkCheck(checkName, allowed,
            changed.Length == 0 ? "no committed files changed" : string.Join(", ", changed)));
    }

    private static async Task<int> RunGraderAsync(
        string graderRoot,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = graderRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(Path.Combine(graderRoot, "Grader.csproj"));
        start.ArgumentList.Add("--nologo");
        start.ArgumentList.Add("--verbosity");
        start.ArgumentList.Add("quiet");
        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(workspaceRoot, "dotnet-home");
        start.Environment["NUGET_PACKAGES"] = Path.Combine(workspaceRoot, "nuget-packages");
        ScrubModelCredentials(start.Environment);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start trusted grader process.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            Console.Error.WriteLine(SanitizeError($"Trusted grader failed. {output}\n{error}"));
        return process.ExitCode;
    }

    private static void UpdateTaskResult(BenchmarkResult result, IssueRecord? task)
    {
        if (task is null) return;
        result.TaskStatus = task.Status.ToString();
        result.ReworkAttempts = ParseInt(task.GetMetadata("reworkAttempts"))
            ?? ParseInt(task.GetMetadata("reworkRound")) ?? 0;
        var planGate = task.GetMetadata("planGate");
        if (string.IsNullOrWhiteSpace(planGate)) return; // Fake runs do not evaluate a model plan.
        using var audit = JsonDocument.Parse(planGate);
        if (audit.RootElement.TryGetProperty("failed", out var failed)
            && failed.ValueKind is JsonValueKind.True or JsonValueKind.False)
            result.GateFailed = failed.GetBoolean();
    }

    private static int? ParseInt(string? value) => int.TryParse(value, out var parsed) ? parsed : null;

    private static int CountChangedFiles(LocalGitHubService gitHub, int prNumber)
    {
        var info = gitHub.PrStore.PrInfo[prNumber];
        var output = Git.Capture(
            $"--git-dir=\"{gitHub.LocalRemotePath}\" diff --name-only {info.BaseBranch}...{info.HeadBranch}",
            gitHub.LocalRemotePath);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    private static BenchmarkOptions ParseOptions(string[] args)
    {
        var repoRoot = ReadOption(args, "--repo-root")
            ?? throw new ArgumentException("Benchmark mode requires a fresh unique --repo-root=<absolute path>.");
        if (!Path.IsPathFullyQualified(repoRoot))
            throw new ArgumentException("--repo-root must be an absolute path in benchmark mode.");
        var caseId = ReadOption(args, "--benchmark-case")
            ?? throw new ArgumentException("Benchmark mode requires --benchmark-case=<calculator|normalize|invoice>.");
        var mode = ReadOption(args, "--benchmark-mode") ?? "fake";
        if (mode is not ("fake" or "live"))
            throw new ArgumentException("--benchmark-mode must be fake or live.");
        if (mode == "live" && !args.Contains("--real-llm", StringComparer.Ordinal))
            throw new ArgumentException("--benchmark-mode=live also requires --real-llm.");
        var timeoutText = ReadOption(args, "--benchmark-timeout-seconds") ?? "300";
        if (!int.TryParse(timeoutText, out var timeoutSeconds) || timeoutSeconds is < 1 or > 3600)
            throw new ArgumentException("--benchmark-timeout-seconds must be between 1 and 3600.");
        var maxCalls = ParsePositiveOption(args, "--benchmark-max-calls", 12);
        var maxInputTokens = ParsePositiveOption(args, "--benchmark-max-input-tokens", 100_000);
        var maxOutputTokens = ParsePositiveOption(args, "--benchmark-max-output-tokens", 8_000);
        var inputRate = ParseRateOption(args, "--benchmark-input-usd-per-million", mode == "live");
        var outputRate = ParseRateOption(args, "--benchmark-output-usd-per-million", mode == "live");
        return new BenchmarkOptions(
            Path.GetFullPath(repoRoot), caseId, mode, timeoutSeconds,
            maxCalls, maxInputTokens, maxOutputTokens, inputRate, outputRate);
    }

    private static int ParsePositiveOption(string[] args, string name, int fallback)
    {
        var text = ReadOption(args, name);
        if (text is null) return fallback;
        if (!int.TryParse(text, out var parsed) || parsed <= 0)
            throw new ArgumentException($"{name} must be a positive integer.");
        return parsed;
    }

    private static decimal ParseRateOption(string[] args, string name, bool required)
    {
        var text = ReadOption(args, name);
        if (text is null)
        {
            if (required) throw new ArgumentException($"Live benchmark mode requires {name}.");
            return 0m;
        }
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0)
            throw new ArgumentException($"{name} must be a non-negative decimal.");
        return parsed;
    }

    private static string? ReadOption(string[] args, string name) => args
        .FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal))?
        .Split('=', 2)[1];

    private static void ScrubModelCredentialsFromEnvironment()
    {
        foreach (var name in ModelCredentialEnvironmentNames)
            Environment.SetEnvironmentVariable(name, null);
    }

    private static void ScrubModelCredentials(IDictionary<string, string?> environment)
    {
        foreach (var name in ModelCredentialEnvironmentNames)
            environment.Remove(name);
    }

    private static readonly string[] ModelCredentialEnvironmentNames =
    [
        "LLM_API_KEY",
        "OPENAI_API_KEY",
        "ANTHROPIC_API_KEY",
        "AZURE_OPENAI_API_KEY",
    ];

    private static string SanitizeError(string message, string? sensitiveValue = null)
    {
        if (message.Contains("response", StringComparison.OrdinalIgnoreCase)
            || message.Contains("http", StringComparison.OrdinalIgnoreCase))
            return "Provider request failed; response details were omitted.";
        var sanitized = message.Replace('\r', ' ').Replace('\n', ' ');
        var apiKey = sensitiveValue ?? Environment.GetEnvironmentVariable("LLM_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
            sanitized = sanitized.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        return sanitized.Length <= 300 ? sanitized : sanitized[..300];
    }

    private sealed record BenchmarkOptions(
        string RepoRoot,
        string CaseId,
        string Mode,
        int TimeoutSeconds,
        int MaxCalls,
        int MaxInputTokens,
        int MaxOutputTokens,
        decimal InputUsdPerMillion,
        decimal OutputUsdPerMillion);
}

internal sealed class BenchmarkFakeAgentRunner(BenchmarkFixture fixture) : IAgentRunner
{
    public Task<AgentRunResult> RunAsync(
        AgentType role,
        string prompt,
        string? sessionId = null,
        IReadOnlyDictionary<string, object>? context = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var worktreePath = context is not null
            && context.TryGetValue("worktreePath", out var value)
            && value is string path
                ? path
                : throw new InvalidOperationException("worktreePath missing from benchmark agent context.");
        File.WriteAllText(Path.Combine(worktreePath, fixture.ImplementationPath), fixture.FakeSolution);
        return Task.FromResult(new AgentRunResult(
            fixture.Prompt, "benchmark-fake-session", 0, 0, TimeSpan.FromMilliseconds(1)));
    }
}
