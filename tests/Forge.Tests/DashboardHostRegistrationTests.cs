using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class DashboardHostRegistrationTests
{
    [Fact]
    public async Task ConfiguredLoopbackPortServesTaskRoutesWithoutCodebaseServices()
    {
        var directory = TempRoot.Instance.NewDirectory("dashboard-registration");
        await using var issues = new IssueStore(Path.Combine(directory, "issues.db"), "qualification");
        var task = await issues.CreateAsync(new NewIssue("client", "Registered independently of codebase analysis"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var tokenFile = Path.Combine(directory, "operator-token");
        await File.WriteAllTextAsync(tokenFile, Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
        await using var host = new DashboardHost(new() { Hostname = "127.0.0.1", Port = port }, new(),
            issues, new AgentStore(issues), new SkillStore(issues), new SprintStore(issues),
            new AgentMessageBus(), new InMemoryDashboardEventBus(), NullLogger<DashboardHost>.Instance,
            mutationTokenFile: tokenFile);
        await host.StartAsync(default);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 30 && response is null; attempt++)
        {
            try { response = await client.GetAsync($"/api/tasks/{task.Id}?projectId=qualification&includeEvents=false"); }
            catch (HttpRequestException) when (attempt < 29) { await Task.Delay(100); }
        }
        using (response)
        {
            Assert.NotNull(response);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(task.Id, body.GetProperty("id").GetString());
            Assert.Equal(task.Title, body.GetProperty("title").GetString());
        }
        using var history = await client.GetAsync($"/api/tasks/{task.Id}/history?projectId=qualification");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var denied = await client.PutAsJsonAsync($"/api/tasks/{task.Id}/human-review?projectId=qualification", new { required = true });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }
}
