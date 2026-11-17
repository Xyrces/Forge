using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Dashboard.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class OperatorObservationUiTests
{
    [Fact]
    public async Task SavesExactDisplayedIdentityAndHashOfNoteWithoutReadingLinkedFiles()
    {
        string? body = null; var posts = 0; var recorded = 0;
        using var f = new Fixture(async request =>
        {
            posts++;
            Assert.Equal("/api/sprints/sprint-1/completion/operator-evidence?projectId=forge", request.RequestUri!.PathAndQuery);
            body = await request.Content!.ReadAsStringAsync();
            return new(HttpStatusCode.OK);
        });
        f.Component.OnRecorded = EventCallback.Factory.Create(f, () => recorded++);
        f.Note("  Observed desktop and narrow layout. Evidence: /proof/capture.png  ");
        await f.Record(); await f.Record();
        var value = JsonDocument.Parse(body!).RootElement;
        Assert.Equal("plan", value.GetProperty("planHash").GetString());
        Assert.Equal("visual", value.GetProperty("criterionId").GetString());
        Assert.Equal(new string('a', 40), value.GetProperty("sourceSha").GetString());
        Assert.Equal("deployment", value.GetProperty("deploymentId").GetString());
        var proof = value.GetProperty("proofReference").GetString()!;
        Assert.Equal("Observed desktop and narrow layout. Evidence: /proof/capture.png", proof);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proof))).ToLowerInvariant(), value.GetProperty("proofSha256").GetString());
        Assert.Equal(1, posts); Assert.Equal(1, recorded);
    }

    [Fact]
    public async Task DuplicateClickAndDisposedSelectionCannotAcknowledgeAnotherContext()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = 0; var recorded = 0;
        using var f = new Fixture(_ => { posts++; return reply.Task; });
        f.Component.OnRecorded = EventCallback.Factory.Create(f, () => recorded++);
        f.Note("Observed this build");
        var pending = f.Record(); await f.Record();
        Assert.Equal(1, posts);
        f.DisposeComponent();
        reply.SetResult(new(HttpStatusCode.OK)); await pending;
        Assert.Equal(0, recorded);
    }

    [Fact]
    public async Task ConflictKeepsNoteAndDoesNotClaimObservationWasRecorded()
    {
        var recorded = 0;
        using var f = new Fixture(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)));
        f.Component.OnRecorded = EventCallback.Factory.Create(f, () => recorded++);
        f.Note("Actual operator notes"); await f.Record();
        Assert.Equal("Actual operator notes", f.Field<string>("_observation"));
        Assert.Contains("not confirmed", f.Field<string>("_error"));
        Assert.False(f.Field<bool>("_recorded")); Assert.Equal(0, recorded);
    }

    private sealed class Fixture : IDisposable
    {
        public OperatorObservation Component { get; } = new()
        { ProjectId = "forge", SprintId = "sprint-1", PlanHash = "plan", SourceSha = new string('a', 40), DeploymentId = "deployment", CriterionId = "visual" };
        private readonly HttpClient http;
        private bool disposed;
        public Fixture(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        {
            http = new(new Handler(send)) { BaseAddress = new("http://localhost") };
            typeof(OperatorObservation).GetProperty("Http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Component, http);
            typeof(OperatorObservation).GetProperty("Logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Component, NullLogger<OperatorObservation>.Instance);
        }
        public void Note(string note) => typeof(OperatorObservation).GetField("_observation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Component, note);
        public T Field<T>(string name) => (T)typeof(OperatorObservation).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Component)!;
        public Task Record() => (Task)typeof(OperatorObservation).GetMethod("RecordAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Component, null)!;
        public void DisposeComponent() { if (!disposed) { disposed = true; Component.Dispose(); } }
        public void Dispose() { DisposeComponent(); http.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
