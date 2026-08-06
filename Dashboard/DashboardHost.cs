using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Forge.Agents;
using Forge.Codebase;
using Forge.Configuration;
using Forge.Core;
using Forge.Orchestrator;
using Forge.Orchestrator.Slots;
using Forge.Projects;

using Forge.Agents.Gates;
namespace Forge.Dashboard;

public sealed class DashboardHost : IAsyncDisposable
{
    private readonly DashboardOptions _options;
    private readonly HeadroomOptions _headroom;
    private readonly IIssueStore _issues;
    private readonly IAgentStore _agents;
    private readonly ISkillStore _skills;
    private readonly ISprintStore _sprints;
    private readonly IIntakeStore _intakeStore;
    private readonly IntakeAgentRegistry? _intakeRegistry;
    private readonly ISpecStore _specs;
    private readonly Agents.GroomerAgentFactory? _groomerFactory;
    private readonly MemoryStore? _memory;
    private readonly Orchestrator.MemoryExtractionStore? _extractions;
    private readonly string? _issuesJsonlPath;
    private readonly VisionStore? _vision;
    private readonly Orchestrator.DesignerAgentFactory? _designerFactory;
    private readonly DesignerRunStore? _designerRuns;
    private readonly DesignArtifactStore? _designArtifacts;
    private readonly Orchestrator.ArtistAgentFactory? _artistFactory;
    private readonly ArtistRunStore? _artistRuns;
    private readonly ArtOutputStore? _artOutputs;
    private readonly Meshy.MeshyClient? _meshy;
    private readonly RecoveryReportStore? _recoveryReports;
    private readonly Orchestrator.StartupRecovery? _startupRecovery;
    private readonly CostTracker? _costTracker;
    private readonly IssueGroomerRunStore? _groomerRuns;
    private readonly ISpecExtractionReader? _extractorOverride;
    private readonly ICodebaseGraphBuilder? _codebaseBuilderOverride;
    private readonly ICodebaseGraphCacheStore? _codebaseCacheOverride;
    private readonly AgentMessageBus _messageBus;
    private readonly Orchestrator.SprintProposalAuditStore? _sprintProposalAudit;
    private readonly Orchestrator.SprintProposeService? _sprintPropose;
    private readonly GitHubService? _gitHub;
    private readonly Forge.Agents.IAgentRunner? _reviewerRunner;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly InMemoryDashboardEventBus _bus;
    private readonly ProjectContextFactory? _projectFactory;
    private readonly SlotTable? _slots;
    private readonly IProjectStore? _projectStore;
    private readonly ProjectCloner? _projectCloner;
    private readonly Forge.Core.SecretStore? _secretStore;
    private readonly AgentRunStore? _agentRuns;
    private readonly Forge.Agents.LlmConfig? _llmConfig;
    private readonly Forge.Agents.RoleModelOverrides? _roleModelOverrides;
    private readonly Forge.Core.TaskStateMachine? _lifecycle;
    private readonly Forge.Core.ModelRateLimitTracker? _modelRateLimits;
    private readonly Func<string, GitHubService?>? _gitHubForProject;
    private readonly Forge.Agents.ProviderApiKeyResolver? _providerApiKeys;
    private readonly GitHubOptions? _githubOptions;
    private readonly GateOptions? _gateOptions;
    private readonly ILogger<DashboardHost> _logger;
    private WebApplication? _app;
    private int _port;

    public DashboardHost(
        DashboardOptions options,
        HeadroomOptions headroom,
        IIssueStore issues,
        IAgentStore agents,
        ISkillStore skills,
        ISprintStore sprints,
        AgentMessageBus messageBus,
        InMemoryDashboardEventBus bus,
        ILogger<DashboardHost> logger,
        IIntakeStore? intakeStore = null,
        IntakeAgentRegistry? intakeRegistry = null,
        ISpecStore? specs = null,
        Agents.GroomerAgentFactory? groomerFactory = null,
        MemoryStore? memory = null,
        Orchestrator.MemoryExtractionStore? extractions = null,
        string? issuesJsonlPath = null,
        VisionStore? vision = null,
        Orchestrator.DesignerAgentFactory? designerFactory = null,
        DesignerRunStore? designerRuns = null,
        DesignArtifactStore? designArtifacts = null,
        Orchestrator.ArtistAgentFactory? artistFactory = null,
        ArtistRunStore? artistRuns = null,
        ArtOutputStore? artOutputs = null,
        Meshy.MeshyClient? meshy = null,
        RecoveryReportStore? recoveryReports = null,
        Orchestrator.StartupRecovery? startupRecovery = null,
        CostTracker? costTracker = null,
        IssueGroomerRunStore? groomerRuns = null,
        ISpecExtractionReader? extractor = null,
        ICodebaseGraphBuilder? codebaseBuilder = null,
        ICodebaseGraphCacheStore? codebaseCache = null,
        Orchestrator.SprintProposalAuditStore? sprintProposalAudit = null,
        Orchestrator.SprintProposeService? sprintPropose = null,
        ProjectContextFactory? projectFactory = null,
        SlotTable? slots = null,
        GitHubService? gitHub = null,
        Forge.Agents.IAgentRunner? reviewerRunner = null,
        ILoggerFactory? loggerFactory = null,
        IProjectStore? projectStore = null,
        ProjectCloner? projectCloner = null,
        GitHubOptions? githubOptions = null,
        GateOptions? gateOptions = null,
        Forge.Core.SecretStore? secretStore = null,
        AgentRunStore? agentRuns = null,
        Forge.Agents.LlmConfig? llmConfig = null,
        Forge.Agents.RoleModelOverrides? roleModelOverrides = null,
        Forge.Core.TaskStateMachine? lifecycle = null,
        Forge.Core.ModelRateLimitTracker? modelRateLimits = null,
        Func<string, GitHubService?>? gitHubForProject = null,
        Forge.Agents.ProviderApiKeyResolver? providerApiKeys = null)
    {
        _options = options;
        _headroom = headroom;
        _issues = issues;
        _agents = agents;
        _skills = skills;
        _sprints = sprints;
        _intakeStore = intakeStore ?? new NullIntakeStore();
        _intakeRegistry = intakeRegistry;
        _specs = specs ?? new NullSpecStore();
        _groomerFactory = groomerFactory;
        _memory = memory;
        _extractions = extractions;
        _issuesJsonlPath = issuesJsonlPath;
        _groomerRuns = groomerRuns;
        _designerFactory = designerFactory;
        _designerRuns = designerRuns;
        _designArtifacts = designArtifacts;
        _artistFactory = artistFactory;
        _artistRuns = artistRuns;
        _artOutputs = artOutputs;
        _meshy = meshy;
        _recoveryReports = recoveryReports;
        _startupRecovery = startupRecovery;
        _costTracker = costTracker;
        _vision = vision;
        _extractorOverride = extractor;
        _codebaseBuilderOverride = codebaseBuilder;
        _codebaseCacheOverride = codebaseCache;
        _sprintProposalAudit = sprintProposalAudit;
        _gateOptions = gateOptions;
        _sprintPropose = sprintPropose;
        _gitHub = gitHub;
        _reviewerRunner = reviewerRunner;
        _loggerFactory = loggerFactory;
        _messageBus = messageBus;
        _bus = bus;
        _projectFactory = projectFactory;
        _slots = slots;
        _projectStore = projectStore;
        _projectCloner = projectCloner;
        _secretStore = secretStore;
        _githubOptions = githubOptions;
        _logger = logger;
        _agentRuns = agentRuns;
        _llmConfig = llmConfig;
        _roleModelOverrides = roleModelOverrides;
        _lifecycle = lifecycle;
        _modelRateLimits = modelRateLimits;
        _gitHubForProject = gitHubForProject;
        _providerApiKeys = providerApiKeys;
    }

    public string BaseUrl => ResolveBaseUrl();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Dashboard disabled by config");
            return;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
        });

        // Kestrel endpoints: bind 80 (HTTP) + 443 (HTTPS) if the
        // operator configured the Kestrel section; fall back to
        // the legacy Hostname:Port single-endpoint mode otherwise
        // (so existing dev/test setups keep working without
        // touching appsettings).
        if (_options.Kestrel is { Endpoints.Count: > 0 })
        {
            // Resolve HTTPS options once.
            var httpsCertPath = _options.Kestrel.Https?.Certificate?.Path is { Length: > 0 } p ? p : null;
            var httpsCertPwd = _options.Kestrel.Https?.Certificate?.Password ?? string.Empty;

            builder.WebHost.ConfigureKestrel(opts =>
            {
                foreach (var (name, ep) in _options.Kestrel.Endpoints)
                {
                    var port = ParsePort(ep.Url);
                    if (port <= 0) continue;
                    var isHttps = name.Equals("https", StringComparison.OrdinalIgnoreCase);
                    opts.ListenAnyIP(port, listen =>
                    {
                        if (isHttps && httpsCertPath is not null)
                            listen.UseHttps(httpsCertPath, httpsCertPwd);
                    });
                }
            });
        }

        // The base URL the Blazor WASM client + dashboard page see.
        // When the Kestrel section redirects, prefer the https://...
        // entry; otherwise the legacy Hostname:Port wins.
        var publicBaseUrl = ResolveBaseUrl();
        var localBaseAddress = ResolveLocalBaseAddress();
        builder.Services.AddForgeUI(new Uri(publicBaseUrl), new Uri(localBaseAddress));

        if (_projectFactory is not null) builder.Services.AddSingleton(_projectFactory);
        if (_slots is not null) builder.Services.AddSingleton(_slots);
        if (_projectFactory is not null) builder.Services.AddSingleton<Forge.Deploy.DeploymentExecutorFactory>();
        if (_projectStore is not null) builder.Services.AddSingleton(_projectStore);
        if (_projectCloner is not null) builder.Services.AddSingleton(_projectCloner);
        if (_githubOptions is not null) builder.Services.AddSingleton(_githubOptions);

        // Secrets: Microsoft.AspNetCore.DataProtection. The provider
        // is shared with the orchestrator process (same master key
        // ring at ~/.aspnet/DataProtection-Keys/ on Linux) so a
        // secret written by the dashboard is readable by the
        // agent's IAgentRunner the moment it's upserted. The
        // purpose string is in SecretStore's ctor.
        if (_secretStore is not null)
        {
            builder.Services.AddDataProtection();
            // Register the concrete instance by its interface so
            // endpoint signatures like `[FromServices] ISecretStore`
            // resolve correctly.
            builder.Services.AddSingleton<Forge.Core.ISecretStore>(_secretStore);
        }

        // 2026-07-18 (Phase 2.11.f + bug-1-review): the Reviewer
        // dispatcher needs to be in the DI service collection
        // BEFORE builder.Build() because the service collection
        // is read-only after that point. The endpoint pulls it
        // from ctx.RequestServices.
        Forge.Reviewer.ReviewerDispatcher? reviewerDispatcherForBuild = null;
        if (_projectFactory is not null && _gitHub is not null && _reviewerRunner is not null)
        {
            // Reviewer verdicts are recorded in queue metadata (the
            // machine record) with a GitHub comment as audit — no
            // separate reviewer token is required in the
            // solo-identity model.
            var d = new Forge.Reviewer.ReviewerDispatcher(
                _issues, _gitHub, _reviewerRunner,
                _loggerFactory?.CreateLogger<Forge.Reviewer.ReviewerDispatcher>()
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Forge.Reviewer.ReviewerDispatcher>.Instance,
                lifecycle: _lifecycle);
            builder.Services.AddSingleton(d);
            reviewerDispatcherForBuild = d;
        }

 _app = builder.Build();
        _app.Urls.Clear();
        // Kestrel already binds the configured endpoints via the
        // ConfigureKestrel calls above. Clearing Urls prevents the
        // generic host from also binding Hostname:Port (which would
        // double-bind when both sections are configured).

        _app.UseRouting();

        if (_options.Kestrel.RedirectHttps &&
            _options.Kestrel.Endpoints.ContainsKey("http") &&
            _options.Kestrel.Endpoints.ContainsKey("https"))
        {
            _app.UseHttpsRedirection();
        }

        _app.UseAntiforgery();

_app.MapGet("/api/state", async (string? projectId, CancellationToken ct) =>
        {
            try
            {
                // Multi-project: ?projectId= reads that project's issue +
                // sprint stores (agents/skills stay global — they're not
                // project-scoped today). Absent param = primary project.
                var issueStore = _issues;
                var sprintStore = _sprints;
                if (projectId is not null && _projectFactory is not null)
                {
                    var pctx = _projectFactory.Find(projectId);
                    if (pctx is null) return Results.NotFound(new { error = "project not found", projectId });
                    issueStore = pctx.Issues;
                    sprintStore = pctx.Sprints;
                }
                var tasks = await issueStore.ListAsync(new IssueFilter(), ct);
                var activeSprint = await sprintStore.GetActiveAsync(ct);
                var agents = await _agents.ListAsync(ct);
                var skills = await _skills.ListAsync(null, globalOnly: false, ct);
                var sprints = await sprintStore.ListAsync(activeOnly: false, ct);
                // Sprint rollups: member tasks (containers/watches
                // excluded) + terminal counts so the Sprints page can
                // render progress without a second query round-trip.
                var statusById = tasks.ToDictionary(t => t.Id);
                // One dep query for the whole payload: blocked id →
                // open blocker ids (sprint board "blocked by" badge).
                var allMemberIds = new List<string>();
                var memberIdsBySprint = new Dictionary<string, List<string>>();
                foreach (var sp0 in sprints)
                {
                    var ids0 = await sprintStore.GetIssueIdsAsync(sp0.Id, ct);
                    memberIdsBySprint[sp0.Id] = ids0.ToList();
                    allMemberIds.AddRange(ids0);
                }
                var openBlockers = await issueStore.OpenBlockersAsync(allMemberIds, ct);
                var sprintViews = new List<object>();
                foreach (var sp in sprints)
                {
                    var memberIds = memberIdsBySprint[sp.Id];
                    var members = memberIds
                        .Select(id => statusById.TryGetValue(id, out var t) ? t : null)
                        .Where(t => t is not null
                            && !AgentTaskTypes.IsContainer(t.Type)
                            && t.Type != AgentTaskTypes.PrWatch)
                        .Select(t => new
                        {
                            id = t!.Id,
                            title = t.Title,
                            status = t.Status.ToString(),
                            blockedBy = openBlockers.TryGetValue(t.Id, out var bb) ? bb : null,
                            // Board self-sufficiency (operator
                            // 2026-07-31): why it's in its column +
                            // what happens next, computed server-side.
                            situation = Forge.Core.TaskSituation.Describe(t).Text,
                            situationTone = Forge.Core.TaskSituation.Describe(t).Tone,
                        })
                        .ToArray();
                    sprintViews.Add(new
                    {
                        id = sp.Id,
                        name = sp.Name,
                        goal = sp.Goal,
                        startDate = sp.StartDate,
                        endDate = sp.EndDate,
                        status = sp.Status.ToString(),
                        createdAt = sp.CreatedAt,
                        updatedAt = sp.UpdatedAt,
                        issueCount = members.Length,
                        doneCount = members.Count(m => m.status is "Completed" or "Closed"),
                        members,
                    });
                }
                var view = new
                {
                    tasks = tasks.Select(t => new
                    {
                        id = t.Id,
                        type = t.Type,
                        title = t.Title,
                        description = t.Description,
                        status = t.Status.ToString(),
                        priority = t.Priority,
                        assignee = t.Assignee,
                        createdAt = t.CreatedAt,
                        updatedAt = t.UpdatedAt,
                        closedAt = t.ClosedAt,
                        dispatchCheckpoint = t.DispatchCheckpoint?.ToString(),
                        checkpointAt = (DateTime?)null,
                        recoveryAttempts = t.RecoveryAttempts,
                        parentIssueId = t.ParentIssueId,
                        prUrl = (string?)null,
                        branch = (string?)null,
                        worktreePath = (string?)null,
                        parameters = ParseMetadata(t.MetadataJson)
                    }).ToArray(),
                    agents = agents.Select(a => new
                    {
                        id = a.Id,
                        agentName = a.AgentName,
                        displayName = a.DisplayName,
                        scope = a.Scope,
                        description = a.Description,
                        enabled = a.Enabled,
                        configJson = a.ConfigJson,
                        createdAt = a.CreatedAt,
                        updatedAt = a.UpdatedAt
                    }).ToArray(),
                    skills = skills.Select(s => new
                    {
                        id = s.Id,
                        name = s.Name,
                        description = s.Description,
                        body = s.Body,
                        agentId = s.AgentId,
                        enabled = s.Enabled,
                        createdAt = s.CreatedAt,
                        updatedAt = s.UpdatedAt
                    }).ToArray(),
                    sprints = sprintViews.ToArray(),
                    lastHeartbeat = DateTime.UtcNow,
                    completedTasks = tasks.Count(t => t.Status == IssueStatus.Completed),
                    failedTasks = tasks.Count(t => t.Status == IssueStatus.Failed),
                    schemaVersion = 4,
                    activeSprintId = activeSprint?.Id
                };
                return Results.Json(view, DashboardJson.Options);
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 503);
            }
        });

        DashboardEndpoints.MapP1Endpoints(_app, _issues, _agents, _skills, _sprints, _messageBus, _logger, _projectFactory);

        if (_projectFactory is not null && _slots is not null)
        {
            ProjectsEndpoints.MapProjectsEndpoints(_app);
            ProjectLookupEndpoints.MapProjectLookupEndpoints(_app);
            SecretsEndpoints.MapSecretsEndpoints(_app);
            DeploymentsEndpoints.MapDeploymentsEndpoints(_app);
        }

        // Cert download — the install helper for first-time
        // operators. Served over both HTTP and HTTPS (HTTPS would
        // require the cert to already be trusted, defeating the
        // point of this download).
        CertEndpoints.MapCertEndpoints(_app);

        if (reviewerDispatcherForBuild is not null)
        {
            ReviewerEndpoints.MapReviewerEndpoints(_app, reviewerDispatcherForBuild);
        }

        // 2026-07-17 (epic-1): the dashboard listens via
        // Microsoft's IHostLifetime. The health endpoint below
        // synthesizes listening/dispatch/deployment signals into a
        // single /api/forgesystem/health snapshot for the
        // operator. DefaultHealthSnapshotFactory reads the
        // orchestrator's recovery + deployment tables when
        // they're wired; otherwise it returns placeholders.
        HealthEndpoint.MapHealthEndpoint(_app, new DefaultHealthSnapshotFactory());

        MetaEndpoints.MapMetaEndpoints(_app);

        AppShellEndpoints.MapAppShellEndpoints(_app, _issues, _sprints, _specs, _memory, _logger, _projectFactory);

        if (_intakeRegistry is not null)
        {
            IntakeEndpoints.MapIntakeEndpoints(_app, _intakeRegistry, _issues, _sprints, _intakeStore, _logger);
        }

        SpecEndpoints.MapSpecEndpoints(_app, _specs, _extractorOverride ?? new NullSpecExtractionReader(), _logger, _intakeStore, _groomerFactory, _groomerRuns, _projectFactory, _issues);

        if (_memory is not null)
        {
            MemoryEndpoints.MapMemoryEndpoints(_app, _memory, _logger);
        }

        if (_extractions is not null)
        {
            MemoryEndpoints.MapExtractionEndpoints(_app, _extractions, _logger);
        }

        if (_issuesJsonlPath is not null)
        {
            IssuesJsonlEndpoints.MapIssuesJsonlEndpoints(_app, _issuesJsonlPath, _logger);
        }

        if (_vision is not null)
        {
            VisionEndpoints.MapVisionEndpoints(_app, _vision, _logger, _memory, _issues);
        }

        if (_memory is not null)
        {
            GateEndpoints.MapGateEndpoints(_app,
                new StageGates(_memory, new Forge.Core.Workflow.WorkflowResolver(_memory)), _logger);
            RunGateCatalogEndpoints.MapRunGateCatalogEndpoints(_app, _gateOptions ?? new GateOptions(), _memory, _logger);
            WorkflowEndpoints.MapWorkflowEndpoints(_app, _memory, _bus, _logger);
        }
        GateVerdictEndpoints.MapGateVerdictEndpoints(_app, _issues, _logger);

        FlowEndpoints.MapFlowEndpoints(_app, _issues, _specs, _sprints, _extractions,
            _memory is not null ? new Forge.Core.Workflow.WorkflowResolver(_memory) : null,
            _memory, _projectFactory);
        NowEndpoints.MapNowEndpoints(_app, _issues, _specs, _sprints, _memory, _agentRuns, _projectFactory);
        if (_agentRuns is not null)
        {
            AgentRunEndpoints.MapAgentRunEndpoints(_app, _agentRuns, _projectFactory);
        }
        AgentsEndpoints.MapAgentsEndpoints(_app, new Agents.RoleAgentRegistry(),
            _llmConfig, _roleModelOverrides, _slots, _agentRuns, _projectFactory,
            _providerApiKeys);
        QueueEndpoints.MapQueueEndpoints(_app, _issues, _sprints, _projectFactory,
            _slots, _llmConfig, _roleModelOverrides, _modelRateLimits);

if (_groomerRuns is not null)
            {
                GroomerEndpoints.MapGroomerEndpoints(_app, _groomerRuns, _logger);
                if (_designerFactory is not null && _designerRuns is not null && _designArtifacts is not null)
                {
                    DesignerEndpoints.MapDesignerEndpoints(
                        _app, _specs, _designerFactory, _designerRuns, _designArtifacts, _logger);
                }
                if (_artistFactory is not null && _artistRuns is not null && _artOutputs is not null && _meshy is not null)
                {
                    ArtistEndpoints.MapArtistEndpoints(
                        _app, _specs, _artistFactory, _artistRuns, _artOutputs, _meshy, _logger);
                }
                if (_recoveryReports is not null && _startupRecovery is not null)
                {
                    RecoveryEndpoints.MapRecoveryEndpoints(
                        _app, _issues, _recoveryReports, _startupRecovery, _logger);
                }
                if (_costTracker is not null)
                {
                    CostEndpoints.MapCostEndpoints(_app, _costTracker, _logger);
                    OpsEndpoints.MapOpsEndpoints(_app, _costTracker, _headroom, _logger);
                }
                else
                {
                    OpsEndpoints.MapOpsEndpoints(_app, null, _headroom, _logger);
                }
            }

            if (_designArtifacts is not null && _artOutputs is not null)
            {
                DesignArtEndpoints.MapDesignArtEndpoints(
                    _app, _designArtifacts, _artOutputs, _designerRuns, _artistRuns, _logger);
            }

        if (_codebaseBuilderOverride is not null && _codebaseCacheOverride is not null)
        {
            CodebaseGraphEndpoints.MapCodebaseGraphEndpoints(_app, _codebaseBuilderOverride, _codebaseCacheOverride, _issues, _logger);

            if (_sprintPropose is not null && _sprintProposalAudit is not null)
            {
                SprintProposeEndpoints.MapSprintProposeEndpoints(_app, _sprintPropose, _sprintProposalAudit, _logger, _projectFactory, _issues);
            }

            TaskEndpoints.MapTaskEndpoints(_app, _issues, _messageBus, _startupRecovery, _logger, _projectFactory, _sprints, _agentRuns,
                _memory is not null ? new Forge.Core.Workflow.WorkflowResolver(_memory) : null,
                _lifecycle, _gitHubForProject);
        }

        _app.MapBuildInfoEndpoint();

        _app.MapGet("/api/agents", () =>
        {
            var registry = new Agents.RoleAgentRegistry();
            var roles = registry.SupportedTypes
                .Select(t => new { type = t.ToString(), role = registry.ForType(t) })
                .ToArray();
            return Results.Json(roles, DashboardJson.Options);
        });

_app.MapGet("/api/events", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var reader = _bus.Subscribe();
            try
            {
                var lastHeartbeat = DateTime.UtcNow;
                await foreach (var ev in reader.ReadAllAsync(ctx.RequestAborted))
                {
                    var json = JsonSerializer.Serialize(ev, DashboardJson.Options);
                    await ctx.Response.WriteAsync($"event: {SanitizeEventName(ev.Kind)}\n", ctx.RequestAborted);
                    await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

                    if ((DateTime.UtcNow - lastHeartbeat).TotalSeconds >= 25)
                    {
                        lastHeartbeat = DateTime.UtcNow;
                        await ctx.Response.WriteAsync("event: heartbeat\n", ctx.RequestAborted);
                        await ctx.Response.WriteAsync($"data: {{\"ts\":\"{lastHeartbeat:O}\"}}\n\n", ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

_app.MapForgeUI();

        var runTask = _app.RunAsync(cancellationToken);
        await Task.Delay(100, cancellationToken);
        _port = _options.Port;
        _logger.LogInformation("Dashboard listening on {Url}", BaseUrl);
    }

    private string ResolveBaseUrl()
    {
        // The public-facing URL the ForgeUI sees. We replace the
        // "any" address (0.0.0.0 / *) with the configured Hostname
        // so the rendered HTML links point at a real address.
        if (_options.Kestrel?.Endpoints is { Count: > 0 })
        {
            if (_options.Kestrel.Endpoints.TryGetValue("https", out var h) && h.Url is { Length: > 0 } hu)
                return hu.Replace("0.0.0.0", _options.Hostname).Replace("*", _options.Hostname);
            if (_options.Kestrel.Endpoints.TryGetValue("http", out var p) && p.Url is { Length: > 0 } pu)
                return pu.Replace("0.0.0.0", _options.Hostname).Replace("*", _options.Hostname);
        }
        return $"http://{_options.Hostname}:{_options.Port}";
    }

    /// <summary>
    /// The HttpClient BaseAddress the Blazor <c>AppShellClient</c>
    /// + <c>ProjectsClient</c> + ... use for their server-side HTTP
    /// calls. Must be a loopback-style address — 0.0.0.0 / *
    /// don't work as outgoing host names, and the public IP only
    /// works if the C# runtime is on the same network. Loopback is
    /// always reachable from in-process code regardless of which
    /// interface the dashboard binds.
    /// </summary>
    private string ResolveLocalBaseAddress()
    {
        var publicUrl = ResolveBaseUrl();
        // 0.0.0.0 + * both meaning "any" — the dashboard listens on
        // loopback, so route in-process calls there.
        return publicUrl.Replace("0.0.0.0", "127.0.0.1").Replace("*", "127.0.0.1");
    }

    private static int ParsePort(string url)
    {
        // Accepts "http://0.0.0.0:443" / "https://0.0.0.0:80" / "http://*:8080".
        var idx = url.LastIndexOf(':');
        if (idx < 0 || idx == url.Length - 1) return 0;
        var seg = url[(idx + 1)..];
        // Strip any trailing slash.
        var slash = seg.IndexOf('/');
        if (slash > 0) seg = seg[..slash];
        return int.TryParse(seg, out var p) ? p : 0;
    }

    private static string SanitizeEventName(string kind)
        => kind.Replace('.', '-').Replace('/', '-');

private static Dictionary<string, object> ParseMetadata(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json, DashboardJson.Options)
                   ?? new();
        }
        catch
        {
            return new();
        }
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        try { await _app.StopAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) { _logger.LogWarning(ex, "Dashboard stop error"); }
        await _app.DisposeAsync();
        _app = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

internal static class DashboardJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}


