using System.Net;
using System.Reflection;
using System.Text.Json;
using Forge.Dashboard.Components.Pages;
using Xunit;

namespace Forge.Tests;

public sealed class TaskNativeReviewHistoryTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [Fact]
    public async Task NativeAndLegacyAreRetainedAndCachedPublishedReviewIsDeduplicated()
    {
        const string url = "https://github.com/example/repo/pull/1#pullrequestreview-41";
        var task = JsonSerializer.SerializeToElement(new { metadata = new Dictionary<string, string> { ["reviewHistory"] = JsonSerializer.Serialize(new[] { new { round = 1, url, verdict = "Approve" } }) } });
        var data = new { reviews = new[] { new { round = 1, verdict = "RequestChanges", summary = "Legacy", reviewer = "legacy", url = "https://github.com/example/repo/pull/1#issuecomment-1", submittedAt = "2026-11-18T00:00:00Z", findings = Array.Empty<object>() } }, notes = Array.Empty<string>(), limited = false,
            nativeReviews = new[] {
                new { id = 43, headSha = "corrected", verdict = "Approved", summary = "Correction reviewed", reviewer = "human", url = url + "3", submittedAt = "2026-11-18T03:00:00Z", role = "GitHub participant", comments = Array.Empty<object>() },
                new { id = 42, headSha = "old", verdict = "ChangesRequested", summary = "Please correct", reviewer = "human", url = url + "2", submittedAt = "2026-11-18T02:00:00Z", role = "GitHub participant", comments = new object[] { new { id = 5, author = "human", path = "a.cs", body = "Incorrect condition", diffHunk = "@@ -1 +1 @@", url = "https://github.com/example/repo/pull/1#discussion_r5", originalHead = "old", inReplyToId = (long?)null } } },
                new { id = 41, headSha = "old", verdict = "Approved", summary = "Automated", reviewer = "reviewer[bot]", url, submittedAt = "2026-11-18T01:00:00Z", role = "Forge reviewer App", comments = Array.Empty<object>() }
            } };
        using var http = new HttpClient(new Handler(JsonSerializer.Serialize(data))) { BaseAddress = new("http://localhost") };
        var component = new TaskDetail { TaskId = "task", Project = "forge" };
        typeof(TaskDetail).GetProperty("Http", Private)!.SetValue(component, http);
        typeof(TaskDetail).GetField("_task", Private)!.SetValue(component, task);
        await (Task)typeof(TaskDetail).GetMethod("LoadLegacyReviewsAsync", Private)!.Invoke(component, null)!;
        Assert.Equal(3, ((System.Collections.ICollection)typeof(TaskDetail).GetField("_nativeReviews", Private)!.GetValue(component)!).Count);
        Assert.Single((System.Collections.IEnumerable)typeof(TaskDetail).GetField("_legacyReviews", Private)!.GetValue(component)!);
        Assert.Empty((System.Collections.IEnumerable)typeof(TaskDetail).GetProperty("ReviewHistory", Private)!.GetValue(component)!);
    }

    [Fact]
    public async Task NativeHistoryOutageKeepsStoredReviewVisible()
    {
        using var http = new HttpClient(new Handler("unavailable", HttpStatusCode.BadGateway)) { BaseAddress = new("http://localhost") };
        var component = new TaskDetail { TaskId = "task", Project = "forge" };
        typeof(TaskDetail).GetProperty("Http", Private)!.SetValue(component, http);
        typeof(TaskDetail).GetField("_task", Private)!.SetValue(component, JsonSerializer.SerializeToElement(new { metadata = new Dictionary<string,string> { ["reviewHistory"] = "[{\"round\":1,\"verdict\":\"Approve\",\"summary\":\"Cached evidence\"}]" } }));
        await (Task)typeof(TaskDetail).GetMethod("LoadLegacyReviewsAsync", Private)!.Invoke(component, null)!;
        Assert.Single((System.Collections.IEnumerable)typeof(TaskDetail).GetProperty("ReviewHistory", Private)!.GetValue(component)!);
        Assert.NotNull(typeof(TaskDetail).GetField("_legacyError", Private)!.GetValue(component));
        Assert.False((bool)typeof(TaskDetail).GetField("_legacyLoaded", Private)!.GetValue(component)!);
    }

    [Theory]
    [InlineData("Commented", "Advisory comments")]
    [InlineData("Dismissed", "Dismissed review")]
    [InlineData("ChangesRequested", "Changes requested at this review")]
    [InlineData("Approved", "Approval recorded")]
    public void HistoryLabelsDoNotInventCurrentAction(string state, string expected) => Assert.Equal(expected,
        typeof(TaskDetail).GetMethod("NativeDecisionLabel", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [state]));

    private sealed class Handler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
    }
}
