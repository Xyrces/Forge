using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Forge.Dashboard.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class RunDetailPollingTests
{
    [Fact]
    public async Task DelayedPreviousProjectCannotStartSecondPollOrStopCurrentLiveRun()
    {
        var oldReply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentDictionary<string, int>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            var project = request.RequestUri!.Query;
            calls.AddOrUpdate(project, 1, (_, n) => n + 1);
            if (project == "?projectId=old") { started.TrySetResult(); return oldReply.Task; }
            currentStarted.TrySetResult();
            return Task.FromResult(Response("running", "Current project run"));
        })) { BaseAddress = new("http://localhost") };
        using var fixture = new Fixture(http);
        var old = fixture.Navigate("old");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var current = fixture.Navigate("current");
        await currentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        oldReply.SetResult(Response("succeeded", "Stale terminal run"));
        await Task.WhenAll(old, current).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(3400);
        Assert.Equal(2, calls["?projectId=current"]); // Initial read + one normal three-second tick.
        Assert.Equal(1, calls["?projectId=old"]);
        Assert.False(fixture.Field<bool>("_terminal"));
        Assert.Null(fixture.Field<string?>("_loadError"));
        Assert.Empty(fixture.Renderer.Errors);
    }

    [Fact]
    public async Task DisposeCancelsPendingReadAndItsLateCompletionCannotRestartPolling()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken observed = default;
        using var http = new HttpClient(new Handler((_, token) =>
        { observed = token; Interlocked.Increment(ref calls); started.TrySetResult(); return reply.Task; }))
            { BaseAddress = new("http://localhost") };
        using var fixture = new Fixture(http);
        var load = fixture.Navigate("current");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Renderer.Dispatcher.InvokeAsync(fixture.Component.Dispose);
        Assert.True(observed.IsCancellationRequested);
        reply.SetResult(Response("running", "Late response"));
        await load;
        await Task.Delay(3400);
        Assert.Equal(1, calls);
        Assert.Null(fixture.Field<object?>("_run"));
        Assert.Null(fixture.Field<string?>("_loadError"));
        Assert.Empty(fixture.Renderer.Errors);
    }

    private static HttpResponseMessage Response(string status, string title) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            view = new { id = "same-local-id", taskId = "task", taskTitle = title, role = "ClientDev", status, startedAt = DateTime.UtcNow },
            transcript = Array.Empty<object>()
        }), System.Text.Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider services = new ServiceCollection().AddLogging().BuildServiceProvider();
        public RunDetail Component { get; } = new();
        public TestRenderer Renderer { get; }
        private readonly int id;
        public Fixture(HttpClient http)
        {
            typeof(RunDetail).GetProperty("Http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Component, http);
            Renderer = new(services);
            id = Renderer.Attach(Component);
        }
        public Task Navigate(string project) => Renderer.Dispatcher.InvokeAsync(() => Renderer.Render(id,
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["RunId"] = "same-local-id", ["Project"] = project })));
        public T Field<T>(string name) => (T)typeof(RunDetail).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Component)!;
        public void Dispose() { Component.Dispose(); Renderer.Dispose(); services.Dispose(); }
    }

    private sealed class TestRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public List<Exception> Errors { get; } = [];
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public int Attach(IComponent component) => AssignRootComponentId(component);
        public Task Render(int id, ParameterView parameters) => RenderRootComponentAsync(id, parameters);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => Errors.Add(exception);
    }
}
