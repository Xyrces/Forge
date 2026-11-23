using System.Net;
using System.Reflection;
using System.Text;
using Forge.Dashboard.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class ProjectWorkerEnvironmentUiTests
{
    [Fact]
    public async Task DefinitionSaveIsScopedAndVersioned_WithoutRequestingActivation()
    {
        var writes = new List<string>();
        using var fixture = new Fixture(async request =>
        {
            Assert.Equal("/api/projects/project-a/worker-environment", request.RequestUri!.PathAndQuery);
            if (request.Method == HttpMethod.Get) return Json("0");
            Assert.Equal(HttpMethod.Put, request.Method);
            writes.Add(await request.Content!.ReadAsStringAsync());
            return Json("1", "Dockerfile.worker");
        });
        await fixture.Invoke("OnParametersSetAsync");
        fixture.Set("_dockerfile", "Dockerfile.worker");
        await fixture.Invoke("SaveAsync");
        Assert.Single(writes);
        Assert.Contains("\"expectedRevision\":\"0\"", writes[0]);
        Assert.Contains("\"dockerfilePath\":\"Dockerfile.worker\"", writes[0]);
        Assert.DoesNotContain("approvedImage", writes[0]);
        Assert.Contains("No build was started", fixture.Field<string>("_notice"));
    }

    [Fact]
    public async Task ProjectSwitchDiscardsStaleSave()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(request => request.Method == HttpMethod.Put ? reply.Task : Task.FromResult(Json("0")));
        await fixture.Invoke("OnParametersSetAsync");
        fixture.Set("_dockerfile", "Dockerfile.worker");
        var save = fixture.Invoke("SaveAsync");
        fixture.Component.ProjectId = "project-b";
        await fixture.Invoke("OnParametersSetAsync");
        reply.SetResult(Json("1", "OldProject.Dockerfile"));
        await save;
        Assert.Equal("", fixture.Field<string>("_dockerfile"));
        Assert.Null(fixture.Field<string?>("_notice"));
        Assert.False(fixture.Field<bool>("_busy"));
    }

    [Fact]
    public async Task ConflictPreservesOperatorDraftAndShowsNeedToReload()
    {
        using var fixture = new Fixture(request => Task.FromResult(request.Method == HttpMethod.Get ? Json("0") :
            new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{\"error\":\"Environment changed. Reload and retry.\"}", Encoding.UTF8, "application/json") }));
        await fixture.Invoke("OnParametersSetAsync"); fixture.Set("_dockerfile", "Dockerfile.worker");
        await fixture.Invoke("SaveAsync");
        Assert.Equal("Dockerfile.worker", fixture.Field<string>("_dockerfile"));
        Assert.Contains("Reload", fixture.Field<string>("_error"));
        Assert.Null(fixture.Field<string?>("_notice"));
    }

    private static HttpResponseMessage Json(string revision, string? dockerfile = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { revision, definitionRevision = revision,
            dockerfilePath = dockerfile, buildContext = ".", status = dockerfile is null ? "stock" : "pending" }), Encoding.UTF8, "application/json")
    };
    private sealed class Fixture : IDisposable
    {
        public ProjectWorkerEnvironment Component { get; } = new() { ProjectId = "project-a" };
        private readonly HttpClient http;
        public Fixture(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        {
            http = new(new Handler(send)) { BaseAddress = new("http://localhost") };
            typeof(ProjectWorkerEnvironment).GetProperty("Http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Component, http);
            typeof(ProjectWorkerEnvironment).GetProperty("Logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Component, NullLogger<ProjectWorkerEnvironment>.Instance);
        }
        public void Set(string field, object value) => typeof(ProjectWorkerEnvironment).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Component, value);
        public T Field<T>(string field) => (T)typeof(ProjectWorkerEnvironment).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Component)!;
        public Task Invoke(string method) => (Task)typeof(ProjectWorkerEnvironment).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Component, null)!;
        public void Dispose() { Component.Dispose(); http.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
