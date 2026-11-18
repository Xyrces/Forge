using System.Net;
using System.Reflection;
using System.Text;
using Forge.Dashboard.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class ProjectReviewPolicyUiTests
{
    [Fact]
    public async Task SaveUsesSelectedProjectAndMode_AndDoubleClickDoesNotRepeat()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var puts = 0;
        using var fixture = new Fixture(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json("Automated");
            puts++;
            Assert.Equal("/api/projects/forge/review-policy", request.RequestUri!.PathAndQuery);
            Assert.Contains("Both", await request.Content!.ReadAsStringAsync());
            return await reply.Task;
        });
        await fixture.Invoke("OnParametersSetAsync");
        fixture.Set("_mode", "Both");
        var saving = fixture.Invoke("SaveAsync");
        await fixture.Invoke("SaveAsync");
        Assert.Equal(1, puts);
        reply.SetResult(Json("Both")); await saving;
        Assert.Equal("Both", fixture.Field<string>("_savedMode"));
        Assert.Contains("saved", fixture.Field<string>("_notice"));
    }

    [Fact]
    public async Task ProjectChangeDiscardsOldSaveAndDoesNotLeaveNewSelectorDisabled()
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(request => request.Method == HttpMethod.Put ? reply.Task : Task.FromResult(Json("Automated")));
        await fixture.Invoke("OnParametersSetAsync"); fixture.Set("_mode", "Human");
        var saving = fixture.Invoke("SaveAsync");
        fixture.Component.ProjectId = "other";
        await fixture.Invoke("OnParametersSetAsync");
        reply.SetResult(Json("Human")); await saving;
        Assert.False(fixture.Field<bool>("_saving"));
        Assert.Equal("Automated", fixture.Field<string>("_savedMode"));
        Assert.Null(fixture.Field<string?>("_notice"));
    }

    [Fact]
    public async Task NominationOnlyChangeAndExplicitClearAreSaved()
    {
        var bodies = new List<string>();
        using var fixture = new Fixture(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json("Automated");
            var body = await request.Content!.ReadAsStringAsync(); bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body,Encoding.UTF8,"application/json") };
        });
        await fixture.Invoke("OnParametersSetAsync");
        fixture.Set("_nominee","Owner"); await fixture.Invoke("SaveAsync");
        Assert.Equal("Owner",fixture.Field<string>("_savedNominee"));
        fixture.Set("_nominee",""); await fixture.Invoke("SaveAsync");
        Assert.Equal(2,bodies.Count);
        Assert.Contains("Owner",bodies[0]);
        Assert.Contains("\"nominatedHuman\":\"\"",bodies[1]);
    }

    private static HttpResponseMessage Json(string mode) => new(HttpStatusCode.OK)
    { Content = new StringContent("{\"mode\":\"" + mode + "\"}", Encoding.UTF8, "application/json") };
    private sealed class Fixture : IDisposable
    {
        public ProjectReviewPolicy Component { get; } = new() { ProjectId = "forge" };
        private readonly HttpClient http;
        public Fixture(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        {
            http = new(new Handler(send)) { BaseAddress = new("http://localhost") };
            typeof(ProjectReviewPolicy).GetProperty("Http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Component, http);
            typeof(ProjectReviewPolicy).GetProperty("Logger", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Component, NullLogger<ProjectReviewPolicy>.Instance);
        }
        public void Set(string field, object value) => typeof(ProjectReviewPolicy).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Component, value);
        public T Field<T>(string field) => (T)typeof(ProjectReviewPolicy).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Component)!;
        public Task Invoke(string method) => (Task)typeof(ProjectReviewPolicy).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Component, null)!;
        public void Dispose() { Component.Dispose(); http.Dispose(); }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
