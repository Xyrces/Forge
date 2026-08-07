using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Codebase;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Tests.Integration.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Forge.Tests.Integration;

/// <summary>
/// DesignerEndpoints integration tests: GET /api/designer/runs and
/// GET /api/specs/{id}/design-artifacts are simple SELECTs over the
/// v9 tables; POST /api/specs/{id}/design kicks off a manual run.
///
/// <para>
/// We test the read paths end-to-end (real Kestrel + real DB). The
/// manual run path is covered by DesignerAgentTests + the live
/// demo; the endpoint is a thin fire-and-forget wrapper around
/// the agent, and the existing test coverage proves the agent
/// behavior.
/// </para>
/// </summary>
public class DesignerEndpointTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;
    private readonly DesignArtifactStore _artifacts;
    private readonly DesignerRunStore _runs;
    private readonly InMemoryDashboardEventBus _events;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public DesignerEndpointTests(ITestOutputHelper output)
    {
        _out = output;
        _workDir = TempRoot.Instance.NewDirectory("dendpoints");
        Directory.CreateDirectory(_workDir);
        InitRepo(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _ = new IssueStore(_dbPath);
        _issues = new IssueStore(_dbPath);
        _specs = new SpecStore(_issues);
        _artifacts = new DesignArtifactStore(_dbPath);
        _runs = new DesignerRunStore(_dbPath);
        _events = new InMemoryDashboardEventBus();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _workDir,
            ApplicationName = "Forge.Tests",
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        DesignerEndpoints.MapDesignerEndpoints(app, _specs, designerFactory: null, _runs, _artifacts, NullLogger<DashboardHost>.Instance);
        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        _issues.Dispose();
        _specs.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static void InitRepo(string dir)
    {
        Run("git", "init -q -b main", dir);
        Run("git", "config user.email t@t", dir);
        Run("git", "config user.name T", dir);
        var coreDir = Path.Combine(dir, "PortHorizon.Client");
        Directory.CreateDirectory(coreDir);
        File.WriteAllText(Path.Combine(coreDir, "PortHorizon.Client.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(coreDir, "Program.cs"), "class Program { }");
        File.WriteAllText(Path.Combine(dir, "README.md"), "x");
        Run("git", "add .", dir);
        Run("git", "commit -q -m init", dir);
    }

    private static void Run(string exe, string args, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, Arguments = args, WorkingDirectory = cwd,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    private static int GetEphemeralPort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task<SpecRecord> CreateReadySpecAsync(string title)
    {
        var spec = await _specs.CreateAsync(new NewSpec("PortHorizon", title, "## Summary\nx\n\n## Acceptance criteria\n- [ ] a\n\n## Touches\n- PortHorizon.Client\n\n## Dependencies\n- none\n"));
        await _specs.SetStatusAsync(spec.Id, SpecStatus.ReadyForDesign);
        return (await _specs.GetAsync(spec.Id))!;
    }

    [Fact]
    public async Task GetDesignerRuns_ReturnsRunsForSpec()
    {
        var spec = await CreateReadySpecAsync("Test");
        // Write a run record directly.
        var run = await _runs.StartAsync(spec.Id, DesignerTriggerKind.Manual, default);
        await _runs.FinishAsync(run.Id, DesignerRunStatus.Succeeded,
            SpecStatus.Designed, new[] { "design-1" }, null, null,
            TimeSpan.FromMilliseconds(100), default);

        var resp = await _client.GetAsync($"/api/designer/runs?specId={spec.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, arr.GetArrayLength());
        var first = arr[0];
        Assert.Equal(spec.Id, first.GetProperty("specId").GetString());
        Assert.Equal("succeeded", first.GetProperty("status").GetString());
        Assert.Equal("designed", first.GetProperty("newSpecStatus").GetString());
        var artifactIds = first.GetProperty("designArtifactIds");
        Assert.Equal(1, artifactIds.GetArrayLength());
        Assert.Equal("design-1", artifactIds[0].GetString());
    }

    [Fact]
    public async Task GetDesignArtifacts_ReturnsArtifactsForSpec()
    {
        var spec = await CreateReadySpecAsync("Test");
        await _artifacts.CreateAsync(new NewDesignArtifact(
            SpecId: spec.Id,
            Kind: DesignArtifactKind.Wireframe,
            Title: "Inventory HUD wireframe",
            Body: "<html><body>Inventory HUD</body></html>",
            BodyKind: "html"));
        await _artifacts.CreateAsync(new NewDesignArtifact(
            SpecId: spec.Id,
            Kind: DesignArtifactKind.ComponentSpec,
            Title: "Item row",
            Body: "| col | type |\n|---|---|\n| icon | string |",
            BodyKind: "markdown"));

        var resp = await _client.GetAsync($"/api/specs/{spec.Id}/design-artifacts");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, arr.GetArrayLength());
        var kinds = arr.EnumerateArray()
            .Select(a => a.GetProperty("kind").GetString())
            .OrderBy(s => s)
            .ToArray();
        Assert.Equal(new[] { "componentspec", "wireframe" }, kinds);
    }

    [Fact]
    public async Task GetDesignArtifacts_SpecWithNoArtifacts_ReturnsEmptyArray()
    {
        var spec = await CreateReadySpecAsync("Empty");
        var resp = await _client.GetAsync($"/api/specs/{spec.Id}/design-artifacts");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, arr.GetArrayLength());
    }

    [Fact]
    public async Task GetDesignerRuns_NoSpecIdFilter_ReturnsAll()
    {
        var spec1 = await CreateReadySpecAsync("S1");
        var spec2 = await CreateReadySpecAsync("S2");
        var r1 = await _runs.StartAsync(spec1.Id, DesignerTriggerKind.Manual, default);
        var r2 = await _runs.StartAsync(spec2.Id, DesignerTriggerKind.Scheduled, default);
        await _runs.FinishAsync(r1.Id, DesignerRunStatus.Succeeded, SpecStatus.Designed, null, null, null, TimeSpan.FromMilliseconds(10), default);
        await _runs.FinishAsync(r2.Id, DesignerRunStatus.LlmFailed, null, null, null, "boom", TimeSpan.FromMilliseconds(20), default);

        var resp = await _client.GetAsync("/api/designer/runs");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, arr.GetArrayLength());
    }
}
