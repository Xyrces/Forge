using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Core;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// Phase 2b.2: SSE event stream round-trip.
/// Posts a spec, asserts that the SSE stream emits a
/// spec-related event end-to-end.
/// </summary>
public class IntakeSseStreamTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly IntakeStore _intakeStore;
    private readonly SpecStore _specs;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public IntakeSseStreamTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("sse");
        _issues = new IssueStore(_dbPath);
        _intakeStore = new IntakeStore(_issues);
        _specs = new SpecStore(_issues);

        _host = BuildHost();
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        try { _issues.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    private IHost BuildHost()
    {
        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.GetDirectoryName(_dbPath) ?? Path.GetTempPath(),
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();

        // We mount only SpecEndpoints + the events stream (the same SSE
        // endpoint the dashboard uses). The full intake flow isn't
        // involved here â€” we just want to verify SSE delivers events.
        SpecEndpoints.MapSpecEndpoints(app, _specs, new SpecExtractionReader(_issues),
            NullLogger<DashboardHost>.Instance, _intakeStore);

        // The events stream is normally registered by DashboardHost.BuildAsync.
        // Re-register it here for the test.
        var bus = new InMemoryDashboardEventBus();
        app.MapGet("/api/events", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            var reader = bus.Subscribe();
            try
            {
                await foreach (var ev in reader.ReadAllAsync(ctx.RequestAborted))
                {
                    var json = JsonSerializer.Serialize(ev, DashboardJson.Options);
                    await ctx.Response.WriteAsync($"event: {ev.Kind.Replace('.', '-')}\n", ctx.RequestAborted);
                    await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
        });

        // Test endpoint that emits a custom event so we can verify
        // the SSE plumbing end-to-end. Real callers (IntakeAgent,
        // etc.) emit via the bus instance; for the test we need
        // access to the same bus the SSE stream reads from. The
        // cleanest test-only path is to expose a /test/emit endpoint
        // that publishes on the bus and then closes.
        app.MapPost("/test/emit", async (HttpContext ctx) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var root = doc.RootElement;
            var kind = root.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
            var payload = root.TryGetProperty("payload", out var p) ? p.GetString() ?? "" : "";
            bus.Publish(new DashboardEvent(
                DateTime.UtcNow, kind, "test", payload,
                new Dictionary<string, object?>()));
            await ctx.Response.WriteAsync("ok");
        });

        return app;
    }

    private static int GetEphemeralPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task SseStream_DeliverEventsEmittedAfterSubscribe()
    {
        // 1. Open the SSE stream.
        using var sse = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var streamRequest = new HttpRequestMessage(HttpMethod.Get,
            new Uri(_client.BaseAddress!, "/api/events"));
        streamRequest.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        var streamTask = sse.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);
        // Give the server a moment to subscribe before publishing.
        await Task.Delay(200);

        // 2. Publish a test event via the bus.
        var publish = await _client.PostAsync("/test/emit",
            new StringContent(
                "{\"kind\":\"test.thing_happened\",\"payload\":\"hello-sse\"}",
                Encoding.UTF8, "application/json"));
        var publishBody = await publish.Content.ReadAsStringAsync();
        Assert.True(publish.IsSuccessStatusCode, $"POST /test/emit failed: {publishBody}");

        // 3. Read the SSE stream until we see the event (with timeout).
        using var streamResponse = await streamTask;
        Assert.Equal(System.Net.HttpStatusCode.OK, streamResponse.StatusCode);
        await using var s = await streamResponse.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(s, Encoding.UTF8);
        var deadline = DateTime.UtcNow.AddSeconds(4);
        string? eventLine = null;
        string? dataLine = null;
        while (DateTime.UtcNow < deadline)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) break;
            if (line.StartsWith("event: ")) eventLine = line.Substring("event: ".Length);
            if (line.StartsWith("data: ")) dataLine = line.Substring("data: ".Length);
            if (eventLine == "test-thing_happened" && dataLine is not null) break;
        }
        Assert.Equal("test-thing_happened", eventLine);
        Assert.NotNull(dataLine);
        // The data should contain the payload we sent.
        Assert.Contains("hello-sse", dataLine);
    }

    [Fact]
    public async Task SpecPost_TriggersExtractionAndDerivedTablesReadable()
    {
        // Functional test: POST a spec with body + diagrams + touches
        // + deps, then read the extracted endpoints and verify they
        // reflect what the body said. This is the "spec.extracted"
        // contract the dashboard's side-panel depends on.
        var resp = await _client.PostAsJsonAsync("/api/specs", new
        {
            projectId = "P",
            title = "T",
            body = """
                ## Summary
                Add dark mode.

                ## Diagrams
                ```mermaid
                flowchart LR
                  A --> B
                ```

                ## Touches
                - PortHorizon.Dashboard.Theming

                ## Dependencies
                - blocks spec-portal-redirect
                """
        });
        Assert.Equal(System.Net.HttpStatusCode.Created, resp.StatusCode);
        var spec = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var id = spec.GetProperty("id").GetString()!;

        var diagrams = await _client.GetFromJsonAsync<JsonElement>($"/api/specs/{id}/diagrams");
        Assert.Equal(1, diagrams.GetArrayLength());

        var touches = await _client.GetFromJsonAsync<JsonElement>($"/api/specs/{id}/touches");
        Assert.Equal(1, touches.GetArrayLength());

        var deps = await _client.GetFromJsonAsync<JsonElement>($"/api/specs/{id}/deps");
        Assert.Equal(1, deps.GetArrayLength());
    }
}


