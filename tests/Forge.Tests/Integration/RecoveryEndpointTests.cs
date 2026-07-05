using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.AgentTools;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Xunit;
using Xunit.Abstractions;
using PullRequest = Octokit.PullRequest;

namespace Forge.Tests.Integration;

/// <summary>
/// RecoveryEndpoints integration tests. End-to-end coverage of
/// the four endpoints that surface StartupRecovery to the
/// operator:
///   - GET /api/recovery/reports
///   - GET /api/recovery/reports/{id}
///   - POST /api/recovery/run (side-effects)
///   - POST /api/recovery/dry-run
/// </summary>
public class RecoveryEndpointTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly RecoveryReportStore _reports;
    private readonly GitWorktreeService _worktrees;
    private readonly InMemoryDashboardEventBus _events;
    private readonly StubGitHub _gitHub;
    private readonly StartupRecovery _recovery;
    private readonly IHost _host;
    private readonly HttpClient _client;

    public RecoveryEndpointTests(ITestOutputHelper output)
    {
        _out = output;
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-recovery-ep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        InitRepo(_workDir);
        _dbPath = Path.Combine(_workDir, "issues.db");
        _ = new IssueStore(_dbPath);
        _issues = new IssueStore(_dbPath);
        _reports = new RecoveryReportStore(_dbPath);
        _worktrees = new GitWorktreeService(
            new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            NullLogger<GitWorktreeService>.Instance);
        _events = new InMemoryDashboardEventBus();
        _gitHub = new StubGitHub();
        _recovery = new StartupRecovery(_issues, _reports, _worktrees, _gitHub, _events,
            NullLogger<StartupRecovery>.Instance);

        var port = GetEphemeralPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(NullLoggerProvider.Instance);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        RecoveryEndpoints.MapRecoveryEndpoints(app, _issues, _reports, _recovery,
            NullLogger<DashboardHost>.Instance);
        _host = app;
        _host.Start();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_host.GetPort()}/") };
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        _issues.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private sealed class StubGitHub : IGitHubRecovery
    {
        public int CallCount;
        public int PrNumber = 999;
        public Task<PullRequest> CreatePullRequestAsync(
            string title, string body, string headBranch, string baseBranch,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new PullRequest(PrNumber));
        }
    }

    private static void InitRepo(string dir)
    {
        Run("git", "init -q -b main", dir);
        Run("git", "config user.email t@t", dir);
        Run("git", "config user.name T", dir);
        File.WriteAllText(Path.Combine(dir, "README.md"), "x");
        Run("git", "add .", dir);
        Run("git", "commit -q -m init", dir);
        var bare = Path.Combine(dir, ".remote.git");
        Run("git", $"init --bare -q {bare}", dir);
        Run("git", $"remote add origin {bare}", dir);
        Run("git", "push -q -u origin main", dir);
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

    /// <summary>
    /// Seed an issue in a particular checkpoint state. Mirrors
    /// StartupRecoveryTests.SeedIssueAsync; inlined here to keep
    /// the test fixtures independent.
    /// </summary>
    private async Task<string> SeedAsync(DispatchCheckpoint target)
    {
        var issue = await _issues.CreateAsync(new NewIssue(Type: "task", Title: "x"));
        await _issues.ClaimAsync(issue.Id, "kilo");
        await _worktrees.CreateAsync(issue.Id, "main");
        var wp = _worktrees.WorktreePathFor(issue.Id);
        await _issues.TransitionAsync(issue.Id, IssueStatus.InProgress, error: null,
            metadata: new Dictionary<string, object>
            {
                ["worktreePath"] = wp,
                ["branch"] = $"agent/{issue.Id}",
            });
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.WorktreeAcquired);

        if (target == DispatchCheckpoint.WorktreeAcquired) return issue.Id;

        File.WriteAllText(Path.Combine(wp, "edit.txt"), "agent output");
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.AgentCompleted);
        if (target == DispatchCheckpoint.AgentCompleted) return issue.Id;

        await _worktrees.CommitAllAsync(wp, $"Task({issue.Id}): x");
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.CommitDone);
        if (target == DispatchCheckpoint.CommitDone) return issue.Id;

        await _worktrees.PushAsync(wp, $"agent/{issue.Id}");
        await _issues.SetCheckpointAsync(issue.Id, DispatchCheckpoint.PushDone);
        return issue.Id;
    }

    [Fact]
    public async Task GetReports_ReturnsRecentRows()
    {
        // Seed two reports directly via the store.
        var r1 = await _reports.StartAsync(specId: null);
        await _reports.FinishAsync(r1.Id, 3, 1, 0, Array.Empty<RecoveryActionRecord>(), TimeSpan.FromMilliseconds(50));
        var r2 = await _reports.StartAsync(specId: "spec-x");
        await _reports.FinishAsync(r2.Id, 5, 2, 1, Array.Empty<RecoveryActionRecord>(), TimeSpan.FromMilliseconds(80));

        var resp = await _client.GetAsync("/api/recovery/reports");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var arr = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, arr.GetArrayLength());
        // Most recent first.
        Assert.Equal("spec-x", arr[0].GetProperty("specId").GetString());
    }

    [Fact]
    public async Task GetReports_LimitQueryParam_IsRespected()
    {
        for (var i = 0; i < 5; i++)
        {
            var r = await _reports.StartAsync(specId: null);
            await _reports.FinishAsync(r.Id, 0, 0, 0, Array.Empty<RecoveryActionRecord>(), TimeSpan.Zero);
        }
        var resp = await _client.GetAsync("/api/recovery/reports?limit=2");
        var arr = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, arr.GetArrayLength());
    }

    [Fact]
    public async Task GetReportById_Missing_Returns404()
    {
        var resp = await _client.GetAsync("/api/recovery/reports/9999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PostRun_RunsRecoveryAndReturnsReportId()
    {
        await SeedAsync(DispatchCheckpoint.PushDone);
        await SeedAsync(DispatchCheckpoint.AgentCompleted);
        var resp = await _client.PostAsync("/api/recovery/run", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(payload.GetProperty("reportId").GetInt64() > 0);
        var summary = payload.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("issuesScanned").GetInt32());
        Assert.Equal(2, summary.GetProperty("issuesReplayed").GetInt32());
        Assert.Equal(2, _gitHub.CallCount);
    }

    [Fact]
    public async Task PostDryRun_ClassifiesWithoutSideEffects()
    {
        await SeedAsync(DispatchCheckpoint.PushDone);
        await SeedAsync(DispatchCheckpoint.WorktreeAcquired);
        var resp = await _client.PostAsync("/api/recovery/dry-run", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var payload = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, payload.GetProperty("scanned").GetInt32());
        var decisions = payload.GetProperty("decisions");
        Assert.Equal(2, decisions.GetArrayLength());
        // Verify the GitHub stub wasn't called — dry-run must not
        // produce side effects.
        Assert.Equal(0, _gitHub.CallCount);
    }
}