using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Db;
using Xunit;
using Xunit.Abstractions;

namespace Forge.Tests.Integration;

public sealed class PublishedDashboardAssetTests(ITestOutputHelper output)
{
    private const string AssetPath = "/_content/Fluxor.Blazor.Web.ReduxDevTools/reduxDevTools.js";
    private const string AliasOnly = "window.__FluxorDevTools__ = window.__REDUX_DEVTOOLS_EXTENSION__;";
    // Representative Fluxor 6.10 bridge with fixed connection options for this HTTP fixture.
    private const string Bridge = """
        window.__FluxorDevTools__ = new (function() {
            const reduxDevTools = window.__REDUX_DEVTOOLS_EXTENSION__;
            this.init = function() {};
            if (reduxDevTools !== undefined && reduxDevTools !== null) {
                const fluxorDevTools = reduxDevTools.connect({ name: "Forge asset fixture" });
                if (fluxorDevTools !== undefined && fluxorDevTools !== null) {
                    this.init = function(dotNetCallbacks, state) {
                        window.fluxorDevToolsDotNetInterop = dotNetCallbacks;
                        state = JSON.parse(state);
                        fluxorDevTools.init(state);
                    };
                    this.dispatch = function(action, state, stackTrace) {
                        action = JSON.parse(action);
                        state = JSON.parse(state);
                        window.fluxorDevToolsDotNetInterop.stackTrace = stackTrace;
                        fluxorDevTools.send(action, state);
                    };
                }
            }
        })();
        """;
    private const string NoForwarding = """
        window.__FluxorDevTools__ = new (function() {
            const reduxDevTools = window.__REDUX_DEVTOOLS_EXTENSION__;
            const fluxorDevTools = reduxDevTools.connect({});
            this.init = function(dotNetCallbacks, state) { state = JSON.parse(state); };
            this.dispatch = function(action, state, stackTrace) {
                action = JSON.parse(action);
                state = JSON.parse(state);
            };
        })();
        """;
    private const string Bootstrap = "<script src='_framework/blazor.web.js'></script>";
    private static readonly string[] Routes = ["/sprints", "/projects/forge/overview"];

    [SkippableFact]
    public async Task PublishedProductionDashboardServesAnyReferencedReduxDevToolsScript()
    {
        var publish = Environment.GetEnvironmentVariable("FORGE_ASSET_CHECK_PUBLISH_DIR");
        Skip.If(string.IsNullOrWhiteSpace(publish), "Set FORGE_ASSET_CHECK_PUBLISH_DIR to a fresh Release publish directory.");
        Assert.True(Path.IsPathFullyQualified(publish!), "Use an absolute publish directory path.");
        foreach (var file in new[] { "Forge.Core.dll", "Forge.Core.runtimeconfig.json", "wwwroot/app.css" })
            Assert.True(File.Exists(Path.Combine(publish!, file)), $"Publish output is missing {file} in {publish}.");

        using var temp = new TempDbFixture();
        var data = temp.NewDirectory("published-asset-check");
        await using (var registry = new ProjectStore(new IssueStore(ForgeDb.Sqlite(
            TempRoot.BuildConnectionString(ForgesystemPaths.IssuesDb(data, "default"))))))
        {
            // A missing local remote triggers the normal empty-checkout scaffold without network access.
            await registry.UpsertAsync(new NewProject("forge", "Asset check fixture", Path.Combine(data, "not-a-remote")));
        }

        // DashboardHost's internal API clients also need the concrete port before the host starts.
        var port = FindFreePort();
        var origin = new Uri($"http://127.0.0.1:{port}");
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = publish!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(publish!, "Forge.Core.dll"));
        start.ArgumentList.Add("--dashboard-only");
        // Do not inherit operator configuration or static-web-asset overrides.
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "DOTNET_ROOT", "SystemRoot", "ProgramData" })
            if (Environment.GetEnvironmentVariable(key) is { } value)
                start.Environment[key] = value;
        start.Environment["HOME"] = data;
        start.Environment["USERPROFILE"] = data;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["Forgesystem__DataRoot"] = data;
        start.Environment["Db__Provider"] = "sqlite";
        start.Environment["Db__AutoMigrate"] = "true";
        start.Environment["Controller__Enabled"] = "false";
        start.Environment["Dashboard__Enabled"] = "true";
        start.Environment["Dashboard__Hostname"] = "127.0.0.1";
        start.Environment["Dashboard__Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Use Kestrel's host configuration (not Dashboard:Kestrel, which binds all interfaces).
        start.Environment["Kestrel__Endpoints__AssetCheck__Url"] = origin.GetLeftPart(UriPartial.Authority);
        start.Environment["Logging__LogLevel__Microsoft.Hosting.Lifetime"] = "Information";

        using var child = new Process { StartInfo = start, EnableRaisingEvents = true };
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var production = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        child.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not { } line) return;
            output.WriteLine(line);
            if (line.Contains($"Now listening on: {origin.GetLeftPart(UriPartial.Authority)}", StringComparison.Ordinal))
                listening.TrySetResult();
            if (line.TrimEnd().EndsWith("Hosting environment: Production", StringComparison.Ordinal))
                production.TrySetResult();
        };
        child.ErrorDataReceived += (_, e) => { if (e.Data is { } line) output.WriteLine(line); };
        Assert.True(child.Start());
        try
        {
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ready = Task.WhenAll(listening.Task, production.Task);
            var exited = child.WaitForExitAsync(startup.Token);
            var completed = await Task.WhenAny(ready, exited);
            Assert.True(completed == ready && !child.HasExited,
                $"Published dashboard did not start on {origin} in Production. See child output.");

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            {
                BaseAddress = origin,
                Timeout = TimeSpan.FromSeconds(15),
            };
            foreach (var route in Routes)
                await AssertReduxAssetServedAsync(client, route);
        }
        finally
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
            Assert.True(child.WaitForExit(5_000), "Published dashboard did not stop after being killed.");
            child.WaitForExit(); // Drain the redirected output after the child stops.
        }
    }

    [Fact]
    public async Task NoReferenceDoesNotRequireAnAssetRequest()
    {
        using var client = TestClient($"<!-- <script src='{AssetPath}'></script> -->",
            _ => throw new InvalidOperationException("No Redux script request was expected."));
        await AssertReduxAssetServedAsync(client, Routes[0]);
    }

    [Fact]
    public async Task ReferencedScriptUsesHtmlBaseAndPreservesQuery()
    {
        var requests = new List<Uri>();
        using var client = TestClient($"<SCRIPT defer SRC='{AssetPath.TrimStart('/')}?v=2&amp;x=1'></SCRIPT>", uri =>
        {
            requests.Add(uri);
            return Response(HttpStatusCode.OK, "application/javascript", Bridge);
        });
        await AssertReduxAssetServedAsync(client, Routes[1]);
        Assert.Equal(AssetPath + "?v=2&x=1", Assert.Single(requests).PathAndQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "text/plain", "", "HTTP 404")]
    [InlineData(HttpStatusCode.InternalServerError, "text/html", "<html>error</html>", "HTTP 500")]
    [InlineData(HttpStatusCode.Redirect, "text/html", "", "HTTP 302")]
    [InlineData(HttpStatusCode.OK, "text/html", "<html>fallback</html>", "JavaScript media type")]
    [InlineData(HttpStatusCode.OK, "text/javascript", "<html>" + Bridge + "</html>", "Fluxor bridge")]
    [InlineData(HttpStatusCode.OK, "text/javascript", "console.log('unrelated');", "Fluxor bridge")]
    [InlineData(HttpStatusCode.OK, "text/javascript", AliasOnly, "Fluxor bridge")]
    [InlineData(HttpStatusCode.OK, "text/javascript", "// " + AliasOnly, "Fluxor bridge")]
    [InlineData(HttpStatusCode.OK, "text/javascript", "/*" + Bridge + "*/", "Fluxor bridge")]
    [InlineData(HttpStatusCode.OK, "text/javascript", "const fixture = `" + Bridge + "`;", "Fluxor bridge")]
    [InlineData(HttpStatusCode.OK, "text/javascript", NoForwarding, "Fluxor bridge")]
    public async Task ReferencedScriptMustServeTheExpectedJavaScript(
        HttpStatusCode status, string mediaType, string body, string diagnostic)
    {
        using var client = TestClient($"<script src='{AssetPath}'></script>", _ => Response(status, mediaType, body));
        var failure = await Record.ExceptionAsync(() => AssertReduxAssetServedAsync(client, Routes[0]));
        var assertion = Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(failure);
        Assert.Contains(diagnostic, assertion.Message);
        Assert.Contains(AssetPath, assertion.Message);
    }

    private static async Task AssertReduxAssetServedAsync(HttpClient client, string route)
    {
        var pageUri = new Uri(client.BaseAddress!, route);
        using var page = await client.GetAsync(pageUri);
        Assert.True(page.StatusCode == HttpStatusCode.OK, $"GET {route} returned HTTP {(int)page.StatusCode}.");
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        var html = await page.Content.ReadAsStringAsync();
        html = Regex.Replace(html, "<!--.*?-->", "", RegexOptions.Singleline);
        var scripts = Regex.Matches(html, @"<script\b(?<attributes>[^>]*)>.*?</script\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Select(tag => Attribute(tag.Groups["attributes"].Value, "src"))
            .OfType<string>().ToArray();
        // Match the actual rendered shell, not a 200 login/error page with no script.
        Assert.Contains(scripts, src => src.TrimStart('/').StartsWith("_framework/blazor.web.js", StringComparison.Ordinal));
        var href = Attribute(Regex.Match(html, @"<base\b[^>]*>", RegexOptions.IgnoreCase).Value, "href");
        var assetBase = href is null ? pageUri : new Uri(pageUri, href);
        foreach (var src in scripts)
        {
            if (!Uri.TryCreate(assetBase, src, out var asset) ||
                !Uri.UnescapeDataString(asset.AbsolutePath).EndsWith("/reduxDevTools.js", StringComparison.OrdinalIgnoreCase))
                continue;

            var label = $"{route} references Redux DevTools at {asset}";
            Assert.True(asset.GetLeftPart(UriPartial.Authority) == pageUri.GetLeftPart(UriPartial.Authority),
                $"{label}, which is not served by the dashboard origin.");
            using var response = await client.GetAsync(asset);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{label}, but GET returned HTTP {(int)response.StatusCode}.");
            var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            Assert.True(type is "text/javascript" or "application/javascript" or "application/ecmascript" or "text/ecmascript",
                $"{label}, but GET returned {type ?? "no Content-Type"} instead of a JavaScript media type.");
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(HasFluxorBridgeFingerprint(body),
                $"{label}, but the response does not match the expected Fluxor bridge implementation.");
        }
    }

    private static bool HasFluxorBridgeFingerprint(string body)
    {
        if (body.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith('<'))
            return false;

        // Fluxor.Blazor.Web.ReduxDevTools 6.10 generates this bridge with variable connection options.
        // Match distinctive implementation statements, not exact formatting or option values.
        // Ignore quoted text and comments: examples in a doc comment are not an implementation.
        var code = Regex.Replace(body, """//[^\r\n]*|/\*[\s\S]*?(?:\*/|\z)|"(?:\\[\s\S]|[^"\\])*"|'(?:\\[\s\S]|[^'\\])*'|`(?:\\[\s\S]|[^`\\])*`""", " ",
            RegexOptions.NonBacktracking);
        return Regex.IsMatch(code, @"
            \bwindow\s*\.\s*__FluxorDevTools__\s*=\s*new\b
            [\s\S]*?\bconst\s+reduxDevTools\s*=\s*window\s*\.\s*__REDUX_DEVTOOLS_EXTENSION__\b
            [\s\S]*?\bconst\s+fluxorDevTools\s*=\s*reduxDevTools\s*\.\s*connect\s*\(
            [\s\S]*?\bthis\s*\.\s*init\s*=\s*function\s*\(\s*dotNetCallbacks\s*,\s*state\s*\)\s*\{
            [^{}]*?\bstate\s*=\s*JSON\s*\.\s*parse\s*\(\s*state\s*\)
            [^{}]*?\bfluxorDevTools\s*\.\s*init\s*\(\s*state\s*\)
            [\s\S]*?\bthis\s*\.\s*dispatch\s*=\s*function\s*\(\s*action\s*,\s*state\s*,\s*stackTrace\s*\)\s*\{
            [^{}]*?\baction\s*=\s*JSON\s*\.\s*parse\s*\(\s*action\s*\)
            [^{}]*?\bstate\s*=\s*JSON\s*\.\s*parse\s*\(\s*state\s*\)
            [^{}]*?\bfluxorDevTools\s*\.\s*send\s*\(\s*action\s*,\s*state\s*\)",
            RegexOptions.IgnorePatternWhitespace | RegexOptions.NonBacktracking);
    }

    private static string? Attribute(string tag, string name)
    {
        var attrs = Regex.Matches(tag, """\s+(?<name>[^\s=<>/]+)(?:\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s>]+)))?""");
        foreach (Match attr in attrs)
            if (attr.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                return WebUtility.HtmlDecode(attr.Groups["v"].Value);
        return null;
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string mediaType, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType) };

    private static HttpClient TestClient(string script, Func<Uri, HttpResponseMessage> asset) =>
        new(new StubHandler(uri => Routes.Contains(uri.AbsolutePath)
            ? Response(HttpStatusCode.OK, "text/html", $"<html><head><base href='/'></head><body>{Bootstrap}{script}</body></html>")
            : asset(uri))) { BaseAddress = new Uri("http://127.0.0.1:12345/") };

    private sealed class StubHandler(Func<Uri, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request.RequestUri!));
    }
}
