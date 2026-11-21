using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Projects;
using Forge.Tests.Integration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class ProjectVisionEndpointTests : IAsyncLifetime
{
    private readonly string _dir = TempRoot.Instance.NewDirectory("vision-map-http");
    private ProjectContextFactory _projects = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private MemoryStore _shared = null!;
    private readonly Conversation _conversation = new();
    private string Source(string id) => Path.Combine(_dir, id, "docs", "MASTER_DESIGN.md");
    private static string Endpoint(string id, string operation) => $"/api/projects/{id}/vision/{operation}";

    public async Task InitializeAsync()
    {
        var projects = new[] { "primary", "secondary" }
            .Select(id => new ProjectOptions { Id = id, Root = Path.Combine(_dir, id) }).ToArray();
        _projects = new ProjectContextFactory(projects, projects.ToDictionary(p => p.Id, p => Path.Combine(p.Root, "test.db")));
        var primary = new VisionStore(projects[0].Root, "docs/MASTER_DESIGN.md");
        foreach (var p in projects)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Source(p.Id))!);
            await File.WriteAllTextAsync(Source(p.Id), $"# {p.Id}\r\n\r\n## Goal\r\n\r\nOriginal wording.\r\n", new UTF8Encoding(true));
        }
        primary.Reload();
        _shared = new MemoryStore(((IssueStore)_projects.Find("primary")!.Issues).Db);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _dir });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        VisionEndpoints.MapVisionEndpoints(_app, primary, NullLogger.Instance, _shared, projectContexts: _projects, conversation: _conversation);
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_app.GetPort()}") };
    }

    [Fact]
    public async Task ChatAndApplyPreserveTheSourceAndRouteTheApprovalToItsOwner()
    {
        var before = await File.ReadAllBytesAsync(Source("secondary"));
        var doc = await _http.GetFromJsonAsync<VisionMapDocument>(Endpoint("secondary", "map"));
        Assert.Equal(doc!.Source, string.Concat(doc.Nodes.Select(n => n.Text)));
        var chat = await _http.PostAsJsonAsync(Endpoint("secondary", "chat"), new VisionEndpoints.VisionChatRequest(
            doc.Revision, [new("user", "Make the goal clearer.")], []));
        chat.EnsureSuccessStatusCode();
        var draft = await chat.Content.ReadFromJsonAsync<VisionReply>();
        Assert.Equal("secondary", _conversation.Project);
        Assert.Equal(before, await File.ReadAllBytesAsync(Source("secondary")));
        var applied = await _http.PostAsJsonAsync(Endpoint("secondary", "apply"), new VisionEndpoints.VisionApplyRequest(doc.Revision, draft!.Edits));
        applied.EnsureSuccessStatusCode();
        var updated = await applied.Content.ReadFromJsonAsync<VisionMapDocument>();
        var expected = doc.Source.Replace("Original wording.", "Clearer wording.", StringComparison.Ordinal);
        Assert.Equal(expected, updated!.Source);
        Assert.Equal(before, await File.ReadAllBytesAsync(Source("secondary")));
        var upstream = await _http.GetFromJsonAsync<JsonElement>(Endpoint("secondary", "repository"));
        Assert.Equal(doc.Source, upstream.GetProperty("content").GetString());
        var owner = new MemoryStore(((IssueStore)_projects.Find("secondary")!.Issues).Db);
        Assert.Equal(expected, (await owner.GetAsync("vision/secondary"))!.Body);
        Assert.Equal(expected, (await _shared.GetAsync("vision/secondary"))!.Body);
        Assert.Null(await _shared.GetAsync("vision/master"));
        Assert.DoesNotContain("Clearer", await File.ReadAllTextAsync(Source("primary")));
    }

    [Fact]
    public async Task ConcurrentEditorsHaveOneWinnerAndStaleChatMakesNoModelCall()
    {
        var doc = (await _http.GetFromJsonAsync<VisionMapDocument>(Endpoint("primary", "map")))!;
        var node = doc.Nodes.Single(n => n.Title == "Goal");
        var responses = await Task.WhenAll(new[] { "One", "Two" }.Select(text => _http.PostAsJsonAsync(
            Endpoint("primary", "apply"), new VisionEndpoints.VisionApplyRequest(doc.Revision, [new(node.Id, "replace", "Original wording.", text)]))));
        Assert.Single(responses, r => r.IsSuccessStatusCode);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        var current = (await _http.GetFromJsonAsync<VisionMapDocument>(Endpoint("primary", "map")))!.Source;
        Assert.Equal(current, (await _shared.GetAsync("vision/master"))!.Body);
        Assert.Equal(current, (await _shared.GetAsync("vision/primary"))!.Body);
        var stale = await _http.PostAsJsonAsync(Endpoint("primary", "chat"), new VisionEndpoints.VisionChatRequest(
            doc.Revision, [new("user", "A follow-up")], []));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Null(_conversation.Project);
        var staleRaw = await _http.PutAsJsonAsync("/api/vision?projectId=primary", new VisionEndpoints.VisionUpdate("old", doc.Revision));
        Assert.Equal(HttpStatusCode.Conflict, staleRaw.StatusCode);
        Assert.Equal(current, (await _http.GetFromJsonAsync<JsonElement>("/api/vision")).GetProperty("content").GetString());
        Assert.Equal(doc.Source, await File.ReadAllTextAsync(Source("primary")));
    }

    [Fact]
    public async Task RefreshWithdrawsGuidanceWhenTheSourceWasRemoved()
    {
        var seed = await _http.PostAsync("/api/vision/refresh?projectId=primary", null);
        seed.EnsureSuccessStatusCode();
        Assert.Contains("Original wording.", (await _shared.GetAsync("vision/master"))!.Body);
        File.Delete(Source("primary"));
        var withdraw = await _http.PostAsync("/api/vision/refresh?projectId=primary", null);
        withdraw.EnsureSuccessStatusCode();
        Assert.Equal("", (await _shared.GetAsync("vision/master"))!.Body);
        Assert.Equal("", (await _shared.GetAsync("vision/primary"))!.Body);
    }

    [Fact]
    public async Task ApprovedDocumentSurvivesGuidanceFailureAndRefreshRecoversIt()
    {
        var db = ((IssueStore)_projects.Find("secondary")!.Issues).Db;
        await using var connection = await db.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_guidance BEFORE INSERT ON memory WHEN NEW.[key]='vision/secondary' BEGIN SELECT RAISE(ABORT,'fixture outage'); END";
        await command.ExecuteNonQueryAsync();
        var map = (await _http.GetFromJsonAsync<VisionMapDocument>(Endpoint("secondary", "map")))!;
        var edit = new VisionMapEdit(map.Nodes.Single(n => n.Title == "Goal").Id, "replace", "Original wording.", "Reviewed wording.");
        var applied = await _http.PostAsJsonAsync(Endpoint("secondary", "apply"), new VisionEndpoints.VisionApplyRequest(map.Revision, [edit]));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, applied.StatusCode);
        Assert.Contains("vision_memory_sync_pending", await applied.Content.ReadAsStringAsync());
        var saved = (await _http.GetFromJsonAsync<VisionMapDocument>(Endpoint("secondary", "map")))!.Source;
        Assert.Contains("Reviewed wording.", saved);
        Assert.Equal(map.Source, await File.ReadAllTextAsync(Source("secondary")));
        command.CommandText = "DROP TRIGGER fail_guidance";
        await command.ExecuteNonQueryAsync();
        var retry = await _http.PostAsync("/api/vision/refresh?projectId=secondary", null);
        retry.EnsureSuccessStatusCode();
        Assert.Equal(saved, (await new MemoryStore(db).GetAsync("vision/secondary"))!.Body);
        Assert.Equal(saved, (await _shared.GetAsync("vision/secondary"))!.Body);
    }

    [Fact]
    public async Task MissingVisionCanStartEmptyAndUnknownProjectsCannotUseThePrimary()
    {
        File.Delete(Source("secondary"));
        var empty = (await _http.GetFromJsonAsync<VisionMapDocument>(Endpoint("secondary", "map")))!;
        Assert.Equal("", empty.Source);
        var applied = await _http.PostAsJsonAsync(Endpoint("secondary", "apply"), new VisionEndpoints.VisionApplyRequest(empty.Revision,
            [new("root", "add", "", "Keep this promise.", "Mission")]));
        applied.EnsureSuccessStatusCode();
        Assert.Contains("Mission", (await applied.Content.ReadFromJsonAsync<VisionMapDocument>())!.Source);
        Assert.False(File.Exists(Source("secondary")));
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(Endpoint("missing", "map"))).StatusCode);
    }

    [Fact]
    public async Task UpdatedRepositorySourceCanBePreviewedAndExplicitlyImported()
    {
        var map = (await _http.GetFromJsonAsync<VisionMapDocument>(Endpoint("secondary", "map")))!;
        (await _http.PutAsJsonAsync("/api/vision?projectId=secondary", new VisionEndpoints.VisionUpdate("Operator's revision", map.Revision))).EnsureSuccessStatusCode();
        await File.WriteAllTextAsync(Source("secondary"), "A later upstream vision");
        (await _http.PostAsync("/api/vision/refresh?projectId=secondary", null)).EnsureSuccessStatusCode();
        var current = (await _http.GetFromJsonAsync<VisionMapDocument>(Endpoint("secondary", "map")))!;
        Assert.Equal("Operator's revision", current.Source);
        var repository = await _http.GetFromJsonAsync<JsonElement>(Endpoint("secondary", "repository"));
        var incoming = repository.GetProperty("content").GetString();
        Assert.Equal("A later upstream vision", incoming);
        (await _http.PutAsJsonAsync("/api/vision?projectId=secondary", new VisionEndpoints.VisionUpdate(incoming, current.Revision))).EnsureSuccessStatusCode();
        Assert.Equal(incoming, (await _http.GetFromJsonAsync<VisionMapDocument>(Endpoint("secondary", "map")))!.Source);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        await _projects.DisposeAsync();
    }

    private sealed class Conversation : IVisionConversation
    {
        public string? Project { get; private set; }
        public Task<VisionReply> ReplyAsync(string projectId, VisionMapDocument map, IReadOnlyList<VisionChatMessage> messages,
            IReadOnlyList<VisionMapEdit> pending, CancellationToken ct)
        {
            Project = projectId;
            return Task.FromResult(new VisionReply("The goal can be more specific.",
                [new(map.Nodes.Single(n => n.Title == "Goal").Id, "replace", "Original wording.", "Clearer wording.")]));
        }
    }
}
