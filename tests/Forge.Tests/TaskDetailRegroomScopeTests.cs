using System.Net;
using System.Reflection;
using System.Text.Json;
using Forge.Dashboard.Components.Pages;
using Xunit;

namespace Forge.Tests;

public sealed class TaskDetailRegroomScopeTests
{
    [Theory]
    [InlineData("task")]
    [InlineData("state")]
    [InlineData("runs")]
    [InlineData("projects")]
    public async Task DelayedOldLoadCannotReplaceNewTaskOrAuthorizeItsActions(string delayedStage)
    {
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = false;
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var stage = path == "/api/projects" ? "projects" : path.Contains("agent-runs") ? "runs" : path.EndsWith("/state") ? "state" : "task";
            if (!delayed && stage == delayedStage) { delayed = true; return reply.Task; }
            return Task.FromResult(Response(stage, "New task"));
        })) { BaseAddress = new("http://localhost") };
        var component = new TaskDetail { TaskId = "task-old", Project = "forge" };
        typeof(TaskDetail).GetProperty("Http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, http);
        var old = Invoke(component, "OnParametersSetAsync");
        Assert.True(delayed);
        Assert.False(Property<bool>(component, "CanActOnLoadedTask"));
        component.TaskId = "task-new"; component.Project = "alpha";
        await Invoke(component, "OnParametersSetAsync");
        Assert.Equal("alpha|task-new", Field<string>(component, "_loadedRecoveryTarget"));
        reply.SetResult(Response(delayedStage, "Old task")); await old;
        var task = Field<JsonElement?>(component, "_task")!.Value;
        Assert.Equal("New task", task.GetProperty("title").GetString());
        Assert.Equal("alpha|task-new", Field<string>(component, "_loadedRecoveryTarget"));
        Assert.True(Property<bool>(component, "CanActOnLoadedTask"));
    }

    [Fact]
    public async Task FailedTaskRefreshDisablesRecoveryActions()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))))
            { BaseAddress = new("http://localhost") };
        var component = new TaskDetail { TaskId = "task-3", Project = "forge" };
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
            _ => "{}"
        }, System.Text.Encoding.UTF8, "application/json")
    };
    private static T Field<T>(TaskDetail c, string name) => (T)typeof(TaskDetail).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c)!;
    private static T Property<T>(TaskDetail c, string name) => (T)typeof(TaskDetail).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c)!;
    private static Task Invoke(TaskDetail c, string name) => (Task)typeof(TaskDetail).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(c, null)!;
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
