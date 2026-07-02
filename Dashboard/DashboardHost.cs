using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Agents;
using PortHorizon.Agents.Codebase;
using PortHorizon.Agents.Configuration;
using PortHorizon.Agents.Core;
using PortHorizon.Agents.Orchestrator;

namespace PortHorizon.Agents.Dashboard;

public sealed class DashboardHost : IAsyncDisposable
{
    private readonly DashboardOptions _options;
    private readonly IIssueStore _issues;
    private readonly IAgentStore _agents;
    private readonly ISkillStore _skills;
    private readonly ISprintStore _sprints;
    private readonly IIntakeStore _intakeStore;
    private readonly IntakeAgentRegistry? _intakeRegistry;
    private readonly ISpecStore _specs;
    private readonly Agents.GroomerAgentFactory? _groomerFactory;
    private readonly MemoryStore? _memory;
    private readonly string? _issuesJsonlPath;
    private readonly VisionStore? _vision;
    private readonly ISpecExtractionReader? _extractorOverride;
    private readonly ICodebaseGraphBuilder? _codebaseBuilderOverride;
    private readonly ICodebaseGraphCacheStore? _codebaseCacheOverride;
    private readonly AgentMessageBus _messageBus;
    private readonly InMemoryDashboardEventBus _bus;
    private readonly ILogger<DashboardHost> _logger;
    private WebApplication? _app;
    private int _port;

    public DashboardHost(
        DashboardOptions options,
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
        string? issuesJsonlPath = null,
        VisionStore? vision = null,
        ISpecExtractionReader? extractor = null,
        ICodebaseGraphBuilder? codebaseBuilder = null,
        ICodebaseGraphCacheStore? codebaseCache = null)
    {
        _options = options;
        _issues = issues;
        _agents = agents;
        _skills = skills;
        _sprints = sprints;
        _intakeStore = intakeStore ?? new NullIntakeStore();
        _intakeRegistry = intakeRegistry;
        _specs = specs ?? new NullSpecStore();
        _groomerFactory = groomerFactory;
        _memory = memory;
        _issuesJsonlPath = issuesJsonlPath;
        _vision = vision;
        _extractorOverride = extractor;
        _codebaseBuilderOverride = codebaseBuilder;
        _codebaseCacheOverride = codebaseCache;
        _messageBus = messageBus;
        _bus = bus;
        _logger = logger;
    }

    public string BaseUrl => $"http://{_options.Hostname}:{_port}";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Dashboard disabled by config");
            return;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        _app = builder.Build();
        _app.Urls.Clear();
        _app.Urls.Add($"http://{_options.Hostname}:{_options.Port}");

_app.MapGet("/api/state", async (CancellationToken ct) =>
        {
            try
            {
                var tasks = await _issues.ListAsync(new IssueFilter(), ct);
                var activeSprint = await _sprints.GetActiveAsync(ct);
                var agents = await _agents.ListAsync(ct);
                var skills = await _skills.ListAsync(null, globalOnly: false, ct);
                var sprints = await _sprints.ListAsync(activeOnly: false, ct);
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
                        parameters = ParseMetadata(t.MetadataJson)
                    }).ToArray(),
                    agents = agents.Select(a => new
                    {
                        id = a.Id,
                        kiloName = a.KiloName,
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
                    sprints = sprints.Select(s => new
                    {
                        id = s.Id,
                        name = s.Name,
                        goal = s.Goal,
                        startDate = s.StartDate,
                        endDate = s.EndDate,
                        status = s.Status.ToString(),
                        createdAt = s.CreatedAt,
                        updatedAt = s.UpdatedAt
                    }).ToArray(),
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

        DashboardEndpoints.MapP1Endpoints(_app, _issues, _agents, _skills, _sprints, _messageBus, _logger);

        if (_intakeRegistry is not null)
        {
            IntakeEndpoints.MapIntakeEndpoints(_app, _intakeRegistry, _issues, _sprints, _intakeStore, _logger);
        }

        SpecEndpoints.MapSpecEndpoints(_app, _specs, _extractorOverride ?? new NullSpecExtractionReader(), _logger, _intakeStore, _groomerFactory);

        if (_memory is not null)
        {
            MemoryEndpoints.MapMemoryEndpoints(_app, _memory, _logger);
        }

        if (_issuesJsonlPath is not null)
        {
            IssuesJsonlEndpoints.MapIssuesJsonlEndpoints(_app, _issuesJsonlPath, _logger);
        }

        if (_vision is not null)
        {
            VisionEndpoints.MapVisionEndpoints(_app, _vision, _logger);
        }

        if (_codebaseBuilderOverride is not null && _codebaseCacheOverride is not null)
        {
            CodebaseGraphEndpoints.MapCodebaseGraphEndpoints(_app, _codebaseBuilderOverride, _codebaseCacheOverride, _issues, _logger);
        }

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
                await foreach (var ev in reader.ReadAllAsync(ctx.RequestAborted))
                {
                    var json = JsonSerializer.Serialize(ev, DashboardJson.Options);
                    await ctx.Response.WriteAsync($"event: {SanitizeEventName(ev.Kind)}\n", ctx.RequestAborted);
                    await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
        });

        _app.MapGet("/", () => Results.Content(GetEmbeddedHtml(), "text/html; charset=utf-8"));
        _app.MapGet("/index.html", () => Results.Content(GetEmbeddedHtml(), "text/html; charset=utf-8"));

        _app.MapFallback(() => Results.Redirect("/index.html"));

        var runTask = _app.RunAsync(cancellationToken);
        await Task.Delay(100, cancellationToken);
        _port = _options.Port;
        _logger.LogInformation("Dashboard listening on {Url}", BaseUrl);
    }

    private static string SanitizeEventName(string kind)
        => kind.Replace('.', '-').Replace('/', '-');

    private static readonly Lazy<string> _embeddedHtml = new(() =>
    {
        var asm = typeof(DashboardHost).Assembly;
        using var stream = asm.GetManifestResourceStream("PortHorizon.Agents.Dashboard.wwwroot.index.html")
            ?? throw new InvalidOperationException("Embedded HTML resource missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static string GetEmbeddedHtml() => _embeddedHtml.Value;

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


