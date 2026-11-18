using System.Net;
using System.Reflection;
using System.Text.Json;
using Forge.Dashboard.Components.Pages;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Forge.Tests;

public sealed class TaskDetailSectionHydrationTests
{
    [Theory]
    [InlineData("Review")]
    [InlineData("History & runs")]
    public async Task DirectSectionHydratesWithoutRepeatingReadsAndStillNavigates(string section)
    {
        var requests = new List<string>();
        using var http = new HttpClient(new FixtureHandler(requests)) { BaseAddress = new("http://localhost") };
        using var prerenderServices = Services();
        var prerender = Component(http, prerenderServices, section);
        await Invoke(prerender, "OnParametersSetAsync");
        var task = (JsonElement)Field(prerender, "_task")!;
        var snapshot = typeof(TaskDetail).GetMethod("CaptureOverviewSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(prerender, [task])!;
        var serialized = JsonSerializer.SerializeToUtf8Bytes(snapshot, snapshot.GetType());
        using var interactiveServices = Services();
        await interactiveServices.GetRequiredService<ComponentStatePersistenceManager>().RestoreStateAsync(new StateStore(serialized));
        var interactive = Component(http, interactiveServices, section);
        var before = requests.Count;
        await Invoke(interactive, "OnParametersSetAsync");
        Assert.Equal(before, requests.Count);
        if (section == "Review")
        {
            Assert.True((bool)Field(interactive, "_legacyLoaded")!);
            Assert.Single(requests.Where(p => p.Contains("review-history")));
            Assert.Single((System.Collections.IEnumerable)Field(interactive, "_nativeReviews")!);
        }
        else
        {
            Assert.True((bool)Field(interactive, "_activityLoaded")!);
            Assert.Equal("older", Field(interactive, "_historyCursor"));
            await Invoke(interactive, "LoadHistoryAsync", true);
            Assert.Contains(requests, p => p.Contains("cursor=older"));
        }
        interactive.TaskId = "next-task";
        await Invoke(interactive, "OnParametersSetAsync");
        Assert.Contains(requests, p => p.Contains("next-task"));
        prerender.Dispose(); interactive.Dispose();
    }

    private static TaskDetail Component(HttpClient http, IServiceProvider services, string section)
    {
        var component = new TaskDetail { TaskId = "task-1", Project = "forge", Section = section };
        typeof(TaskDetail).GetProperty("Http", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, http);
        typeof(TaskDetail).GetProperty("PersistentState", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, services.GetRequiredService<PersistentComponentState>());
        return component;
    }
    private static ServiceProvider Services()
    {
        var builder = WebApplication.CreateBuilder(); builder.Services.AddRazorComponents();
        return builder.Services.BuildServiceProvider();
    }
    private static object? Field(TaskDetail component, string field) => typeof(TaskDetail).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component);
    private static Task Invoke(TaskDetail component, string method, params object[] arguments) => (Task)typeof(TaskDetail).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, arguments)!;
    private sealed class StateStore(byte[] state) : IPersistentComponentStateStore
    {
        public Task<IDictionary<string, byte[]>> GetPersistedStateAsync() => Task.FromResult<IDictionary<string, byte[]>>(new Dictionary<string, byte[]> { ["forge-task-overview:forge|task-1"] = state });
        public Task PersistStateAsync(IReadOnlyDictionary<string, byte[]> state) => Task.CompletedTask;
    }
    private sealed class FixtureHandler(List<string> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery; requests.Add(path);
            var body = path.Contains("review-history") ? "{\"reviews\":[],\"notes\":[],\"nativeReviews\":[{\"id\":1,\"verdict\":\"Approved\",\"summary\":\"Accepted\",\"reviewer\":\"human\",\"role\":\"Human\",\"comments\":[]}]}"
                : path.Contains("/history") ? "{\"events\":[{\"at\":\"2026-11-18T00:00:00Z\",\"kind\":\"note\",\"detail\":\"Recorded\",\"actor\":\"human\"}],\"nextCursor\":\"older\"}"
                : path.Contains("/workers") ? "{\"available\":true,\"attempts\":[]}"
                : path.Contains("/agent-runs") ? "{\"active\":[],\"recent\":[]}"
                : path.Contains("/projects") ? "[]"
                : path.Contains("/state") || path.Contains("/human-review") ? "{}"
                : "{\"id\":\"task-1\",\"title\":\"Task\",\"status\":\"Pending\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
