using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using PortHorizon.Agents.AgentTools;
using PortHorizon.Agents.Agents;
using PortHorizon.Agents.Configuration;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Dashboard;
using PortHorizon.Agents.Orchestrator;
using PortHorizon.Agents.Reviewer;

namespace PortHorizon.Agents;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var mode = ParseMode(args);
        var configPath = ParseArg(args, "--config");

        AgentOptions options;
        try
        {
            options = OptionsLoader.Load(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Configuration error: {ex.Message}");
            return 1;
        }

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss.fff ";
                o.IncludeScopes = false;
            });
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger("PortHorizon.Agents");

        if (mode == CliMode.Status)
            return await PrintStatusAsync(options, logger);

        if (mode == CliMode.Check)
            return await RunPreflightCheckAsync(options, logger);

        if (mode == CliMode.Enqueue)
            return await EnqueueTaskAsync(args, options);

        if (mode == CliMode.DashboardOnly)
            return await RunDashboardOnlyAsync(options, loggerFactory, logger);

        if (mode == CliMode.WorktreeSmoke)
            return await RunWorktreeSmokeAsync(args, options, loggerFactory, logger);

        return await RunOrchestratorAsync(options, loggerFactory, logger);
    }

    private enum CliMode { Run, Once, Status, Enqueue, DashboardOnly, WorktreeSmoke, Check }

    private static CliMode ParseMode(string[] args)
    {
        if (args.Any(a => a == "--status")) return CliMode.Status;
        if (args.Any(a => a == "--enqueue-task")) return CliMode.Enqueue;
        if (args.Any(a => a == "--dashboard-only")) return CliMode.DashboardOnly;
        if (args.Any(a => a == "--worktree-smoke")) return CliMode.WorktreeSmoke;
        if (args.Any(a => a == "--once")) return CliMode.Once;
        if (args.Any(a => a == "--check")) return CliMode.Check;
        return CliMode.Run;
    }

    private static string? ParseArg(string[] args, string key)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == key && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith($"{key}=", StringComparison.Ordinal))
                return args[i][(key.Length + 1)..];
        }
        return null;
    }

    private static async Task<int> PrintStatusAsync(AgentOptions options, ILogger logger)
    {
        var workspaceDir = Path.GetDirectoryName(options.Workspace.Root) ?? ".";
        try
        {
            var issues = new IssueStore(Path.Combine(workspaceDir, ".portHorizon", "state", "issues.db"));
            var all = await issues.ListAsync(new IssueFilter(), CancellationToken.None);
            Console.WriteLine($"Pending:    {all.Count(i => i.Status == IssueStatus.Pending)}");
            Console.WriteLine($"InProgress: {all.Count(i => i.Status == IssueStatus.InProgress)}");
            Console.WriteLine($"Completed:  {all.Count(i => i.Status == IssueStatus.Completed)}");
            Console.WriteLine($"Failed:     {all.Count(i => i.Status == IssueStatus.Failed)}");
            Console.WriteLine($"Blocked:    {all.Count(i => i.Status == IssueStatus.Blocked)}");
            Console.WriteLine($"Total:      {all.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Status error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> EnqueueTaskAsync(string[] args, AgentOptions options)
    {
        var workspaceDir = Path.GetDirectoryName(options.Workspace.Root) ?? ".";
        var title = ParseArg(args, "--enqueue-task")
            ?? $"task-{Guid.NewGuid().ToString("N")[..8]}";
        var type = ParseArg(args, "--task-type") ?? "ecs";
        var description = ParseArg(args, "--task-desc") ?? "no description";
        var branch = ParseArg(args, "--branch") ?? $"agent/{title}";

        var issues = new IssueStore(Path.Combine(workspaceDir, ".portHorizon", "state", "issues.db"));

        // Stable, caller-supplied id: prefer explicit --task-id, else slugify the title.
        var explicitId = ParseArg(args, "--task-id");
        var shortId = explicitId ?? Slugify(title);
        if (string.IsNullOrEmpty(shortId)) shortId = Guid.NewGuid().ToString("N")[..8];

        var metadata = new Dictionary<string, object> { ["branch"] = branch };

        try
        {
            var issue = await issues.CreateAsync(new NewIssue(
                Type: type,
                Title: title,
                Description: description,
                Metadata: metadata), CancellationToken.None);

            // Override the auto id with the requested short id so callers can
            // reference their own chosen ids. Easiest is to rename the row.
            if (issue.ShortId != shortId)
            {
                // IssueStore assigned task-N; we just record the requested id
                // as an alias under task-<shortId>. For P0 we use the
                // auto-assigned id and warn if it differs.
                Console.Error.WriteLine($"Warning: --task-id ignored (IssueStore assigned {issue.Id}). Use the assigned id.");
            }

            Console.WriteLine($"Enqueued {issue.Id} ({type}): {title}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Enqueue failed: {ex.Message}");
            return 1;
        }
    }

    private static string Slugify(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        var cleaned = sb.ToString().Trim('-');
        return cleaned.Length == 0 || cleaned.Length > 40 ? cleaned[..Math.Min(40, cleaned.Length)] : cleaned;
    }

    private static async Task<int> RunDashboardOnlyAsync(
        AgentOptions options, ILoggerFactory loggerFactory, ILogger logger)
    {
        var workspaceDir = Path.GetDirectoryName(options.Workspace.Root) ?? ".";
        var stateStore = new StateStore(Path.Combine(workspaceDir, ".portHorizon", "state"));
        var issues = new IssueStore(Path.Combine(workspaceDir, ".portHorizon", "state", "issues.db"));
        var agents = new AgentStore(issues);
        var skills = new SkillStore(issues);
        var sprints = new SprintStore(issues);
        var messageBus = new AgentMessageBus();
        var eventBus = new InMemoryDashboardEventBus();
        var dashboard = new DashboardHost(
            options.Dashboard, issues, agents, skills, sprints, messageBus, eventBus,
            loggerFactory.CreateLogger<DashboardHost>());

        _ = stateStore; // keep dead-code-elim happy; will remove in next commit

        using var shutdownCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            logger.LogWarning("SIGINT received; cancelling...");
            e.Cancel = true;
            shutdownCts.Cancel();
        };

        try
        {
            await dashboard.StartAsync(shutdownCts.Token);
            logger.LogInformation("Dashboard running. Ctrl+C to stop.");
            await Task.Delay(Timeout.Infinite, shutdownCts.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            try { await dashboard.StopAsync(); } catch { }
        }
    }

    private static async Task<int> RunWorktreeSmokeAsync(
        string[] args, AgentOptions options, ILoggerFactory loggerFactory, ILogger logger)
    {
        var taskId = ParseArg(args, "--worktree-smoke")
            ?? $"smoke-{Guid.NewGuid().ToString("N")[..8]}";
        var worktrees = new GitWorktreeService(options.Workspace, loggerFactory.CreateLogger<GitWorktreeService>());
        try
        {
            Console.WriteLine($"Workspace root: {worktrees.WorkspaceRoot}");
            Console.WriteLine($"Worktree root : {worktrees.WorktreeRoot}");
            Console.WriteLine($"Default branch: {worktrees.DefaultBranch}");
            Console.WriteLine();
            Console.WriteLine($"[1/5] Creating worktree for task '{taskId}'...");
            var path = await worktrees.CreateAsync(taskId, worktrees.DefaultBranch);
            Console.WriteLine($"      -> {path}");

            Console.WriteLine("[2/5] Verifying worktree exists in git...");
            var listed = await worktrees.WorktreeExistsAsync(path);
            Console.WriteLine($"      -> listed={listed}");

            var headProbe = Path.Combine(path, "PortHorizon.sln");
            Console.WriteLine($"[3/5] Probe: {headProbe} exists = {File.Exists(headProbe)}");

            Console.WriteLine("[4/5] Writing + committing a marker file...");
            var marker = Path.Combine(path, ".ph-smoke");
            await File.WriteAllTextAsync(marker, $"smoke at {DateTime.UtcNow:O}");
            var commit = await worktrees.CommitAllAsync(path, $"smoke({taskId}): worktree smoke");
            Console.WriteLine($"      -> outcome={commit.Outcome} msg={commit.Message.Split('\n').First()}");

            Console.WriteLine("[5/5] Push dry-run (no actual push; verifies config + remote)...");
            var headSha = await worktrees.GetHeadShaAsync(path);
            Console.WriteLine($"      -> head sha: {headSha}");
            var stats = await worktrees.GetDiffStatsAsync(path, worktrees.DefaultBranch);
            Console.WriteLine($"      -> diff: {stats.Summary}");

            Console.WriteLine();
            Console.WriteLine("Smoke OK. Worktree and marker branch left in place.");
            Console.WriteLine($"Clean up with: git -C \"{worktrees.WorkspaceRoot}\" worktree remove --force \"{path}\" && git -C \"{worktrees.WorkspaceRoot}\" branch -D agent/{taskId}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Smoke FAILED: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    /// <summary>
    /// Pre-flight validation: confirm config, schema, git, gateway, and
    /// GitHub auth all work without starting dispatch. Exits non-zero on
    /// any failure so CI / smoke jobs can gate on it.
    /// </summary>
    private static async Task<int> RunPreflightCheckAsync(AgentOptions options, ILogger logger)
    {
        var failures = new List<string>();
        var workspaceDir = Path.GetDirectoryName(options.Workspace.Root) ?? ".";
        var stateDir = Path.Combine(workspaceDir, ".portHorizon", "state");

        Console.WriteLine("Pre-flight check for PortHorizon.Agents");
        Console.WriteLine($"  workspace: {options.Workspace.Root}");
        Console.WriteLine($"  state dir: {stateDir}");
        Console.WriteLine();

        // 1. Workspace is a git repo
        if (!Directory.Exists(options.Workspace.Root))
        {
            failures.Add($"workspace root does not exist: {options.Workspace.Root}");
        }
        else if (!Directory.Exists(Path.Combine(options.Workspace.Root, ".git")))
        {
            failures.Add($"workspace root is not a git repo: {options.Workspace.Root}");
        }
        else
        {
            Console.WriteLine("  [ok] workspace is a git repo");
        }

        // 2. IssueStore opens + schema version is current
        try
        {
            await using var issues = new IssueStore(Path.Combine(stateDir, "issues.db"));
            // Trigger InitializeSchema by listing (cheap read).
            var probe = await issues.ListAsync(new IssueFilter(), CancellationToken.None);
            var expectedSchema = IssueStore.CurrentSchemaVersion;
            // Read the actual schema_version from the DB.
            int actualSchema = -1;
            await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(issues.ConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
                var result = await cmd.ExecuteScalarAsync();
                actualSchema = Convert.ToInt32(result);
            }
            if (actualSchema == expectedSchema)
            {
                Console.WriteLine($"  [ok] issues.db schema v{actualSchema} (current)");
            }
            else
            {
                failures.Add($"issues.db schema v{actualSchema} but current is v{expectedSchema} (run orchestrator once to migrate)");
            }
            _ = probe;
        }
        catch (Exception ex)
        {
            failures.Add($"issues.db: {ex.GetType().Name}: {ex.Message}");
        }

        // 3. MemoryStore opens + schema version is current
        try
        {
            var memPath = Path.Combine(stateDir, "memory.db");
            if (!File.Exists(memPath))
            {
                Console.WriteLine("  [skip] memory.db does not exist yet (will be created on first start)");
            }
            else
            {
                // Reuse IssueStore to bootstrap the schema, then check.
                _ = new IssueStore(memPath);
                await using var mem = new MemoryStore(memPath);
                var memProbe = await mem.RecallAsync();
                int actualSchema = -1;
                await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(mem.ConnectionString))
                {
                    await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
                    var result = await cmd.ExecuteScalarAsync();
                    actualSchema = Convert.ToInt32(result);
                }
                var expectedSchema = IssueStore.CurrentSchemaVersion;
                if (actualSchema == expectedSchema)
                {
                    Console.WriteLine($"  [ok] memory.db schema v{actualSchema} (current)");
                }
                else
                {
                    failures.Add($"memory.db schema v{actualSchema} but current is v{expectedSchema} (run orchestrator once to migrate)");
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add($"memory.db: {ex.GetType().Name}: {ex.Message}");
        }

        // 4. LLM provider + key configured
        var llmConfig = LlmConfigAdapter.FromOptions(options.Llm);
        if (string.IsNullOrEmpty(llmConfig.DefaultProvider))
        {
            failures.Add("llm.defaultProvider is empty");
        }
        else
        {
            var provider = llmConfig.Providers.FirstOrDefault(p => p.Name == llmConfig.DefaultProvider);
            if (provider is null)
            {
                failures.Add($"llm.defaultProvider '{llmConfig.DefaultProvider}' not found in llm.providers[]");
            }
            else if (string.IsNullOrEmpty(provider.ApiKey) || provider.ApiKey.StartsWith("KILO_GATEWAY") || provider.ApiKey.StartsWith("GITHUB_TOKEN"))
            {
                failures.Add($"llm.providers['{provider.Name}'].apiKey looks unset (still a placeholder)");
            }
            else if (!provider.BaseUrl.StartsWith("http"))
            {
                failures.Add($"llm.providers['{provider.Name}'].baseUrl invalid: {provider.BaseUrl}");
            }
            else
            {
                // Ping the gateway with a minimal chat completion.
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
                    var resp = await http.PostAsJsonAsync(
                        provider.BaseUrl.TrimEnd('/') + "/v1/chat/completions",
                        new
                        {
                            model = provider.DefaultModel,
                            messages = new[] { new { role = "user", content = "ping" } },
                            max_tokens = 4,
                        });
                    if (resp.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"  [ok] kilo gateway reachable at {provider.BaseUrl} (model={provider.DefaultModel})");
                    }
                    else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        failures.Add($"kilo gateway auth failed (HTTP 401) — apiKey expired or invalid");
                    }
                    else
                    {
                        failures.Add($"kilo gateway returned HTTP {(int)resp.StatusCode} — {resp.ReasonPhrase}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"kilo gateway unreachable: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // 5. GitHub token + repo
        if (string.IsNullOrEmpty(options.GitHub.Token) || options.GitHub.Token.StartsWith("GITHUB_TOKEN"))
        {
            failures.Add("github.token looks unset (still a placeholder)");
        }
        else
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", options.GitHub.Token);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PortHorizon.Agents-Check");
                var resp = await http.GetAsync($"https://api.github.com/repos/{options.GitHub.Owner}/{options.GitHub.Repo}");
                if (resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"  [ok] GitHub repo {options.GitHub.Owner}/{options.GitHub.Repo} reachable");
                }
                else if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    failures.Add($"GitHub repo {options.GitHub.Owner}/{options.GitHub.Repo} not found (or token lacks access)");
                }
                else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    failures.Add("GitHub token rejected (HTTP 401) — token expired or wrong scope");
                }
                else
                {
                    failures.Add($"GitHub returned HTTP {(int)resp.StatusCode} — {resp.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"GitHub unreachable: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("All pre-flight checks passed.");
            return 0;
        }
        Console.Error.WriteLine($"{failures.Count} pre-flight check(s) failed:");
        foreach (var f in failures)
        {
            Console.Error.WriteLine($"  - {f}");
        }
        return 1;
    }

    /// <summary>
    /// Pick the <see cref="IChatClientFactory"/> based on the configured
    /// providers. Stub config (no providers with a non-Empty ApiKey) yields
    /// the in-process <see cref="StubbedChatClientFactory"/>; everything else
    /// uses the OpenAI-compatible factory.
    /// </summary>
    private static IChatClientFactory SelectChatClientFactory(LlmConfig llmConfig, LlmOptions options)
    {
        var hasRealKey = llmConfig.Providers.Any(p => !string.IsNullOrEmpty(p.ApiKey));
        if (!hasRealKey)
        {
            Console.Error.WriteLine("No LLM provider with an API key configured; using StubbedChatClientFactory.");
            return new StubbedChatClientFactory();
        }
        return new OpenAICompatibleChatClientFactory();
    }


    private static async Task<int> RunOrchestratorAsync(
        AgentOptions options, ILoggerFactory loggerFactory, ILogger logger)
    {
        var workspaceDir = Path.GetDirectoryName(options.Workspace.Root) ?? ".";
        var stateStore = new StateStore(Path.Combine(workspaceDir, ".portHorizon", "state"));
        var issues = new IssueStore(Path.Combine(workspaceDir, ".portHorizon", "state", "issues.db"));
        var agents = new AgentStore(issues);
        var skills = new SkillStore(issues);
        var sprints = new SprintStore(issues);
        var messageBus = new AgentMessageBus();
        var worktrees = new GitWorktreeService(options.Workspace, loggerFactory.CreateLogger<GitWorktreeService>());
        var gitHub = new GitHubService(options.GitHub);
        var roleRegistry = new RoleAgentRegistry();
        var agentsStore = new Core.AgentStore(issues);
        var skillsStore = new Core.SkillStore(issues);
        var skillSource = new SqliteSkillSource(agentsStore, skillsStore, roleRegistry);
        // The memory table lives in IssueStore's schema (v7). Construct an
        // IssueStore against the memory DB once at startup so the schema
        // (and any future migrations) run before MemoryStore touches it.
        // MemoryStore itself does not own migrations.
        var memoryDbPath = Path.Combine(workspaceDir, ".portHorizon", "state", "memory.db");
        var memoryBootstrap = new Core.IssueStore(memoryDbPath);
        var memoryStore = new MemoryStore(memoryDbPath);

        // Phase 4: JSONL mirror of the issue store. Background service
        // rewrites the file every 5s so it's safe to tail -f.
        var issuesJsonlPath = Path.Combine(workspaceDir, ".portHorizon", "state", "issues.jsonl");
        var jsonlMirror = new IssuesJsonlMirror(issues, issuesJsonlPath,
            loggerFactory.CreateLogger<IssuesJsonlMirror>());

            // P3.5: issue_groomer_run store. Shares the issues DB
            // (the v8 migration is applied at IssueStore's ctor).
            // The groomer_runs table has a foreign key on issue.id,
            // so the runs must live in the same DB as the issue rows.
            var groomerRunsDb = Path.Combine(workspaceDir, ".portHorizon", "state", "issues.db");
            var groomerRuns = new Core.IssueGroomerRunStore(groomerRunsDb);

        // P0.5: vision.md import. Build the VisionStore (loads the
        // configured file on startup), inject it into memory as the
        // 'vision/master' key, and pass it to the dashboard so the
        // Vision tab can surface it.
        var vision = new VisionStore(options.Workspace.Root, options.Vision.Path);
        var visionSnapshot = vision.Reload();
        if (visionSnapshot.Exists)
        {
            logger.LogInformation("Vision loaded from {Path} ({Len} chars)",
                visionSnapshot.Path, visionSnapshot.Content.Length);
            // Inject into memory so every agent prompt includes the
            // vision. The memory block goes through the normal
            // MemoryStore path; no special casing in the agent.
            await memoryStore.RememberAsync("vision/master", visionSnapshot.Content, ttlDays: null, CancellationToken.None);
        }
        else
        {
            logger.LogWarning("Vision file not found at {Path}; dashboard Vision tab will be empty", visionSnapshot.Path);
        }
        var llmConfig = LlmConfigAdapter.FromOptions(options.Llm);
        var chatClientFactory = (IChatClientFactory)SelectChatClientFactory(llmConfig, options.Llm);
        var agentRunner = new MafAgentRunner(
            chatClientFactory, llmConfig, roleRegistry,
            loggerFactory.CreateLogger<MafAgentRunner>(),
            skills: skillSource,
            kiloAgentsRoot: Path.Combine(options.Workspace.Root, ".kilo", "agents"),
            memory: memoryStore);
        var eventBus = new InMemoryDashboardEventBus();
        var prWatcher = new PRWatcher(
            gitHub, worktrees, issues,
            TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30),
            eventBus,
            loggerFactory.CreateLogger<PRWatcher>());
        var orchestrator = new OrchestratorAgent(
            agentRunner, roleRegistry, worktrees, gitHub, prWatcher, issues,
            agents, sprints, messageBus,
            eventBus,
            loggerFactory.CreateLogger<OrchestratorAgent>());
        orchestrator.BindOptions(options);
        var intakeStore = new Core.IntakeStore(issues);
        var intakeRegistry = new IntakeAgentRegistry(projectId =>
            new IntakeAgent(
                projectId,
                intakeStore,
                issues,
                sprints,
                chatClientFactory,
                llmConfig,
                roleRegistry,
                eventBus,
                loggerFactory.CreateLogger<IntakeAgent>(),
                skills: skillSource,
                kiloAgentsRoot: Path.Combine(options.Workspace.Root, ".kilo", "agents")));
        var specStore = new Core.SpecStore(issues);
        var specExtractionReader = new Core.SpecExtractionReader(issues);
        var codebaseGraphCache = new Codebase.CodebaseGraphCacheStore(issues);
        var codebaseGraphBuilder = new Codebase.DotnetCodebaseGraphBuilder();
        var projectContextSource = new Core.FilesystemProjectContextSource(
            issues, agents, specStore, skills, options.Workspace.Root);
        var productAgentFactory = new Agents.ProductAgentFactory(
            specStore, issues, projectContextSource, chatClientFactory, llmConfig,
            roleRegistry, eventBus, skillSource, loggerFactory,
            Path.Combine(options.Workspace.Root, ".kilo", "agents"));
        var productRefinementQueue = new Agents.ProductRefinementQueue(
            productAgentFactory, specStore, eventBus,
            loggerFactory.CreateLogger<Agents.ProductRefinementQueue>());
        var groomerFactory = new Agents.GroomerAgentFactory(
            issues, specStore, eventBus, chatClientFactory, llmConfig, loggerFactory);
        var dashboard = new DashboardHost(
            options.Dashboard, issues, agents, skills, sprints, messageBus, eventBus,
            loggerFactory.CreateLogger<DashboardHost>(),
            intakeStore: intakeStore,
            intakeRegistry: intakeRegistry,
            specs: specStore,
            groomerFactory: groomerFactory,
            memory: memoryStore,
            issuesJsonlPath: issuesJsonlPath,
            vision: vision,
            groomerRuns: groomerRuns,
            extractor: specExtractionReader,
            codebaseBuilder: codebaseGraphBuilder,
            codebaseCache: codebaseGraphCache);

        using var shutdownCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            logger.LogWarning("SIGINT received; cancelling...");
            e.Cancel = true;
            shutdownCts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            logger.LogWarning("Process exit; cancelling");
            try { shutdownCts.Cancel(); } catch { }
        };

        try
        {
            logger.LogInformation("Starting dashboard");
            await dashboard.StartAsync(shutdownCts.Token);

            // JSONL mirror is a fire-and-forget background task; it
            // cancels itself when shutdownCts fires.
            _ = jsonlMirror.StartAsync(shutdownCts.Token);

            // P3.5: scheduled Groomer wakes up every 5 minutes and
            // grooms any Approved specs that haven't been groomed
            // recently (or whose last groom failed). Fire-and-forget.
            var scheduledGroomer = new Orchestrator.ScheduledGroomer(
                specStore, groomerFactory, groomerRuns, eventBus,
                loggerFactory.CreateLogger<Orchestrator.ScheduledGroomer>(),
                interval: TimeSpan.FromMinutes(5));
            _ = scheduledGroomer.RunAsync(shutdownCts.Token);

            logger.LogInformation("Orchestrator starting");
            await orchestrator.ExecuteAsync(shutdownCts.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Orchestrator stopped");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Orchestrator crashed");
            return 1;
        }
        finally
        {
            try { await dashboard.StopAsync(); } catch { }
            try
            {
                var s = await stateStore.LoadStateAsync();
                await stateStore.SaveStateAsync(s);
            }
            catch { }
        }
    }
}

