using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortHorizon.Agents.Configuration;
using PortHorizon.Agents.Core;

namespace PortHorizon.Agents.Dashboard;

public sealed class DashboardHost : IAsyncDisposable
{
    private readonly DashboardOptions _options;
    private readonly StateStore _state;
    private readonly InMemoryDashboardEventBus _bus;
    private readonly ILogger<DashboardHost> _logger;
    private WebApplication? _app;
    private int _port;

    public DashboardHost(DashboardOptions options, StateStore state, InMemoryDashboardEventBus bus, ILogger<DashboardHost> logger)
    {
        _options = options;
        _state = state;
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
                var state = await _state.LoadStateAsync(ct);
                return Results.Json(state, DashboardJson.Options);
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: 503);
            }
        });

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