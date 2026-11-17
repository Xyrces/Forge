using System.Collections.Concurrent;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using Fluxor;
using Forge.Dashboard;
using Forge.Dashboard.Components.Layout;
using Forge.Dashboard.Features.AppShell;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// Combined-source offline checks: actual reader/HTTP JSON through the UI client,
/// effect and reducers into the real shell. Not browser or running-image evidence.
/// </summary>
public sealed class BuildRevisionContractTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string UpperSha = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
    private const string CommitPrefix = "https://github.com/Xyrces/Forge/commit/";

    [Theory]
    [InlineData("valid", Sha)]
    [InlineData("newline", Sha)]
    [InlineData("uppercase", UpperSha)]
    [InlineData("empty", null)]
    [InlineData("abbreviated", null)]
    [InlineData("malformed", null)]
    [InlineData("multi-value", null)]
    [InlineData("missing", null)]
    [InlineData("read-failure", null)]
    public async Task HttpMetadataFlowsIntoGlobalShellAndStaysIndependentOfProjectSelection(string scenario, string? expectedRevision)
    {
        await using var fixture = new Fixture(scenario);
        var root = await fixture.RenderShell();
        Assert.Contains("Forge revision: <span>Unavailable</span>", await fixture.Html(root));
        Assert.Null(fixture.State.Value.CurrentProjectId);
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Effects.HandleLoadBuildInfo(new(), fixture.Dispatch));

        var metadata = fixture.State.Value.BuildInfo;
        Assert.NotNull(metadata);
        var assembly = typeof(BuildInfoEndpoints).Assembly;
        var expectedVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        Assert.Equal(expectedVersion, metadata.InformationalVersion);
        Assert.Equal(RuntimeInformation.FrameworkDescription, metadata.Framework);
        Assert.Equal(expectedRevision, metadata.SourceRevision);
        Assert.Equal(expectedRevision is null ? null : CommitPrefix + expectedRevision, metadata.CommitUrl);
        AssertPresentation(await fixture.Html(root), expectedRevision);
        Assert.Equal(1, fixture.MetadataRequests);

        // Project-list arrival and selection run their real effects, not a metadata reload.
        await fixture.Renderer.Dispatcher.InvokeAsync(() => fixture.Effects.HandleLoadProjects(new(), fixture.Dispatch));
        Assert.Equal("forge", fixture.State.Value.CurrentProjectId);
        Assert.Same(metadata, fixture.State.Value.BuildInfo);
        AssertPresentation(await fixture.Html(root), expectedRevision);
        var explicitReads = 1;
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
            Assert.Same(metadata, fixture.State.Value.BuildInfo);
            AssertPresentation(await fixture.Html(root), expectedRevision);
            Assert.Equal(explicitReads, fixture.MetadataRequests);

            // Deliberate independent API comparison, not triggered by the project effect.
            Assert.Equal(metadata, await fixture.Client.GetBuildInfoAsync(CancellationToken.None));
            explicitReads++;
        }
        await fixture.Renderer.Dispatcher.InvokeAsync(() =>
            fixture.Dispatch.Dispatch(new AppShellActions.ProjectsLoadedAction([], null)));
        Assert.Null(fixture.State.Value.CurrentProjectId);
        Assert.Same(metadata, fixture.State.Value.BuildInfo);
        var noProjectHtml = await fixture.Html(root);
        AssertPresentation(noProjectHtml, expectedRevision);
        Assert.DoesNotContain("project-switcher__select", noProjectHtml);
        Assert.Equal(explicitReads, fixture.MetadataRequests);
        Assert.Single(fixture.Dispatch.Actions.OfType<AppShellActions.LoadBuildInfoAction>());
        Assert.All(fixture.Requests.Where(p => p.StartsWith("/api/meta/buildinfo", StringComparison.Ordinal)),
            path => Assert.Equal("/api/meta/buildinfo", path));
        Assert.Equal(explicitReads, fixture.ReadPaths.Count);
        Assert.All(fixture.ReadPaths, path => Assert.Equal("/app/source-revision", path));
    }

    private static void AssertPresentation(string html, string? revision)
    {
        if (revision is null)
        {
            Assert.Contains("Forge revision: <span>Unavailable</span>", html);
            Assert.DoesNotContain("build-revision__sha", html);
            Assert.DoesNotContain("View Forge commit", html);
            Assert.DoesNotContain(CommitPrefix, html);
        }
        else
        {
            Assert.Contains($"Forge revision: <code>{revision[..7]}</code>", html);
            Assert.Contains($"""<summary aria-label="Forge revision {revision[..7]}; show full source revision">""", html);
            Assert.Contains($"""<code class="build-revision__sha">{revision}</code>""", html);
            Assert.Contains($"""<a href="{CommitPrefix}{revision}">View Forge commit</a>""", html);
            Assert.DoesNotContain("Unavailable", html);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _workDir = TempRoot.Instance.NewDirectory("revision-contract");
        private readonly WebApplication _app;
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        public ConcurrentQueue<string> Requests { get; } = new();
        public ConcurrentQueue<string> ReadPaths { get; } = new();
        public int MetadataRequests => Requests.Count(p => p.StartsWith("/api/meta/buildinfo", StringComparison.Ordinal));
        public ShellState State { get; } = new();
        public ShellDispatcher Dispatch { get; }
        public AppShellClient Client { get; }
        public AppShellEffects Effects { get; }
        public HtmlRenderer Renderer { get; }

        public Fixture(string scenario)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = _workDir,
                ApplicationName = "Forge.Tests"
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            _app = builder.Build();
            _app.Use(async (context, next) =>
            {
                Requests.Enqueue(context.Request.Path + context.Request.QueryString);
                await next();
            });
            _app.MapBuildInfoEndpoint(new RunningRevisionReader(NullLogger.Instance, (path, _) =>
            {
                ReadPaths.Enqueue(path);
                return scenario switch
                {
                    "valid" => Task.FromResult(Sha),
                    "newline" => Task.FromResult("  " + Sha + Environment.NewLine),
                    "uppercase" => Task.FromResult(UpperSha),
                    "empty" => Task.FromResult(" "),
                    "abbreviated" => Task.FromResult(Sha[..7]),
                    "malformed" => Task.FromResult(new string('z', 40)),
                    "multi-value" => Task.FromResult(Sha + Environment.NewLine + Sha),
                    "missing" => Task.FromException<string>(new FileNotFoundException("Injected missing image artifact")),
                    "read-failure" => Task.FromException<string>(new IOException("Injected image artifact read failure")),
                    _ => throw new InvalidOperationException("Unknown offline scenario")
                };
            }));
            // Isolated shell support; no real projects, stores or orchestrator services.
            _app.MapGet("/api/projects/", () => new[] { new ProjectListEntry("forge", "Forge"), new("alpha", "Alpha") });
            _app.MapGet("/api/now", () => new { attention = Array.Empty<object>() });
            _app.Start();
            _http = new(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new($"http://127.0.0.1:{_app.GetPort()}/")
            };
            Dispatch = new(State);
            Client = new(_http);
            Effects = new(Client, State, new NoJs());
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IState<AppShellState>>(State);
            services.AddSingleton<IDispatcher>(Dispatch);
            services.AddSingleton<NavigationManager>(new Navigation());
            services.AddSingleton(_http);
            _services = services.BuildServiceProvider();
            Renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }

        public Task<HtmlRootComponent> RenderShell() => Renderer.Dispatcher.InvokeAsync(() =>
            Renderer.RenderComponentAsync<MainLayout>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(MainLayout.Body)] = (RenderFragment)(builder => builder.AddContent(0, State.Value.CurrentRoute ?? "/"))
            })));

        public Task<string> Html(HtmlRootComponent root) => Renderer.Dispatcher.InvokeAsync(root.ToHtmlString);

        public async ValueTask DisposeAsync()
        {
            await Renderer.DisposeAsync();
            await _services.DisposeAsync();
            _http.Dispose();
            await _app.DisposeAsync();
            Directory.Delete(_workDir, recursive: true);
        }
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
        public Navigation() => Initialize("http://offline.invalid/", "http://offline.invalid/");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
    }
}
