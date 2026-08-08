using Forge.Agents;
using Forge.AgentTools;
using Forge.Codebase;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Db;
using Forge.Core.Messaging;
using Forge.Dashboard;
using Forge.Messaging;
using Forge.Orchestrator.Slots;
using Forge.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Talaria.Core.Abstractions;

namespace Forge.Orchestrator.Composition;

/// <summary>Thrown by <see cref="ForgeComposition.BuildAsync"/> when the project registry is empty.</summary>
public sealed class NoProjectsRegisteredException : Exception
{
    public NoProjectsRegisteredException()
        : base("No projects registered.") { }
}

/// <summary>The per-project bootstrap result: finalised project list, DB path map, data root, and the registry-level stores.</summary>
public sealed record ForgeProjectBootstrap(
    IReadOnlyList<ProjectOptions> Projects,
    IReadOnlyDictionary<string, string> IssuesDbByProject,
    string DataRoot,
    ProjectStore ProjectStore,
    ProjectCloner Cloner,
    SecretStore SecretStore);

/// <summary>
/// Composition root for the orchestrator runtime. Everything
/// RunOrchestratorAsync used to hand-wire is registered here: stores as
/// singletons (constructed in their historical boot order — several
/// ctors run schema migrations and must stay ordered), factories,
/// schedulers, OrchestratorAgent, PRWatcher-side services, and the
/// messaging transport/publisher. DashboardHost is factory-registered
/// so it resolves the SHARED transport + publisher instances from this
/// container (its own WebApplication container gets them passed
/// through). Consumers of the graph resolve from the returned provider.
/// </summary>
public static class ForgeComposition
{
    public static async Task<ServiceProvider> BuildAsync(
        AgentOptions options, ILoggerFactory loggerFactory, CancellationToken ct = default)
    {
        var (knownProjects, orchDbByProject, orchDataRoot, projectStore, cloner, secretStore) =
            Program.BuildProjectBootstrap(options, loggerFactory);
        if (knownProjects.Count == 0)
            throw new NoProjectsRegisteredException();
        var primary = knownProjects[0];
        var log = loggerFactory.CreateLogger("Forge");

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        var boot = new ForgeProjectBootstrap(knownProjects, orchDbByProject, orchDataRoot, projectStore, cloner, secretStore);
        services.AddSingleton(boot);
        services.AddSingleton(projectStore);
        services.AddSingleton<IProjectStore>(projectStore);
        services.AddSingleton(cloner);
        services.AddSingleton(secretStore);
        services.AddSingleton<ISecretStore>(secretStore);

        // Messaging: one transport + one publisher for the process,
        // created eagerly so EVERY store built below (primary and
        // per-project via the factories) publishes through them.
        // DashboardHost's factory registration resolves these same
        // instances (shared in-memory channels).
        var transport = ForgeMessagingExtensions.CreateTransport(options.Messaging.Transport);
        services.AddSingleton(transport);
        var eventPublisher = new TalariaEventPublisher(
            transport, loggerFactory.CreateLogger<TalariaEventPublisher>());
        services.AddSingleton<IEventPublisher>(eventPublisher);
        services.AddSingleton(sp => new SweepTickPublisher(
            sp.GetRequiredService<IEventPublisher>(),
            async c => (await projectStore.ListAsync(c)).Select(p => p.Id).ToArray(),
            sp.GetRequiredService<ILogger<SweepTickPublisher>>()));

        // Dispatch-loop wakeup: message consumers signal the loop on
        // enqueue/transition events (run-finished signals internally).
        // Event-driven loop wakeups: one signal per loop, shared with
        // the message consumers (competing-consumer transport — ONE
        // consumer per topic fans out to every interested loop).
        var wakeups = Consumers.SchedulerWakeups.Create();
        services.AddSingleton(wakeups);
        services.AddSingleton(sp => new Consumers.TaskEnqueuedConsumer(
            sp.GetRequiredService<ITransport>(), wakeups,
            sp.GetRequiredService<ILogger<Consumers.TaskEnqueuedConsumer>>()));
        var projectFactory = new ProjectContextFactory(projectStore, orchDataRoot, orchDbByProject,
            (pid, path) => Program.FactoryFor(options.Db, pid, path),
            events: eventPublisher);
        services.AddSingleton(projectFactory);
        string? ProjectRootLookup(string projectId) =>
            projectFactory.KnownProjects
                .FirstOrDefault(p => string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase))
                ?.Root;
        IReadOnlyDictionary<string, Core.RoleTerritory>? ProjectTerritoryLookup(string projectId) =>
            projectFactory.KnownProjects
                .FirstOrDefault(p => string.Equals(p.Id, projectId, StringComparison.OrdinalIgnoreCase))
                ?.Territories;

        var primaryDb = orchDbByProject[primary.Id];
        var primaryStateDir = Path.GetDirectoryName(primaryDb)!;
        var stateStore = new StateStore(primaryStateDir);
        services.AddSingleton(stateStore);
        var primaryFactory = Program.FactoryFor(options.Db, primary.Id, primaryDb);
        services.AddSingleton(primaryFactory);
        var issues = new IssueStore(primaryFactory, primary.Id, eventPublisher);
        services.AddSingleton(issues);
        services.AddSingleton<IIssueStore>(issues);
        var registryIssues = new IssueStore(
            ForgeDb.ForRegistry(options.Db.IsSqlServer, options.Db.ConnectionString, primaryDb));
        var agents = new AgentStore(registryIssues);
        services.AddSingleton(agents);
        services.AddSingleton<IAgentStore>(agents);
        var skills = new SkillStore(registryIssues);
        services.AddSingleton(skills);
        services.AddSingleton<ISkillStore>(skills);
        var sprints = new SprintStore(issues);
        services.AddSingleton(sprints);
        services.AddSingleton<ISprintStore>(sprints);
        var messageBus = new AgentMessageBus();
        services.AddSingleton(messageBus);
        var worktrees = new GitWorktreeService(
            new WorkspaceOptions
            {
                Root = primary.Root,
                WorktreeRoot = options.Workspace.WorktreeRoot,
                DefaultBranch = options.Workspace.DefaultBranch,
            },
            loggerFactory.CreateLogger<GitWorktreeService>(),
            githubToken: options.GitHub?.Token);
        services.AddSingleton(worktrees);
        var gitHub = Program.BuildGitHubService(
            options.GitHub ?? new GitHubOptions(), loggerFactory.CreateLogger<GitHubService>());
        services.AddSingleton(gitHub);
        var roleRegistry = new RoleAgentRegistry();
        services.AddSingleton(roleRegistry);
        var skillSource = new SqliteSkillSource(skills, roleRegistry);
        services.AddSingleton(skillSource);
        services.AddSingleton<ISkillSource>(skillSource);

        // Seed the skill catalog: pipeline-behavior skills per role
        // (Forge-owned, seed-if-absent — operator edits win) + EVERY
        // registered project's .kilo/skills imported as repo-owned,
        // project-scoped rows (repo is the source of truth — SKILL.md
        // edits propagate on startup; removed files remove rows).
        await SkillSeeder.SeedAsync(
            skills,
            knownProjects
                .Select(p => new SkillSeeder.ProjectSkillSource(
                    p.Id, Path.Combine(p.Root, ".kilo", "skills")))
                .ToList(),
            loggerFactory.CreateLogger("Forge.SkillSeeder"),
            ct);

        // The memory table lives in IssueStore's schema (v7). On SQLite
        // it lives in a separate memory.db file, so construct an
        // IssueStore against it once at startup to run the schema before
        // MemoryStore touches it. On SQL Server the memory table lives
        // in the same per-project schema — no separate bootstrap needed.
        var memoryDbPath = Path.Combine(primaryStateDir, "memory.db");
        MemoryStore memoryStore;
        if (options.Db.IsSqlServer)
        {
            memoryStore = new MemoryStore(primaryFactory);
        }
        else
        {
            _ = new IssueStore(memoryDbPath);
            memoryStore = new MemoryStore(memoryDbPath);
        }
        services.AddSingleton(memoryStore);

        var agentRunStore = new AgentRunStore(primaryFactory);
        services.AddSingleton(agentRunStore);
        var workflowResolver = new Core.Workflow.WorkflowResolver(memoryStore);
        services.AddSingleton(workflowResolver);
        var stageGates = new StageGates(memoryStore, workflowResolver);
        services.AddSingleton(stageGates);

        var issuesJsonlPath = Path.Combine(primaryStateDir, "issues.jsonl");
        var jsonlMirror = new IssuesJsonlMirror(issues, issuesJsonlPath,
            loggerFactory.CreateLogger<IssuesJsonlMirror>());
        services.AddSingleton(jsonlMirror);

        var groomerRuns = new IssueGroomerRunStore(primaryFactory);
        services.AddSingleton(groomerRuns);
        var designArtifacts = new DesignArtifactStore(primaryFactory);
        services.AddSingleton(designArtifacts);
        var designerRuns = new DesignerRunStore(primaryFactory);
        services.AddSingleton(designerRuns);
        var artOutputs = new ArtOutputStore(primaryFactory);
        services.AddSingleton(artOutputs);
        var artistRuns = new ArtistRunStore(primaryFactory);
        services.AddSingleton(artistRuns);
        var recoveryReports = new RecoveryReportStore(primaryFactory);
        services.AddSingleton(recoveryReports);

        var vision = new VisionStore(primary.Root, options.Vision.Path);
        services.AddSingleton(vision);
        var rolePromptsRoot = RolePromptRoot.Resolve(primary.Root);
        log.LogInformation("Role prompts root: {RolePromptsRoot}", rolePromptsRoot);
        var visionSnapshot = vision.Reload();
        if (visionSnapshot.Exists)
        {
            log.LogInformation("Vision loaded from {Path} ({Len} chars)",
                visionSnapshot.Path, visionSnapshot.Content.Length);
            await memoryStore.RememberAsync("vision/master", visionSnapshot.Content, ttlDays: null, ct);
        }
        else
        {
            log.LogWarning("Vision file not found at {Path}; dashboard Vision tab will be empty", visionSnapshot.Path);
        }

        var skillBootstrap = new SkillBootstrap(
            memoryStore, loggerFactory.CreateLogger<SkillBootstrap>());
        await skillBootstrap.SeedAsync();

        var llmConfig = await Program.ResolveProviderApiKeysAsync(
            LlmConfigAdapter.FromOptions(options.Llm), knownProjects, secretStore,
            loggerFactory.CreateLogger("Forge.Bootstrap"));
        services.AddSingleton(llmConfig);
        var (chatClientFactory, costTracker) = Program.SelectChatClientFactory(llmConfig, options.Llm, options.Headroom);
        services.AddSingleton(chatClientFactory);
        if (costTracker is not null)
            services.AddSingleton(costTracker);

        var roleModelOverrides = new RoleModelOverrides(memoryStore);
        await roleModelOverrides.LoadAsync(ct);
        services.AddSingleton(roleModelOverrides);
        // ONE shared 429 tracker for the whole process: a 429 from ANY
        // subsystem cools that model for ALL of them.
        var modelRateLimits = new ModelRateLimitTracker();
        services.AddSingleton(modelRateLimits);
        ProviderApiKeyResolver? providerKeyResolver = null;
        if (chatClientFactory is OpenAICompatibleChatClientFactory openAiFactory)
        {
            openAiFactory.Overrides = roleModelOverrides;
            openAiFactory.RateLimits = modelRateLimits;
            openAiFactory.MaxConcurrentRequests = options.Llm.MaxConcurrentRequests;
            openAiFactory.OverloadRetryCount = options.Llm.OverloadRetryCount;

            // Live provider keys: a Secrets-page rotation takes effect
            // on the next run — no restart. The 30s refresh loop is
            // started by the runtime (RunOrchestratorAsync) so it binds
            // to the shutdown token.
            var keyResolver = new ProviderApiKeyResolver(
                secretStore,
                async c => (await projectStore.ListAsync(c)).Select(p => p.Id).ToArray(),
                loggerFactory.CreateLogger<ProviderApiKeyResolver>());
            openAiFactory.KeyResolver = keyResolver;
            providerKeyResolver = keyResolver;
            var providerNames = llmConfig.Providers.Select(p => p.Name).ToArray();
            await keyResolver.RefreshAsync(providerNames, ct);
            services.AddSingleton(keyResolver);
        }

        var extractionStore = new MemoryExtractionStore(primaryFactory);
        services.AddSingleton(extractionStore);
        var sprintProposalAudit = new SprintProposalAuditStore(primaryFactory);
        services.AddSingleton(sprintProposalAudit);
        var scorer = new DeterministicScorer();
        services.AddSingleton(scorer);
        var sprintPropose = new SprintProposeService(issues, sprints, scorer, sprintProposalAudit);
        services.AddSingleton(sprintPropose);
        var memoryExtractor = new MemoryExtractor(
            chatClientFactory, llmConfig, memoryStore,
            loggerFactory.CreateLogger<MemoryExtractor>(),
            sprints: sprints);
        services.AddSingleton(memoryExtractor);
        var specStoreRef = new SpecStoreHolder();
        services.AddSingleton(specStoreRef);

        MafAgentRunner.DiagnosticLogPath = Path.Combine(orchDataRoot, "logs", "agent.log");
        var agentRunner = new MafAgentRunner(
            chatClientFactory, llmConfig, roleRegistry,
            loggerFactory.CreateLogger<MafAgentRunner>(),
            skills: skillSource,
            rolePromptsRoot: rolePromptsRoot,
            projectRootLookup: ProjectRootLookup,
            projectTerritoryLookup: ProjectTerritoryLookup,
            verifyCommandsLookup: id => projectFactory.KnownProjects
                .FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                ?.VerifyCommands,
            memory: memoryStore,
            handoffs: new ContextHandoffStore(primaryFactory),
            designArtifacts: () => designArtifacts,
            specs: () => specStoreRef.Value,
            artOutputs: () => artOutputs,
            secrets: secretStore,
            issues: issues,
            runs: agentRunStore,
            // Per-project run registry: the run row lands in the OWNING
            // project's schema (operator rule 2026-07-30).
            runsByProject: pid => pid is null
                ? agentRunStore
                : projectFactory.Find(pid) is { } runCtx
                    ? new AgentRunStore(((IssueStore)runCtx.Issues).Db)
                    : agentRunStore,
            // file_followup rows belong to the RUN's project store.
            issueStoreLookup: pid => pid is null
                ? null
                : projectFactory.Find(pid)?.Issues,
            modelOverrides: roleModelOverrides,
            gates: options.Gates);
        services.AddSingleton(agentRunner);
        services.AddSingleton<IAgentRunner>(agentRunner);
        var eventBus = new InMemoryDashboardEventBus();
        services.AddSingleton(eventBus);
        services.AddSingleton<IDashboardEventBus>(eventBus);
        var lifecycle = new TaskStateMachine(
            options.State.WriteAuthority,
            loggerFactory.CreateLogger<TaskStateMachine>());
        services.AddSingleton(lifecycle);

        // P4 Stage B — pick the workflow runtime based on appsettings.json.
        IWorkflowDispatcher dispatcher;
        if (string.Equals(options.Orchestrator.Execution, "Durable", StringComparison.OrdinalIgnoreCase))
        {
            var workflow = new Orchestrator.Workflow.EngineeringDispatchWorkflow(
                issues, agentRunner, worktrees, gitHub, roleRegistry, options.Workspace,
                eventBus, agent => messageBus.Drain(agent),
                designArtifacts, artOutputs,
                memoryExtractor, extractionStore,
                loggerFactory.CreateLogger<Orchestrator.Workflow.EngineeringDispatchWorkflow>(),
                projectId: primary.Id,
                timeoutMinutes: options.Spawner.AgentRunTimeoutMinutes,
                lifecycle: lifecycle,
                workflow: workflowResolver,
                verifyCommands: primary.VerifyCommands)
                .Build();
            var durableServices = new ServiceCollection()
                .AddSingleton(workflow)
                .BuildServiceProvider();
            dispatcher = new DurableDispatcher(
                options.Orchestrator,
                workflow,
                loggerFactory.CreateLogger<DurableDispatcher>(),
                buildHost: () => DurableDispatcher.BuildHost(
                    durableServices, workflow, options.Orchestrator));
        }
        else
        {
            // InProcessDispatcher (default): runs the same workflow via
            // InProcessExecution. P4 Stage A's StartupRecovery handles
            // crash safety.
            dispatcher = new InProcessDispatcher(
                async (issue, bundle, ct) =>
                {
                    var workflow = new Orchestrator.Workflow.EngineeringDispatchWorkflow(
                        bundle.IssueStore, agentRunner, bundle.Worktrees, bundle.GitHub, roleRegistry,
                        new WorkspaceOptions { DefaultBranch = bundle.Project.DefaultBranch },
                        eventBus, agent => messageBus.Drain(agent),
                        bundle.DesignArtifacts, bundle.ArtOutputs,
                        memoryExtractor, extractionStore,
                        loggerFactory.CreateLogger<Orchestrator.Workflow.EngineeringDispatchWorkflow>(),
                        projectId: bundle.Project.Id,
                        loggerFactory: loggerFactory,
                        sprints: bundle.Sprints,
                        timeoutMinutes: options.Spawner.AgentRunTimeoutMinutes,
                        workflow: workflowResolver,
                        verifyCommands: bundle.Project.VerifyCommands,
                        // Message-driven review trigger: publish
                        // PrOpened — the PrOpenedConsumer launches the
                        // background review on the pushed head while CI
                        // runs; the 15m sweep tick is the backstop.
                        onPrOpened: (task, ct) =>
                        {
                            if (!int.TryParse(task.GetMetadata("prNumber"), out var prNumber))
                                return Task.CompletedTask;
                            return eventPublisher.PublishAsync(new Core.Messaging.PrOpened
                            {
                                MessageId = Core.Messaging.PrOpened.IdFor(task.Id, prNumber, task.GetMetadata("branchSha")),
                                ProjectId = bundle.Project.Id,
                                TaskId = task.Id,
                                PrNumber = prNumber,
                                Branch = task.GetMetadata("branch"),
                            }, ct);
                        });
                    await workflow.RunAsync(issue, ct);
                },
                loggerFactory.CreateLogger<InProcessDispatcher>());
        }
        services.AddSingleton(dispatcher);

        var dispatchBundleFactory = new ProjectDispatchBundleFactory(
            options, orchDataRoot, projectStore, cloner,
            agentRunner, roleRegistry, dispatcher, messageBus, eventBus, loggerFactory,
            secrets: secretStore, gates: stageGates, lifecycle: lifecycle,
            eventPublisher: eventPublisher);
        services.AddSingleton(dispatchBundleFactory);

        // Watch pipeline + scheduler triggers: ONE consumer per topic
        // (the in-memory transport is competing-consumer) fanning out
        // to every interested loop. The watch sweep runs on
        // SweepTick(watch); PrOpened / ReviewVerdictRecorded /
        // MergeReady transitions drive the immediate fast paths.
        var watchSweeps = new WatchSweepService(
            agentRunner, llmConfig, roleModelOverrides, modelRateLimits,
            lifecycle, workflowResolver, eventBus, loggerFactory,
            loggerFactory.CreateLogger<WatchSweepService>());
        services.AddSingleton(watchSweeps);
        services.AddSingleton(sp => new Consumers.TaskTransitionedConsumer(
            sp.GetRequiredService<ITransport>(), wakeups, dispatchBundleFactory, projectStore,
            sp.GetRequiredService<ILogger<Consumers.TaskTransitionedConsumer>>()));
        services.AddSingleton(sp => new Consumers.SweepTickConsumer(
            sp.GetRequiredService<ITransport>(), dispatchBundleFactory, projectStore,
            wakeups, watchSweeps, sp.GetRequiredService<ILogger<Consumers.SweepTickConsumer>>()));
        services.AddSingleton(sp => new Consumers.SpecStatusChangedConsumer(
            sp.GetRequiredService<ITransport>(), wakeups,
            sp.GetRequiredService<ILogger<Consumers.SpecStatusChangedConsumer>>()));
        services.AddSingleton(sp => new Consumers.SprintStatusChangedConsumer(
            sp.GetRequiredService<ITransport>(), wakeups,
            sp.GetRequiredService<ILogger<Consumers.SprintStatusChangedConsumer>>()));
        services.AddSingleton(sp => new Consumers.FollowUpFiledConsumer(
            sp.GetRequiredService<ITransport>(), wakeups,
            sp.GetRequiredService<ILogger<Consumers.FollowUpFiledConsumer>>()));
        services.AddSingleton(sp => new Consumers.GroomRequestedConsumer(
            sp.GetRequiredService<ITransport>(), wakeups,
            sp.GetRequiredService<ILogger<Consumers.GroomRequestedConsumer>>()));
        services.AddSingleton(sp => new Consumers.PrOpenedConsumer(
            sp.GetRequiredService<ITransport>(), dispatchBundleFactory, projectStore,
            watchSweeps, sp.GetRequiredService<ILogger<Consumers.PrOpenedConsumer>>()));
        services.AddSingleton(sp => new Consumers.ReviewVerdictRecordedConsumer(
            sp.GetRequiredService<ITransport>(), dispatchBundleFactory, projectStore,
            sp.GetRequiredService<ILogger<Consumers.ReviewVerdictRecordedConsumer>>()));

        GitHubService? GitHubForProject(string projectId)
        {
            try
            {
                var project = string.IsNullOrEmpty(projectId)
                    ? knownProjects.FirstOrDefault()
                    : knownProjects.FirstOrDefault(p => string.Equals(p.Id, projectId, StringComparison.Ordinal));
                return project is null ? null : dispatchBundleFactory.Build(project).GitHub;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "GitHubForProject({ProjectId}): resolution failed", projectId);
                return null;
            }
        }

        var slots = Program.BuildSlotTable(knownProjects);
        services.AddSingleton(slots);
        var orchestrator = new OrchestratorAgent(
            projectStore,
            dispatchBundleFactory,
            agentRunner, roleRegistry,
            messageBus, dispatcher, eventBus,
            loggerFactory.CreateLogger<OrchestratorAgent>(),
            loggerFactory: loggerFactory,
            slots: slots,
            modelCooldowns: modelRateLimits,
            lifecycle: lifecycle,
            workflow: workflowResolver,
            wakeup: wakeups.Dispatch);
        orchestrator.BindOptions(options);
        orchestrator.ModelOverrides = roleModelOverrides;
        services.AddSingleton(orchestrator);

        var intakeStore = new IntakeStore(issues);
        services.AddSingleton(intakeStore);
        services.AddSingleton<IIntakeStore>(intakeStore);
        var specStore = new SpecStore(issues, designArtifacts: designArtifacts);
        services.AddSingleton(specStore);
        services.AddSingleton<ISpecStore>(specStore);
        // Planning lane spec rows are PER-PROJECT workload data
        // (operator rule 2026-07-31): writers route through a
        // project-aware facade; dashboard endpoints keep the PLAIN store.
        var routingSpecStore = new ProjectRoutingSpecStore(
            specStore,
            findByProject: pid => projectFactory.Find(pid)?.Specs,
            allProjectStores: () => projectFactory.KnownProjects
                .Select(proj => projectFactory.Find(proj.Id)?.Specs)
                .Where(st => st is not null)
                .Cast<ISpecStore>()
                .ToList());
        services.AddSingleton(routingSpecStore);
        specStoreRef.Set(routingSpecStore);

        var intakeRegistry = new IntakeAgentRegistry(projectId =>
            new IntakeAgent(
                projectId,
                intakeStore,
                // Epics belong to the SESSION'S project store (routing
                // incident 2026-07-29).
                projectFactory.Find(projectId)?.Issues ?? issues,
                projectFactory.Find(projectId)?.Sprints ?? sprints,
                chatClientFactory,
                llmConfig,
                roleRegistry,
                eventBus,
                loggerFactory.CreateLogger<IntakeAgent>(),
                skills: skillSource,
                rolePromptsRoot: rolePromptsRoot,
                specs: specStoreRef.Value));
        services.AddSingleton(intakeRegistry);

        var specExtractionReader = new SpecExtractionReader(issues);
        services.AddSingleton<ISpecExtractionReader>(specExtractionReader);
        var codebaseGraphCache = new CodebaseGraphCacheStore(issues);
        services.AddSingleton<ICodebaseGraphCacheStore>(codebaseGraphCache);
        var codebaseGraphBuilder = new DotnetCodebaseGraphBuilder();
        services.AddSingleton<ICodebaseGraphBuilder>(codebaseGraphBuilder);
        var projectContextSource = new FilesystemProjectContextSource(
            issues, agents, routingSpecStore, skills, primary.Root);
        services.AddSingleton(projectContextSource);
        var productAgentFactory = new ProductAgentFactory(
            routingSpecStore, issues, projectContextSource, chatClientFactory, llmConfig,
            roleRegistry, eventBus, skillSource, loggerFactory,
            rolePromptsRoot);
        services.AddSingleton(productAgentFactory);
        // Self-starts in its ctor (event subscription + worker task).
        var productRefinementQueue = new ProductRefinementQueue(
            productAgentFactory, routingSpecStore, eventBus,
            loggerFactory.CreateLogger<ProductRefinementQueue>());
        services.AddSingleton(productRefinementQueue);
        var groomerFactory = new GroomerAgentFactory(
            issues, routingSpecStore, eventBus, chatClientFactory, llmConfig, loggerFactory,
            memory: memoryStore, projectRoot: primary.Root,
            projectRootLookup: ProjectRootLookup,
            issueStoreLookup: id => projectFactory.Find(id)?.Issues);
        services.AddSingleton(groomerFactory);

        var designHygiene = new DesignHygieneChecker(
            routingSpecStore, codebaseGraphCache, codebaseGraphBuilder, primary.Root,
            projectRootLookup: ProjectRootLookup);
        services.AddSingleton(designHygiene);
        var designerAgentFactory = new DesignerAgentFactory(
            routingSpecStore, designArtifacts, designerRuns, memoryStore, designHygiene,
            chatClientFactory, llmConfig, roleRegistry, eventBus, loggerFactory,
            rolePromptsRoot);
        services.AddSingleton(designerAgentFactory);

        var meshyOptions = Microsoft.Extensions.Options.Options.Create(new Forge.Meshy.MeshyOptions
        {
            ApiKey = options.Llm.MeshyApiKey,
            BaseUrl = options.Llm.MeshyBaseUrl,
            PollIntervalSeconds = options.Llm.MeshyPollIntervalSeconds,
            MaxWaitSeconds = options.Llm.MeshyMaxWaitSeconds,
            MaxConcurrentJobs = options.Llm.MeshyMaxConcurrentJobs,
        });
        var meshy = new Forge.Meshy.MeshyClient(
            new SocketsHttpHandler(),
            meshyOptions,
            loggerFactory.CreateLogger<Forge.Meshy.MeshyClient>(),
            artOutputRoot: primary.Id == "default"
                ? Path.Combine(primary.Root, ".portHorizon", "art-output")
                : ForgesystemPaths.ArtOutputDir(orchDataRoot, primary.Id));
        services.AddSingleton(meshy);
        var artistAgentFactory = new ArtistAgentFactory(
            routingSpecStore, designArtifacts, artOutputs, artistRuns, memoryStore, meshy,
            chatClientFactory, llmConfig, roleRegistry, eventBus, loggerFactory);
        services.AddSingleton(artistAgentFactory);

        var primaryBundle = dispatchBundleFactory.Build(primary);
        var startupRecovery = new StartupRecovery(
            issues, recoveryReports, primaryBundle.Worktrees,
            new GitHubRecoveryAdapter(primaryBundle.GitHub),
            eventBus,
            loggerFactory.CreateLogger<StartupRecovery>(),
            lifecycle: lifecycle);
        services.AddSingleton(startupRecovery);

        log.LogInformation(
            "Multi-project registry: {Count} project(s) [{Ids}]; slot caps configured per role.",
            knownProjects.Count,
            string.Join(",", knownProjects.Select(p => $"{p.Id}={p.Name}")));

        // DashboardHost: factory-registered so it resolves the shared
        // messaging instances (same transport + publisher as the
        // orchestrator consumers) from THIS container.
        services.AddSingleton(sp => new DashboardHost(
            options.Dashboard, options.Headroom,
            sp.GetRequiredService<IIssueStore>(),
            sp.GetRequiredService<IAgentStore>(),
            sp.GetRequiredService<ISkillStore>(),
            sp.GetRequiredService<ISprintStore>(),
            messageBus, eventBus,
            sp.GetRequiredService<ILogger<DashboardHost>>(),
            intakeStore: intakeStore,
            intakeRegistry: intakeRegistry,
            specs: specStore,
            groomerFactory: groomerFactory,
            memory: memoryStore,
            extractions: extractionStore,
            sprintProposalAudit: sprintProposalAudit,
            sprintPropose: sprintPropose,
            issuesJsonlPath: issuesJsonlPath,
            vision: vision,
            groomerRuns: groomerRuns,
            designerFactory: designerAgentFactory,
            designerRuns: designerRuns,
            designArtifacts: designArtifacts,
            artistFactory: artistAgentFactory,
            artistRuns: artistRuns,
            artOutputs: artOutputs,
            meshy: meshy,
            recoveryReports: recoveryReports,
            startupRecovery: startupRecovery,
            costTracker: costTracker,
            extractor: specExtractionReader,
            codebaseBuilder: codebaseGraphBuilder,
            codebaseCache: codebaseGraphCache,
            projectFactory: projectFactory,
            slots: slots,
            gitHub: gitHub,
            reviewerRunner: agentRunner,
            loggerFactory: loggerFactory,
            projectStore: projectStore,
            projectCloner: cloner,
            githubOptions: options.GitHub,
            secretStore: secretStore,
            agentRuns: agentRunStore,
            llmConfig: llmConfig,
            roleModelOverrides: roleModelOverrides,
            gateOptions: options.Gates,
            lifecycle: lifecycle,
            modelRateLimits: modelRateLimits,
            gitHubForProject: GitHubForProject,
            providerApiKeys: providerKeyResolver,
            eventPublisher: sp.GetRequiredService<IEventPublisher>(),
            transport: sp.GetRequiredService<ITransport>()));

        // Schedulers: trigger events kick via the wakeup signals; the
        // interval is now the 15-MINUTE BACKSTOP (the 5m cadence is
        // gone — events drive the fast path). Constructed here, started
        // by the runtime with the shutdown token (RunOrchestratorAsync).
        var followUpTriage = new FollowUpTriageAgent(
            chatClientFactory, llmConfig,
            loggerFactory.CreateLogger<FollowUpTriageAgent>());
        services.AddSingleton(followUpTriage);
        services.AddSingleton(new ScheduledGroomer(
            routingSpecStore, groomerFactory, groomerRuns, eventBus,
            loggerFactory.CreateLogger<ScheduledGroomer>(),
            interval: TimeSpan.FromMinutes(15),
            issues: issues, sprints: sprints, gates: stageGates,
            projectContexts: projectFactory,
            wakeup: wakeups.Groom));
        services.AddSingleton(new ScheduledWatchdog(
            projectFactory, eventBus,
            loggerFactory.CreateLogger<ScheduledWatchdog>(),
            lifecycle));
        services.AddSingleton(new DesignerScheduler(
            routingSpecStore, designerAgentFactory, designerRuns, eventBus,
            loggerFactory.CreateLogger<DesignerScheduler>(),
            interval: TimeSpan.FromMinutes(15),
            gates: stageGates,
            workflow: workflowResolver,
            wakeup: wakeups.Design));
        services.AddSingleton(new ArtistScheduler(
            routingSpecStore, artistAgentFactory, artistRuns, eventBus,
            loggerFactory.CreateLogger<ArtistScheduler>(),
            interval: TimeSpan.FromMinutes(15),
            wakeup: wakeups.Artist));
        services.AddSingleton(new Orchestrator.Sprint.SprintAssembler(
            projectFactory, eventBus,
            loggerFactory.CreateLogger<Orchestrator.Sprint.SprintAssembler>(),
            interval: TimeSpan.FromMinutes(15),
            gates: stageGates,
            followUpTriage: followUpTriage,
            wakeup: wakeups.Assemble,
            eventPublisher: eventPublisher));

        return services.BuildServiceProvider();
    }
}
