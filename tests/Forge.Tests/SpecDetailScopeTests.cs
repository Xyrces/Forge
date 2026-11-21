using System.Net;
using System.Reflection;
using Fluxor;
using Forge.Dashboard.Components.Pages;
using Forge.Dashboard.Features.AppShell;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Forge.Tests;

public sealed class SpecDetailScopeTests
{
    [Fact]
    public async Task ExplicitDeepLinkAlignsShellAndMissingSpecHasTerminalScopedState()
    {
        var requests = new List<string>();
        using var fixture = new Fixture(async request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            await Task.Yield(); return new(HttpStatusCode.NotFound);
        }, "forge", "alpha");
        await fixture.Load();
        Assert.Equal("alpha", fixture.State.Value.CurrentProjectId);
        Assert.True(fixture.Field<bool>("_notFound"));
        Assert.False(fixture.Field<bool>("_loading"));
        Assert.Null(fixture.Field<object?>("_spec"));
        Assert.Null(fixture.Field<object?>("_actions"));
        Assert.Equal("/specs?project=alpha", fixture.Property<string>("BackToSpecs"));
        Assert.Single(requests); Assert.Contains("project=alpha", requests[0]);
    }

    [Fact]
    public async Task DelayedProjectPreferenceCannotOverrideExplicitEntityScope()
    {
        using var fixture = new Fixture(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)), "alpha", "forge");
        await fixture.Load();
        fixture.Dispatch.Dispatch(new AppShellActions.ProjectsLoadedAction([], "alpha"));
        Assert.Equal("forge", fixture.State.Value.CurrentProjectId);
        Assert.False(fixture.Field<bool>("_leaving"));
        Assert.Equal("/specs?project=forge", fixture.Property<string>("BackToSpecs"));
    }

    [Fact]
    public async Task ProjectSwitchNavigatesToListAndDiscardsOutstandingEntityResponse()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(_ => response.Task, "alpha", "forge");
        var load = fixture.Load();
        Assert.Equal("forge", fixture.State.Value.CurrentProjectId);
        fixture.Dispatch.Dispatch(new AppShellActions.SelectProjectAction("alpha"));
        Assert.Equal("http://localhost/specs?project=alpha", fixture.Navigation.Uri);
        response.SetResult(Json(Tree));
        await load;
        Assert.Null(fixture.Field<object?>("_spec"));
        Assert.Null(fixture.Field<object?>("_loadedActionTarget"));
        Assert.True(fixture.Field<bool>("_leaving"));
    }

    [Fact]
    public async Task MissingRefreshClearsPriorForcedGroomingOffer()
    {
        using var fixture = new Fixture(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)), "forge", "forge");
        fixture.SetField("_offerForceGroom", true);
        fixture.SetField("_actionError", "Previously groomed; force offered");
        await fixture.Load();
        Assert.True(fixture.Field<bool>("_notFound"));
        Assert.False(fixture.Field<bool>("_offerForceGroom"));
        Assert.Null(fixture.Field<object?>("_actionError"));
        Assert.Null(fixture.Field<object?>("_loadedActionTarget"));
    }

    [Fact]
    public async Task RetryableFailureDoesNotExposeExceptionOrKeepLoadingAndRetryCanSucceed()
    {
        var fail = true;
        using var fixture = new Fixture(request =>
        {
            if (fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("/tree") ? Tree : request.RequestUri.AbsolutePath.EndsWith("/actions") ? "{}" : "[]"));
        }, "forge", "forge");
        await fixture.Load();
        Assert.False(fixture.Field<bool>("_loading"));
        Assert.Contains("try again", fixture.Field<string>("_error"));
        Assert.DoesNotContain("503", fixture.Field<string>("_error"));
        fail = false;
        await fixture.Invoke("LoadAsync");
        Assert.NotNull(fixture.Field<object?>("_spec"));
        Assert.Null(fixture.Field<object?>("_error"));
    }

    private const string Tree = """{"spec":{"id":"spec-1","title":"Owned spec","status":"ReadyForDesign"},"stories":[],"orphanTasks":[],"groomRuns":[]}""";
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    private sealed class Fixture : IDisposable
    {
        private readonly SpecDetail _component = new();
        private readonly HttpClient _http;
        public State State { get; }
        public Dispatcher Dispatch { get; }
        public Navigation Navigation { get; } = new();
        public Fixture(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle, string shell, string project)
        {
            State = new(shell);
            var subscriptions = new Subscriptions(); Dispatch = new(State, subscriptions);
            _http = new(new Handler(handle)) { BaseAddress = new Uri("http://localhost") };
            Set("ShellStore", State); Set("Dispatch", Dispatch); Set("ActionSubscriber", subscriptions); Set("Http", _http); Set("Nav", Navigation);
            _component.SpecId = "spec-1"; _component.Project = project;
            Method("OnInitialized").Invoke(_component, null);
        }
        private static MethodInfo Method(string name) => typeof(SpecDetail).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        private void Set(string name, object value) => typeof(SpecDetail).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_component, value);
        public void SetField(string name, object value) => typeof(SpecDetail).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_component, value);
        public T Field<T>(string name) => (T)typeof(SpecDetail).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_component)!;
        public T Property<T>(string name) => (T)typeof(SpecDetail).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_component)!;
        public Task Invoke(string name) => (Task)Method(name).Invoke(_component, null)!;
        public Task Load() => Invoke("OnParametersSetAsync");
        public void Dispose() { _component.Dispose(); _http.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request); }
    private sealed class State(string project) : IState<AppShellState>
    {
        public AppShellState Value { get; set; } = new() { CurrentProjectId = project };
        public event EventHandler? StateChanged { add { } remove { } }
    }
    private sealed class Dispatcher(State state, Subscriptions subscriptions) : IDispatcher
    {
        public void Dispatch(object action)
        {
            if (action is AppShellActions.SelectProjectAction select) state.Value = state.Value with { CurrentProjectId = select.ProjectId };
            if (action is AppShellActions.ProjectsLoadedAction loaded) state.Value = state.Value with { CurrentProjectId = loaded.CurrentProjectId };
            subscriptions.Publish(action);
        }
        public event EventHandler<ActionDispatchedEventArgs>? ActionDispatched { add { } remove { } }
    }
    private sealed class Subscriptions : IActionSubscriber
    {
        private readonly Dictionary<Type, Action<object>> _callbacks = new();
        public void SubscribeToAction<TAction>(object subscriber, Action<TAction> callback) => _callbacks[typeof(TAction)] = action => callback((TAction)action);
        public void UnsubscribeFromAllActions(object subscriber) => _callbacks.Clear();
        public IDisposable GetActionUnsubscriberAsIDisposable(object subscriber) => new EmptyDisposable();
        public void Publish(object action) { if (_callbacks.TryGetValue(action.GetType(), out var callback)) callback(action); }
        private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }
    }
    private sealed class Navigation : NavigationManager
    {
        public Navigation() => Initialize("http://localhost/", "http://localhost/specs/spec-1");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
    }
}
