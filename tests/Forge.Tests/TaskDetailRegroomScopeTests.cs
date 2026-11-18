using System.Net;
using System.Reflection;
using System.Text.Json;
using Forge.Dashboard.Components.Pages;
using Xunit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Tests;

public sealed class TaskDetailRegroomScopeTests
{
    [Theory]
    [InlineData("task")]
    [InlineData("state")]
    [InlineData("runs")]
    [InlineData("history")]
    [InlineData("workers")]
    [InlineData("projects")]
    public async Task DelayedOldLoadCannotReplaceNewTaskOrAuthorizeItsActions(string delayedStage)
    {
        using var services = Services();
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = false;
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var stage = path == "/api/projects" ? "projects" : path.Contains("agent-runs") ? "runs" : path.EndsWith("/state") ? "state" : path.EndsWith("/history") ? "history" : path.EndsWith("/workers") ? "workers" : "task";
            if (!delayed && stage == delayedStage) { delayed = true; return reply.Task; }
            return Task.FromResult(Response(stage, "New task"));
        })) { BaseAddress = new("http://localhost") };
        var component = new TaskDetail { TaskId = "task-old", Project = "forge" };
        InjectState(component, services);
        typeof(TaskDetail).GetProperty("Http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, http);
        var old = Invoke(component, "OnParametersSetAsync");
        var activity = delayedStage is "state" or "runs" or "workers" or "history";
        if (activity)
        {
            await old;
            Assert.False(delayed); // Overview must not request task activity.
            typeof(TaskDetail).GetField("_section", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, "History & runs");
            old = Invoke(component, "LoadActivityAsync");
        }
        Assert.True(delayed);
        Assert.Equal(activity, Property<bool>(component, "CanActOnLoadedTask"));
        component.TaskId = "task-new"; component.Project = "alpha";
        await Invoke(component, "OnParametersSetAsync");
        Assert.Equal("alpha|task-new", Field<string>(component, "_loadedRecoveryTarget"));
        reply.SetResult(Response(delayedStage, "Old task")); await old;
        var task = Field<JsonElement?>(component, "_task")!.Value;
        Assert.Equal("New task", task.GetProperty("title").GetString());
        Assert.Equal("alpha|task-new", Field<string>(component, "_loadedRecoveryTarget"));
        Assert.True(Property<bool>(component, "CanActOnLoadedTask"));
        Assert.Equal("Overview", Field<string>(component, "_section"));
        Assert.Null(Field<object?>(component, "_state"));
        foreach (var field in new[] { "_runs", "_events", "_workers" })
            Assert.Empty((System.Collections.IEnumerable)Field<object>(component, field));
    }

    [Fact]
    public async Task FailedTaskRefreshDisablesRecoveryActions()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))))
            { BaseAddress = new("http://localhost") };
        using var services = Services();
        var component = new TaskDetail { TaskId = "task-3", Project = "forge" };
        InjectState(component, services);
        typeof(TaskDetail).GetProperty("Http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, http);
        await Invoke(component, "OnParametersSetAsync");
        Assert.False(Property<bool>(component, "CanActOnLoadedTask"));
        Assert.Null(Field<string?>(component, "_loadedRecoveryTarget"));
    }

    private static HttpResponseMessage Response(string stage, string title) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(stage switch
        {
            "task" => JsonSerializer.Serialize(new { id = "task", title, status = "Pending", events = Array.Empty<object>() }),
            "projects" => "[]",
            "state" => JsonSerializer.Serialize(new { state = title }),
            "history" => JsonSerializer.Serialize(new { events = new[] { new { kind = "note", detail = title, at = DateTimeOffset.UtcNow, actor = "fixture" } } }),
            "workers" => JsonSerializer.Serialize(new { available = true, attempts = new[] { new { id = title, role = "CoreDev", state = "running", createdAt = "2026-11-17T00:00:00Z", updatedAt = "2026-11-17T00:00:00Z" } } }),
            "runs" => JsonSerializer.Serialize(new { active = new[] { new { id = title, role = "CoreDev", status = "running", startedAt = DateTimeOffset.UtcNow } }, recent = Array.Empty<object>() }),
            _ => "{}"
        }, System.Text.Encoding.UTF8, "application/json")
    };
    private static ServiceProvider Services()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRazorComponents();
        return builder.Services.BuildServiceProvider();
    }
    private static void InjectState(TaskDetail component, IServiceProvider services) =>
        typeof(TaskDetail).GetProperty("PersistentState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, services.GetRequiredService<PersistentComponentState>());
    private static T Field<T>(TaskDetail c, string name) => (T)typeof(TaskDetail).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c)!;
    private static T Property<T>(TaskDetail c, string name) => (T)typeof(TaskDetail).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c)!;
    private static Task Invoke(TaskDetail c, string name) => (Task)typeof(TaskDetail).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(c, null)!;
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
