using System.Net;
using System.Reflection;
using System.Text.Json;
using Forge.Dashboard.Components;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Forge.Tests;

public sealed class TaskRegroomUiTests
{
    private const string Preview = """{"eligible":true,"snapshot":{"updatedAt":"2026-11-17T19:00:00Z","bindingHash":"exact-binding"}}""";

    [Fact]
    public async Task RequiresReasonAndSendsExactSnapshotOnce()
    {
        string? body = null; var posts = 0; var requested = 0;
        using var f = new Fixture(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json(Preview);
            Assert.Equal("/api/tasks/task-3/regroom?projectId=forge", request.RequestUri!.PathAndQuery);
            posts++; body = await request.Content!.ReadAsStringAsync(); return Json("{}");
        });
        f.Component.Requested = EventCallback.Factory.Create(f, () => requested++);
        await f.Load(); await f.Apply(); Assert.Equal(0, posts);
        f.Reason("  Bind to the active outcome criteria  ");
        await f.Apply(); await f.Apply();
        var value = JsonDocument.Parse(body!).RootElement;
        Assert.Equal("2026-11-17T19:00:00Z", value.GetProperty("expectedUpdatedAt").GetString());
        Assert.Equal("exact-binding", value.GetProperty("expectedBindingHash").GetString());
        Assert.Equal("Bind to the active outcome criteria", value.GetProperty("reason").GetString());
        Assert.Equal(1, posts); Assert.Equal(1, requested);
        Assert.Null(f.Field<object?>("_snapshot"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictOrLostAcknowledgementRequiresFreshEligibility(bool lost)
    {
        var posts = 0;
        using var f = new Fixture(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json(Preview));
            posts++;
            if (lost) throw new HttpRequestException("lost acknowledgement");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
        });
        await f.Load(); f.Reason("Reconsider mapping"); await f.Apply(); await f.Apply();
        Assert.Equal(1, posts); Assert.Null(f.Field<object?>("_snapshot"));
        Assert.NotNull(f.Field<string?>("_error")); Assert.Null(f.Field<string?>("_notice"));
        Assert.Equal("Reconsider mapping", f.Field<string>("_reason"));
    }

    [Fact]
    public async Task FailedPreviewCannotEnableMutation()
    {
        var posts = 0;
        using var f = new Fixture(request =>
        { if (request.Method == HttpMethod.Post) posts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)); });
        await f.Load(); f.Reason("Reason"); await f.Apply();
        Assert.Equal(0, posts); Assert.Null(f.Field<object?>("_snapshot")); Assert.NotNull(f.Field<string?>("_error"));
    }

    [Fact]
    public async Task ProjectChangeDuringPostCannotAcknowledgeOrClearNewPreview()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = 0; var posts = 0;
        using var f = new Fixture(request =>
        { if (request.Method == HttpMethod.Get) return Task.FromResult(Json(Preview)); posts++; return reply.Task; });
        f.Component.Requested = EventCallback.Factory.Create(f, () => requested++);
        await f.Load(); f.Reason("Old project reason"); var old = f.Apply(); await f.Apply();
        Assert.Equal(1, posts);
        f.Component.ProjectId = "other"; await f.Load();
        Assert.Equal("", f.Field<string>("_reason")); Assert.False(f.Field<bool>("_busy"));
        reply.SetResult(Json("{}")); await old;
        Assert.Equal(0, requested); Assert.NotNull(f.Field<object?>("_snapshot"));
        Assert.Equal("other|task-3", f.Field<string>("_loadedTarget"));
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, System.Text.Encoding.UTF8, "application/json") };
    private sealed class Fixture : IDisposable
    {
        public TaskRegroom Component { get; } = new() { TaskId = "task-3", ProjectId = "forge" };
        private readonly HttpClient http;
        public Fixture(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        {
            http = new(new Handler(send)) { BaseAddress = new("http://localhost") };
            typeof(TaskRegroom).GetProperty("Http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Component, http);
        }
        public void Reason(string value) => typeof(TaskRegroom).GetField("_reason", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Component, value);
        public T Field<T>(string name) => (T)typeof(TaskRegroom).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Component)!;
        private Task Invoke(string name) => (Task)typeof(TaskRegroom).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Component, null)!;
        public Task Load() => Invoke("OnParametersSetAsync");
        public Task Apply() => Invoke("ApplyAsync");
        public void Dispose() { Component.Dispose(); http.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
