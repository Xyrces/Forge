using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.JSInterop;
using Xunit;
using TaskList = Forge.Dashboard.Components.Pages.Tasks;

namespace Forge.Tests;

public sealed class TaskListNavigationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task DelayedOldProjectStorageCannotReplaceNewProjectFilters()
    {
        var oldStorage = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<string>();
        using var http = CreateClient(requests, _ => Task.FromResult(CreateResponse()));
        var browser = new Browser(key => key == "tasks:old"
            ? oldStorage.Task
            : Task.FromResult("""{"view":"attention","search":"new search","pages":1}"""));
        var page = CreatePage(http, browser);
        page.Project = "old";
        var oldRestore = Invoke(page, "RestoreAsync");

        page.Project = "new";
        await Invoke(page, "RestoreAsync");
        oldStorage.SetResult("""{"view":"history","search":"old search","pages":50}""");
        await oldRestore;

        Assert.Equal("attention", ReadField<string>(page, "_view"));
        Assert.Equal("new search", ReadField<string>(page, "_search"));
        Assert.Single(requests);
        Assert.Contains("projectId=new", requests[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserFilterOrSearchStopsPendingSavedPageReplay(bool search)
    {
        var oldResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<string>();
        using var http = CreateClient(requests, _ => requests.Count == 1
            ? oldResponse.Task
            : Task.FromResult(CreateResponse("next-page")));
        var browser = new Browser(_ => Task.FromResult("""{"view":"history","pages":50}"""));
        var page = CreatePage(http, browser);
        page.Project = "forge";
        var restore = Invoke(page, "RestoreAsync");

        if (search)
        {
            typeof(TaskList).GetField("_search", Private)!.SetValue(page, "changed search");
            await Invoke(page, "LoadFromUserAsync", false);
        }
        else
        {
            await Invoke(page, "SelectView", "attention");
        }
        oldResponse.SetResult(CreateResponse("stale-cursor"));
        await restore;

        Assert.Equal(2, requests.Count);
        Assert.False(ReadField<bool>(page, "_restoreScroll"));
        Assert.Equal("next-page", ReadField<string>(page, "_cursor"));
        Assert.Equal(1, ReadField<int>(page, "_pages"));
        Assert.Contains(search ? "search=changed%20search" : "view=attention", requests[1]);
    }

    private static TaskList CreatePage(HttpClient http, IJSObjectReference browser)
    {
        var page = new TaskList();
        typeof(TaskList).GetProperty("Http", Private)!.SetValue(page, http);
        typeof(TaskList).GetField("_browser", Private)!.SetValue(page, browser);
        return page;
    }

    private static T ReadField<T>(TaskList page, string field)
        => (T)typeof(TaskList).GetField(field, Private)!.GetValue(page)!;

    private static Task Invoke(TaskList page, string method, params object[] args)
        => (Task)typeof(TaskList).GetMethod(method, Private)!.Invoke(page, args)!;

    private static HttpClient CreateClient(
        List<string> requests,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new Handler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return respond(request);
        });
        return new HttpClient(handler) { BaseAddress = new("http://localhost") };
    }

    private static HttpResponseMessage CreateResponse(string? cursor = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            items = Array.Empty<object>(),
            nextCursor = cursor,
            total = 0
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request);
    }

    private sealed class Browser(Func<string, Task<string>> read) : IJSObjectReference
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<T> InvokeAsync<T>(string id, object?[]? args)
            => InvokeAsync<T>(id, CancellationToken.None, args);

        public async ValueTask<T> InvokeAsync<T>(string id, CancellationToken ct, object?[]? args)
        {
            var json = await read((string)args![0]!);
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
    }
}
