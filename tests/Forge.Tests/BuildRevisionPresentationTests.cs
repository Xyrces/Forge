using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using Fluxor;
using Forge.Dashboard.Components;
using Forge.Dashboard.Components.Layout;
using Forge.Dashboard.Features.AppShell;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;

namespace Forge.Tests;

/// <summary>Offline client/state/SSR checks only, not browser or deployment qualification.</summary>
public sealed class BuildRevisionPresentationTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string CommitUrl = "https://github.com/Xyrces/Forge/commit/" + Sha;
    private static readonly BuildInfoDto Valid = new("1.2.3+not-revision-authority", ".NET 10.0.0", Sha, CommitUrl);
    private const string ValidJson = """
        {"informationalVersion":"1.2.3+not-revision-authority","framework":".NET 10.0.0",
         "sourceRevision":"0123456789abcdef0123456789abcdef01234567",
         "commitUrl":"https://github.com/Xyrces/Forge/commit/0123456789abcdef0123456789abcdef01234567"}
        """;

    [Theory]
    [InlineData(Sha)]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF01")]
    public async Task RevisionUsesSevenCharactersAndLabeledNativeKeyboardDisclosureWithExactLink(string revision)
    {
        var url = "https://github.com/Xyrces/Forge/commit/" + revision;
        await using var fixture = new Fixture();
        var html = await fixture.RenderRevision(Valid with { SourceRevision = revision, CommitUrl = url });
        Assert.Contains("""<details class="build-revision">""", html);
        Assert.Contains($"""<summary aria-label="Forge revision {revision[..7]}; show full source revision">""", html);
        Assert.Contains($"Forge revision: <code>{revision[..7]}</code>", html);
        Assert.Contains("Full source revision", html);
        Assert.Contains($"""<code class="build-revision__sha">{revision}</code>""", html);
        Assert.Contains($"""<a href="{url}">View Forge commit</a>""", html);
        // Native summary and anchor are keyboard-operable without JS/tabindex overrides.
        Assert.DoesNotContain("tabindex", html);
        Assert.DoesNotContain("title=", html); // Not hover-only full-SHA access.
        Assert.DoesNotContain("Unavailable", html);
        Assert.DoesNotContain(Valid.InformationalVersion, html);
    }

    public static IEnumerable<object?[]> UnavailableMetadata()
    {
        yield return [null];
        yield return [new BuildInfoDto("version+" + Sha, ".NET 10")]; // Old server.
        yield return [Valid with { SourceRevision = null, CommitUrl = null }];
        yield return [Valid with { SourceRevision = null }];
        yield return [Valid with { CommitUrl = null }];
        yield return [Valid with { SourceRevision = "" }];
        yield return [Valid with { SourceRevision = Sha[..7] }];
        yield return [Valid with { SourceRevision = new string('a', 39) }];
        yield return [Valid with { SourceRevision = new string('a', 41) }];
        yield return [Valid with { SourceRevision = new string('g', 40) }];
        yield return [Valid with { SourceRevision = Sha + Environment.NewLine }];
        yield return [Valid with { SourceRevision = Sha + Environment.NewLine + Sha }];
        yield return [Valid with { CommitUrl = "https://example.invalid/commit/" + Sha }];
        yield return [Valid with { CommitUrl = CommitUrl + "?projectId=alpha" }];
        yield return [Valid with { CommitUrl = "https://github.com/Xyrces/Forge/commit/" + new string('a', 40) }];
        yield return [Valid with { CommitUrl = "javascript:alert(1)" }];
    }

    [Theory]
    [MemberData(nameof(UnavailableMetadata))]
    public async Task MissingOrInconsistentMetadataShowsUnavailableWithoutDisclosureLinkOrInferredFallback(BuildInfoDto? info)
    {
        await using var fixture = new Fixture();
        var html = await fixture.RenderRevision(info);
        Assert.Contains("Forge revision:", html);
        Assert.Contains("Unavailable", html);
        Assert.DoesNotContain("<a", html);
        Assert.DoesNotContain("<details", html);
        Assert.DoesNotContain("<summary", html);
        Assert.DoesNotContain(Sha, html);
        Assert.DoesNotContain("View Forge commit", html);
    }

    [Fact]
    public async Task ClientConsumesAdditiveContractAndPreservesExistingVersionFrameworkFields()
    {
        await using var fixture = new Fixture();
        Assert.Equal(Valid, await fixture.Client.GetBuildInfoAsync(CancellationToken.None));
        Assert.Equal(["/api/meta/buildinfo"], fixture.Requests.ToArray());
    }

    [Theory]
    [InlineData("""{"informationalVersion":"legacy","framework":".NET 10"}""")]
    [InlineData("""{"informationalVersion":"legacy","framework":".NET 10","sourceRevision":null,"commitUrl":null}""")]
    public async Task LegacyAndExplicitNullResponsesDoNotInferIdentity(string json)
    {
        await using var fixture = new Fixture(_ => Json(json));
        var info = await fixture.Client.GetBuildInfoAsync(CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal("legacy", info.InformationalVersion);
        Assert.Equal(".NET 10", info.Framework);
        Assert.Null(info.SourceRevision);
        Assert.Null(info.CommitUrl);
        Assert.Contains("Unavailable", await fixture.RenderRevision(info));
    }

    [Theory]
    [InlineData("http-error")]
    [InlineData("network-error")]
    [InlineData("malformed-json")]
    [InlineData("wrong-field-type")]
    [InlineData("timeout")]
    [InlineData("json-null")]
    public async Task FailedRequestClearsPriorRevisionAndRendersUnavailable(string scenario)
    {
        await using var fixture = new Fixture(_ => scenario switch
        {
            "http-error" => new(HttpStatusCode.ServiceUnavailable),
            "network-error" => throw new HttpRequestException("offline injected failure"),
            "malformed-json" => Json("not json"),
            "wrong-field-type" => Json("""{"sourceRevision":42}"""),
            "timeout" => throw new TaskCanceledException("offline injected timeout"),
            _ => Json("null")
        });
        fixture.State.Value = new() { BuildInfo = Valid };
        var root = await fixture.RenderShell();
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Effects.HandleLoadBuildInfo(new(), fixture.Dispatch));
        Assert.Null(fixture.State.Value.BuildInfo);
        var html = await fixture.Html(root);
        Assert.Contains("Unavailable", html);
        Assert.DoesNotContain(CommitUrl, html);
        Assert.DoesNotContain(Sha, html);
        Assert.DoesNotContain("View Forge commit", html);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToUnavailable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await using var fixture = new Fixture(_ => throw new OperationCanceledException(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.GetBuildInfoAsync(cts.Token));
    }

    // Render the real shell at every routed URI without executing page data loads.
    // Routes.razor supplies MainLayout by default; routed pages must not opt out.
    public static IEnumerable<object[]> DashboardRoutes() => typeof(MainLayout).Assembly.GetTypes()
        .Where(t => typeof(IComponent).IsAssignableFrom(t))
        .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().SelectMany(route => new[]
        {
            new object[] { t, route.Template, true },
            new object[] { t, route.Template, false }
        }))
        .OrderBy(row => (string)row[1]);

    [Theory]
    [MemberData(nameof(DashboardRoutes))]
    public async Task GlobalShellDisplaysValidOrUnavailableRevisionOnEveryDashboardRouteWithoutAnyProject(Type page, string template, bool available)
    {
        var explicitLayout = page.GetCustomAttribute<LayoutAttribute>()?.LayoutType;
        Assert.True(explicitLayout is null || explicitLayout == typeof(MainLayout), $"{page.Name} bypasses the global shell");
        var route = template.Replace("{Id}", "example").Replace("{TaskId}", "example")
            .Replace("{SpecId}", "example").Replace("{RunId}", "example");
        await using var fixture = new Fixture(route: route);
        fixture.State.Value = new() { BuildInfo = available ? Valid : null, CurrentRoute = route };
        var root = await fixture.RenderShell();
        var html = await fixture.Html(root);
        if (available) AssertRevision(html);
        else
        {
            Assert.Contains("Forge revision: <span>Unavailable</span>", html);
            Assert.DoesNotContain("build-revision__sha", html);
            Assert.DoesNotContain(CommitUrl, html);
        }
        Assert.Contains($"Route body: {route}", html);
        Assert.Null(fixture.State.Value.CurrentProjectId);
        Assert.DoesNotContain("project-switcher__select", html);
        Assert.Single(fixture.Dispatch.Actions.OfType<AppShellActions.LoadBuildInfoAction>());
    }

    [Fact]
    public async Task ProjectSelectionSwitchingAndRoutesNeverChangeRevisionOrScopeReloadMetadata()
    {
        await using var fixture = new Fixture();
        var root = await fixture.RenderShell();
        Assert.Contains("Unavailable", await fixture.Html(root));
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Effects.HandleLoadBuildInfo(new(), fixture.Dispatch));
        AssertRevision(await fixture.Html(root));
        Assert.Null(fixture.State.Value.CurrentProjectId);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Effects.HandleLoadProjects(new(), fixture.Dispatch));
        Assert.Equal("forge", fixture.State.Value.CurrentProjectId);
        AssertRevision(await fixture.Html(root));
        foreach (var project in new[] { "alpha", "forge", "alpha" })
        {
            await fixture.Renderer.Dispatcher.InvokeAsync(async () =>
            {
                var select = new AppShellActions.SelectProjectAction(project);
                fixture.Dispatch.Dispatch(select);
                await fixture.Effects.HandleSelectProject(select, fixture.Dispatch);
                fixture.Dispatch.Dispatch(new AppShellActions.NavigateAction("/projects/" + project + "/overview"));
            });
            Assert.Equal(project, fixture.State.Value.CurrentProjectId);
            Assert.Equal(Valid, fixture.State.Value.BuildInfo);
            AssertRevision(await fixture.Html(root));
        }
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Dispatch.Dispatch(new AppShellActions.ProjectsLoadedAction([], null)));
        Assert.Null(fixture.State.Value.CurrentProjectId);
        AssertRevision(await fixture.Html(root));
        Assert.Single(fixture.Requests, r => r.StartsWith("/api/meta/buildinfo", StringComparison.Ordinal));
        Assert.Contains("/api/meta/buildinfo", fixture.Requests);
        Assert.DoesNotContain(fixture.Requests, r => r.StartsWith("/api/meta/buildinfo?", StringComparison.Ordinal));
        Assert.Single(fixture.Dispatch.Actions.OfType<AppShellActions.LoadBuildInfoAction>());
    }

    [Fact]
    public async Task ProjectDataCannotSupplyFallbackForUnavailableRevision()
    {
        await using var fixture = new Fixture();
        fixture.State.Value = new() { BuildInfo = Valid with { SourceRevision = null, CommitUrl = null } };
        var root = await fixture.RenderShell();
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
            fixture.Dispatch.Dispatch(new AppShellActions.ProjectsLoadedAction(
                [new(Sha, CommitUrl), new("alpha", "Alpha")], Sha)));
        foreach (var project in new[] { "alpha", Sha })
        {
            await fixture.Renderer.Dispatcher.InvokeAsync(async () =>
            {
                var select = new AppShellActions.SelectProjectAction(project);
                fixture.Dispatch.Dispatch(select);
                await fixture.Effects.HandleSelectProject(select, fixture.Dispatch);
            });
            var html = await fixture.Html(root);
            Assert.Contains("Forge revision: <span>Unavailable</span>", html);
            Assert.DoesNotContain("build-revision__sha", html);
            Assert.DoesNotContain("View Forge commit", html);
        }
        Assert.DoesNotContain(fixture.Requests, r => r.StartsWith("/api/meta/buildinfo", StringComparison.Ordinal));
        Assert.Single(fixture.Dispatch.Actions.OfType<AppShellActions.LoadBuildInfoAction>());
    }

    private static void AssertRevision(string html)
    {
        Assert.Contains("Forge revision: <code>0123456</code>", html);
        Assert.Contains($"""<code class="build-revision__sha">{Sha}</code>""", html);
        Assert.Contains($"""<a href="{CommitUrl}">View Forge commit</a>""", html);
        Assert.DoesNotContain("Unavailable", html);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        public ConcurrentQueue<string> Requests { get; } = new();
        public ShellState State { get; } = new();
        public ShellDispatcher Dispatch { get; }
        public AppShellClient Client { get; }
        public AppShellEffects Effects { get; }
        public HtmlRenderer Renderer { get; }

        public Fixture(Func<HttpRequestMessage, HttpResponseMessage>? buildResponse = null, string route = "/")
        {
            Dispatch = new(State);
            _http = new(new Handler(request =>
            {
                var path = request.RequestUri!.PathAndQuery;
                Requests.Enqueue(path);
                return path switch
                {
                    "/api/meta/buildinfo" => buildResponse?.Invoke(request) ?? Json(ValidJson),
                    "/api/projects/" => Json("""[{"id":"forge","name":"Forge"},{"id":"alpha","name":"Alpha"}]"""),
                    "/api/now" => Json("""{"attention":[]}"""),
                    _ => throw new InvalidOperationException("Unexpected offline request: " + path)
                };
            })) { BaseAddress = new("http://offline.invalid/") };
            Client = new(_http);
            Effects = new(Client, State, new NoJs());
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IState<AppShellState>>(State);
            services.AddSingleton<IDispatcher>(Dispatch);
            services.AddSingleton<NavigationManager>(new Navigation(route));
            services.AddSingleton(_http);
            _services = services.BuildServiceProvider();
            Renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public Task<Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent> RenderShell() =>
            Renderer.Dispatcher.InvokeAsync(() => Renderer.RenderComponentAsync<MainLayout>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(MainLayout.Body)] = (RenderFragment)(builder => builder.AddContent(0, "Route body: " + (State.Value.CurrentRoute ?? "/")))
                })));

        public Task<string> RenderRevision(BuildInfoDto? info) => Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await Renderer.RenderComponentAsync<BuildRevision>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(BuildRevision.BuildInfo)] = info }));
            return root.ToHtmlString();
        });

        public Task<string> Html(Microsoft.AspNetCore.Components.Web.HtmlRendering.HtmlRootComponent root) =>
            Renderer.Dispatcher.InvokeAsync(root.ToHtmlString);

        public async ValueTask DisposeAsync()
        {
            await Renderer.DisposeAsync();
            await _services.DisposeAsync();
            _http.Dispose();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class ShellState : IState<AppShellState>
    {
        public AppShellState Value { get; set; } = new();
        public event EventHandler? StateChanged;
        public void Changed() => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ShellDispatcher(ShellState state) : IDispatcher
    {
        public ConcurrentQueue<object> Actions { get; } = new();
        public event EventHandler<ActionDispatchedEventArgs>? ActionDispatched { add { } remove { } }
        public void Dispatch(object action)
        {
            Actions.Enqueue(action);
            var next = action switch
            {
                AppShellActions.BuildInfoLoadedAction a => AppShellReducers.OnBuildInfoLoaded(state.Value, a),
                AppShellActions.ProjectsLoadedAction a => AppShellReducers.OnProjectsLoaded(state.Value, a),
                AppShellActions.SelectProjectAction a => AppShellReducers.OnSelectProject(state.Value, a),
                AppShellActions.NavigateAction a => AppShellReducers.OnNavigate(state.Value, a),
                _ => state.Value
            };
            if (next != state.Value)
            {
                state.Value = next;
                state.Changed();
            }
        }
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => new(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => new(default(TValue)!);
    }

    private sealed class Navigation : NavigationManager
    {
        public Navigation(string route) => Initialize("http://offline.invalid/", "http://offline.invalid" + route);
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
    }
}
