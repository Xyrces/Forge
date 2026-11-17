using System.Text.Json;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Db;
using Forge.Core.Execution;
using Forge.Orchestrator.Workers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.Tests;

public sealed class WorkerAttemptTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "forge-worker-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly WorkerAttemptStore _store;
    public WorkerAttemptTests()
    {
        var db = ForgeDb.Sqlite(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        using var connection = db.Open();
        using var command = connection.CreateCommand(); command.CommandText = M038WorkerAttempts.SqliteDdl; command.ExecuteNonQuery();
        _store = new(db);
    }
    private static WorkerAssignment Assignment() => new("id", "dataset", "key", "forge", "task", "CoreDev", "dispatch", "abc", "/var/lib/forge/worktree", "{}", "token");

    [Fact]
    public async Task DuplicatePodCannotClaimOrPublishAndCancellationRevokesAdmission()
    {
        await _store.CreateOrGetAsync(Assignment());
        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => _store.ClaimAsync("id", "pod-" + i)));
        Assert.Equal(1, claims.Count(x => x));
        var owner = (await _store.GetAsync("id"))!.PodUid!;
        Assert.False(await _store.CompleteAsync("id", "other", "token", "{}", null));
        await _store.EnsureOwnerAsync("id", owner, "token");
        await _store.RequestCancellationAsync("id");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.EnsureOwnerAsync("id", owner, "token"));
        Assert.True(await _store.CompleteAsync("id", owner, "token", null, "cancelled"));
        Assert.Equal("cancelled", (await _store.GetAsync("id"))!.State);
        Assert.False(await _store.ClaimAsync("id", "replacement"));
    }

    [Fact]
    public async Task RecreatingAssignmentPreservesTokenAndRecordedResult()
    {
        await _store.CreateOrGetAsync(Assignment());
        await _store.ClaimAsync("id", "pod");
        await _store.CompleteAsync("id", "pod", "token", "result", null);
        var adopted = await _store.CreateOrGetAsync(Assignment() with { AttemptToken = "other" });
        Assert.Equal("token", adopted.AttemptToken);
        Assert.Equal("result", adopted.ResultJson);
        Assert.False(await _store.MarkJobFailedAsync("id", "late failure"));
    }

    [Fact]
    public async Task ReplacementControllerReusesCompletedAttemptWithoutCreatingAnotherJob()
    {
        var jobs = new CompletingJobs(_store);
        var context = new Dictionary<string, object> { ["projectId"] = "forge", ["issueId"] = "task", ["dispatchId"] = "d-1",
            ["worktreePath"] = Path.GetFullPath("."), ["sourceSha"] = new string('a', 40) };
        var first = await new KubernetesAgentRunner(_store, jobs, "dataset").RunAsync(AgentType.CoreDev, "work", null, context, default);
        var second = await new KubernetesAgentRunner(_store, jobs, "dataset").RunAsync(AgentType.CoreDev, "work", null, context, default);
        Assert.Equal(first, second); Assert.Equal(1, jobs.Created);
    }

    [Fact]
    public async Task CancellingControllerWaitDoesNotCancelDurableWorker()
    {
        using var stop = new CancellationTokenSource();
        var jobs = new DetachingJobs(stop);
        var context = new Dictionary<string, object> { ["projectId"] = "forge", ["issueId"] = "task", ["dispatchId"] = "d-1",
            ["worktreePath"] = Path.GetFullPath("."), ["sourceSha"] = new string('a', 40) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new KubernetesAgentRunner(_store, jobs, "dataset").RunAsync(AgentType.CoreDev, "work", null, context, stop.Token));
        var row = await _store.GetAsync(jobs.Id!);
        Assert.NotNull(row); Assert.False(row.CancelRequested); Assert.Equal("queued", row.State);
    }

    [Fact]
    public async Task RecordedResultCannotEscapeBeforePodTerminationIsConfirmed()
    {
        var jobs = new CompletingJobs(_store) { TerminationFails = true };
        var context = new Dictionary<string, object> { ["projectId"] = "forge", ["issueId"] = "task", ["dispatchId"] = "d-1",
            ["worktreePath"] = Path.GetFullPath("."), ["sourceSha"] = new string('a', 40) };
        await Assert.ThrowsAsync<WorkerExecutionUncertainException>(() =>
            new KubernetesAgentRunner(_store, jobs, "dataset").RunAsync(AgentType.CoreDev, "work", null, context, default));
        Assert.Equal(1, jobs.Created);
    }

    [Fact]
    public async Task CancellationUsesForegroundDeletionAndWaitsForAbsence()
    {
        var handler = new DeleteHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        await new KubernetesWorkerJobs(http, new WorkerOptions()).CancelAndWaitAsync("id", default);
        Assert.Equal(new[] { "DELETE", "GET" }, handler.Methods);
        Assert.Contains("Foreground", handler.DeleteBody);
    }

    [Fact]
    public void JobIsIndependentImmutableAndHasNoWorkerApiToken()
    {
        var options = new WorkerOptions { Image = "registry/forge@sha256:" + new string('a', 64) };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(KubernetesWorkerJobs.BuildManifest(Assignment(), options)));
        var root = document.RootElement;
        Assert.False(root.GetProperty("metadata").TryGetProperty("ownerReferences", out _));
        var spec = root.GetProperty("spec"); Assert.Equal(0, spec.GetProperty("backoffLimit").GetInt32());
        Assert.False(spec.GetProperty("template").GetProperty("spec").GetProperty("automountServiceAccountToken").GetBoolean());
        options.Image = "registry/forge:latest";
        Assert.Throws<InvalidOperationException>(() => KubernetesWorkerJobs.BuildManifest(Assignment(), options));
    }

    public void Dispose() => File.Delete(_path);
    private sealed class DeleteHandler : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];
        public string? DeleteBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Methods.Add(request.Method.Method);
            if (request.Method == HttpMethod.Delete)
            {
                DeleteBody = await request.Content!.ReadAsStringAsync(ct);
                return new(System.Net.HttpStatusCode.Accepted);
            }
            return new(System.Net.HttpStatusCode.NotFound);
        }
    }
    private sealed class DetachingJobs(CancellationTokenSource stop) : IWorkerJobs
    {
        public string? Id;
        public Task EnsureJobAsync(WorkerAssignment a, CancellationToken ct) { Id = a.Id; stop.Cancel(); return Task.CompletedTask; }
        public Task<string?> GetFailureAsync(string attemptId, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task CancelAndWaitAsync(string attemptId, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class CompletingJobs(WorkerAttemptStore store) : IWorkerJobs
    {
        public int Created;
        public bool TerminationFails;
        public async Task EnsureJobAsync(WorkerAssignment a, CancellationToken ct)
        {
            Created++;
            await store.ClaimAsync(a.Id, "pod", ct);
            await store.CompleteAsync(a.Id, "pod", a.AttemptToken, JsonSerializer.Serialize(new AgentRunResult("done", null, 1, 2, TimeSpan.Zero)), null, ct);
        }
        public Task<string?> GetFailureAsync(string attemptId, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task CancelAndWaitAsync(string attemptId, CancellationToken ct) => TerminationFails
            ? Task.FromException(new HttpRequestException("control plane unavailable")) : Task.CompletedTask;
    }
}
