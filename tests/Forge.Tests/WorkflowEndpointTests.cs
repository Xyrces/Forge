using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Core;
using Forge.Core.Workflow;
using Forge.Dashboard;
using Forge.Tests.Integration;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Editable workflow HTTP endpoints: draft -> publish -> versions ->
/// restore round-trips through the real wiring against a fresh
/// per-test DB, plus publish validation and snapshot pruning.
/// </summary>
public class WorkflowEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dbPath;
    private readonly MemoryStore _memory;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public WorkflowEndpointTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("wf-api");
        _ = new IssueStore(_dbPath);
        _memory = new MemoryStore(_dbPath);

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
        WorkflowEndpoints.MapWorkflowEndpoints(app, _memory, new InMemoryDashboardEventBus(),
            NullLogger<DashboardHost>.Instance);
        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task DraftPublishRestore_RoundTrip()
    {
        // Initial: no draft, live resolves to the default.
        var initial = await _client.GetFromJsonAsync<JsonElement>("/api/workflow");
        Assert.False(initial.GetProperty("hasDraft").GetBoolean());
        Assert.Equal(13, initial.GetProperty("live").GetProperty("steps").GetArrayLength());

        // Draft: detach the merge gate from review.
        var draft = JsonSerializer.SerializeToElement(WorkflowDefaults.Definition with
        {
            Steps = WorkflowDefaults.Definition.Steps
                .Select(s => s.Id == "review" ? s with { Gates = Array.Empty<string>() } : s)
                .ToList(),
        }, Json);
        var put = await _client.PutAsync("/api/workflow/draft",
            new StringContent(draft.GetRawText(), System.Text.Encoding.UTF8, "application/json"));
        put.EnsureSuccessStatusCode();
        var putBody = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(putBody.GetProperty("diff").EnumerateArray().Select(x => x.GetString()),
            l => l!.Contains("detached"));

        // Publish.
        var publish = await _client.PostAsync("/api/workflow/publish", null);
        publish.EnsureSuccessStatusCode();
        var live = await _client.GetFromJsonAsync<JsonElement>("/api/workflow");
        Assert.False(live.GetProperty("hasDraft").GetBoolean());
        var review = live.GetProperty("live").GetProperty("steps").EnumerateArray()
            .First(s => s.GetProperty("id").GetString() == "review");
        Assert.Equal(0, review.GetProperty("gates").GetArrayLength());

        // The snapshot of "was default" exists; restore returns to default.
        var versions = await _client.GetFromJsonAsync<JsonElement>("/api/workflow/versions");
        var first = versions.EnumerateArray().First();
        Assert.True(first.GetProperty("isDefaultSnapshot").GetBoolean());
        var restore = await _client.PostAsJsonAsync("/api/workflow/versions/restore",
            new { key = first.GetProperty("key").GetString() });
        restore.EnsureSuccessStatusCode();
        var restored = await _client.GetFromJsonAsync<JsonElement>("/api/workflow");
        var reviewAfter = restored.GetProperty("live").GetProperty("steps").EnumerateArray()
            .First(s => s.GetProperty("id").GetString() == "review");
        Assert.Equal(1, reviewAfter.GetProperty("gates").GetArrayLength());
    }

    [Fact]
    public async Task Publish_NoDraft_400()
    {
        var resp = await _client.PostAsync("/api/workflow/publish", null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Publish_ValidationFailure_LiveUntouched()
    {
        var bad = WorkflowDefaults.Definition with
        {
            Steps = WorkflowDefaults.Definition.Steps
                .Append(new WorkflowStep("teleport", "Teleport", "implementation", "stage", false, true, 0, 0, Array.Empty<string>()))
                .ToList(),
        };
        var put = await _client.PutAsync("/api/workflow/draft",
            new StringContent(JsonSerializer.Serialize(bad, Json), System.Text.Encoding.UTF8, "application/json"));
        put.EnsureSuccessStatusCode();   // drafts may be invalid; publish is the gate

        var resp = await _client.PostAsync("/api/workflow/publish", null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", body.GetProperty("error").GetString());
        Assert.Contains(body.GetProperty("errors").EnumerateArray().Select(x => x.GetString()),
            e => e!.Contains("unknown step 'teleport'"));

        // Live still resolves to the default.
        var live = await new WorkflowResolver(_memory).ResolveAsync();
        Assert.Same(WorkflowDefaults.Definition, live);
    }

    [Fact]
    public async Task PutDraft_Unparseable_400()
    {
        var resp = await _client.PutAsync("/api/workflow/draft",
            new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task DiscardDraft_ClearsHasDraft()
    {
        await _client.PutAsync("/api/workflow/draft",
            new StringContent(JsonSerializer.Serialize(WorkflowDefaults.Definition, Json),
                System.Text.Encoding.UTF8, "application/json"));
        var del = await _client.DeleteAsync("/api/workflow/draft");
        del.EnsureSuccessStatusCode();
        var wf = await _client.GetFromJsonAsync<JsonElement>("/api/workflow");
        Assert.False(wf.GetProperty("hasDraft").GetBoolean());
    }

    [Fact]
    public async Task Versions_PrunedToTen()
    {
        for (var i = 0; i < 12; i++)
        {
            await _client.PutAsync("/api/workflow/draft",
                new StringContent(JsonSerializer.Serialize(WorkflowDefaults.Definition, Json),
                    System.Text.Encoding.UTF8, "application/json"));
            var pub = await _client.PostAsync("/api/workflow/publish", null);
            pub.EnsureSuccessStatusCode();
        }
        var versions = await _client.GetFromJsonAsync<JsonElement>("/api/workflow/versions");
        Assert.True(versions.GetArrayLength() <= 10, $"expected <=10 versions, got {versions.GetArrayLength()}");
    }

    [Fact]
    public async Task Restore_UnknownKey_400()
    {
        var resp = await _client.PostAsJsonAsync("/api/workflow/versions/restore",
            new { key = "workflow/versions/999" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static int GetEphemeralPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
