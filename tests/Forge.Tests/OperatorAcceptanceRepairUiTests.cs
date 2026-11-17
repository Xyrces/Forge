using System.Net;
using System.Reflection;
using System.Text.Json;
using Forge.Dashboard.Components;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Forge.Tests;

public sealed class OperatorAcceptanceRepairUiTests
{
    private const string PreviewJson = """
        {"eligible":true,"preview":{"projectId":"forge","specId":"spec-1","specVersion":3,"groomRunId":7,
        "previewHash":"snapshot-hash","authority":{"proposalId":"proposal-1","revision":1,"hash":"approved-hash","specId":"spec-1","approvedExcerpt":"approved scope"},
        "outcomeId":"delivery","objective":"Deploy a useful feature","sourceRows":[{"id":"qa-1","title":"Observe deployed behavior","description":"Operator checks after deployment"}],
        "targetRows":[{"id":"task-1","title":"Implement feature","description":"Code and tests"}],
        "transferredCriteria":[{"id":"visual","text":"Verify desktop and narrow display","evidenceKind":"OperatorObserved","scope":{"approvedExcerpt":"approved scope"}}],
        "criteria":[{"id":"code","text":"Verify implementation","evidenceKind":"MergedAndVerified","scope":{"approvedExcerpt":"approved scope"}}]}}
        """;

    [Fact]
    public async Task ApplySendsOnlyDisplayedSnapshotAndSuccessfulActionCannotRepeat()
    {
        var posts = new List<string>(); var applied = 0;
        using var f = new Fixture(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json(PreviewJson);
            Assert.Equal("/api/specs/spec-1/operator-checks/repair?project=forge", request.RequestUri!.PathAndQuery);
            posts.Add(await request.Content!.ReadAsStringAsync());
            return Json("{}");
        });
        f.Component.Applied = EventCallback.Factory.Create(f, () => applied++);
        await f.Load(); await f.Invoke("ApplyAsync"); await f.Invoke("ApplyAsync");
        var body = JsonDocument.Parse(Assert.Single(posts)).RootElement;
        Assert.Equal(3, body.GetProperty("specVersion").GetInt32());
        Assert.Equal(7, body.GetProperty("groomRunId").GetInt64());
        Assert.Equal("snapshot-hash", body.GetProperty("previewHash").GetString());
        Assert.Equal(3, body.EnumerateObject().Count());
        Assert.Equal(1, applied); Assert.Null(f.Field<object?>("_preview"));
    }

    [Fact]
    public async Task ConflictDiscardsPreviewAndRequiresFreshReview()
    {
        var posts = 0;
        using var f = new Fixture(request => Task.FromResult(request.Method == HttpMethod.Get
            ? Json(PreviewJson) : Reject()));
        HttpResponseMessage Reject() { posts++; return new(HttpStatusCode.Conflict); }
        await f.Load(); await f.Invoke("ApplyAsync"); await f.Invoke("ApplyAsync");
        Assert.Equal(1, posts); Assert.Null(f.Field<object?>("_preview"));
        Assert.Contains("changed since this preview", f.Field<string>("_error"));
        Assert.False(f.Field<bool>("_busy"));
    }

    [Fact]
    public async Task DelayedPreviewFromAnotherProjectCannotAuthorizeAction()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var f = new Fixture(_ => { requests++; return reply.Task; });
        var load = f.Load();
        f.Component.Project = "alpha";
        reply.SetResult(Json(PreviewJson)); await load;
        await f.Invoke("ApplyAsync");
        Assert.Null(f.Field<object?>("_preview")); Assert.Equal(1, requests);
    }

    [Fact]
    public async Task DuplicateClickCannotSendAnotherRequestWhileFirstIsPending()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = 0;
        using var f = new Fixture(request => request.Method == HttpMethod.Get
            ? Task.FromResult(Json(PreviewJson)) : Pending());
        Task<HttpResponseMessage> Pending() { posts++; return reply.Task; }
        await f.Load(); var apply = f.Invoke("ApplyAsync"); await f.Invoke("ApplyAsync");
        Assert.Equal(1, posts); Assert.True(f.Field<bool>("_busy"));
        reply.SetResult(Json("{}")); await apply;
        Assert.False(f.Field<bool>("_busy"));
    }

    [Fact]
    public async Task ProjectChangeDuringPostLoadsNewPreviewAndDiscardsOldAcknowledgement()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = 0;
        using var f = new Fixture(request => request.Method == HttpMethod.Post ? reply.Task
            : Task.FromResult(Json(request.RequestUri!.Query.Contains("alpha") ? PreviewJson.Replace("forge", "alpha") : PreviewJson)));
        f.Component.Applied = EventCallback.Factory.Create(f, () => applied++);
        await f.Load(); var apply = f.Invoke("ApplyAsync");
        f.Component.Project = "alpha"; await f.Load();
        Assert.False(f.Field<bool>("_busy"));
        Assert.Equal("spec-1|alpha|3", f.Field<string>("_loadedTarget"));
        reply.SetResult(Json("{}")); await apply;
        Assert.NotNull(f.Field<object?>("_preview")); Assert.Equal(0, applied);
        Assert.Equal("spec-1|alpha|3", f.Field<string>("_loadedTarget"));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    private sealed class Fixture : IDisposable
    {
        public OperatorAcceptanceRepair Component { get; } = new() { SpecId = "spec-1", Project = "forge", SpecVersion = 3 };
        private readonly HttpClient _http;
        public Fixture(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle)
        {
            _http = new(new Handler(handle)) { BaseAddress = new Uri("http://localhost") };
            typeof(OperatorAcceptanceRepair).GetProperty("Http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Component, _http);
        }
        public T Field<T>(string name) => (T)typeof(OperatorAcceptanceRepair).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Component)!;
        public Task Invoke(string name) => (Task)typeof(OperatorAcceptanceRepair).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Component, null)!;
        public Task Load() => Invoke("OnParametersSetAsync");
        public void Dispose() { Component.Dispose(); _http.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request); }
}
