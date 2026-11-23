using System.Net;
using System.Net.Http.Json;
using Forge.Core;
using Forge.Dashboard;
using Forge.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Forge.Tests;

public sealed class ProjectWorkerEnvironmentEndpointTests : IAsyncLifetime
{
    private readonly IssueStore _issues = new(TempRoot.Instance.NewDbPath("worker-environment-http"));
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private const string Endpoint = "/api/projects/secondary/worker-environment";
    private const string Token = "operator-fixture-token-at-least-32-characters";

    public async Task InitializeAsync()
    {
        var projects = new ProjectStore(_issues);
        await projects.UpsertAsync(new("primary", "Primary", "https://example.invalid/primary"));
        await projects.UpsertAsync(new("secondary", "Secondary", "https://example.invalid/secondary"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IProjectWorkerEnvironmentStore>(new ProjectWorkerEnvironmentStore(projects));
        _app = builder.Build();
        new DashboardMutationAuthentication(Token).Protect(_app);
        _app.MapProjectWorkerEnvironmentEndpoints();
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_app.GetPort()}") };
        _http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
    }

    [Fact]
    public async Task UnauthenticatedWorkersCannotSaveOrQualifyAnImage()
    {
        _http.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PutAsJsonAsync(Endpoint, new SetWorkerEnvironmentDefinition("0", "Dockerfile"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PostAsJsonAsync(Endpoint + "/qualify", new QualifyWorkerEnvironment("0", "0", "image", "commit"))).StatusCode);
        Assert.Equal("stock", (await _http.GetFromJsonAsync<ProjectWorkerEnvironment>(Endpoint))!.Status);
    }

    [Fact]
    public async Task OperatorRoundTripRoutesProjectAndRejectsStalePublication()
    {
        var initial = (await _http.GetFromJsonAsync<ProjectWorkerEnvironment>(Endpoint))!;
        var saved = await _http.PutAsJsonAsync(Endpoint, new SetWorkerEnvironmentDefinition(initial.Revision, "Dockerfile"));
        saved.EnsureSuccessStatusCode();
        var definition = (await saved.Content.ReadFromJsonAsync<ProjectWorkerEnvironment>())!;
        var request = new QualifyWorkerEnvironment(definition.Revision, definition.DefinitionRevision,
            "registry.example.invalid/worker@sha256:" + new string('a', 64), new string('b', 40));
        var activated = await _http.PostAsJsonAsync(Endpoint + "/qualify", request);
        activated.EnsureSuccessStatusCode();
        var active = (await activated.Content.ReadFromJsonAsync<ProjectWorkerEnvironment>())!;
        Assert.Equal("active", active.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PostAsJsonAsync(Endpoint + "/qualify", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PutAsJsonAsync(Endpoint, new SetWorkerEnvironmentDefinition(initial.Revision, null))).StatusCode);
        Assert.Equal("stock", (await _http.GetFromJsonAsync<ProjectWorkerEnvironment>(Endpoint.Replace("secondary", "primary")))!.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(Endpoint.Replace("secondary", "missing"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PutAsJsonAsync(Endpoint, new SetWorkerEnvironmentDefinition(active.Revision, "../Dockerfile"))).StatusCode);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _issues.Dispose();
    }
}
