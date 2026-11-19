using Forge.Configuration;
using Forge.Dashboard;
using Xunit;

namespace Forge.Tests;

public sealed class RecoveryDashboardTests
{
    [Fact]
    public async Task MaintenanceHostExposesOnlyRecoveryReadsAndRejectsEveryMutation()
    {
        var reads = 0;
        await using var app = RecoveryDashboard.Build(new DashboardOptions { Hostname = "127.0.0.1", Port = 0 }, _ =>
        {
            reads++;
            return Task.FromResult<object>(new { mode = "read-only-recovery", projects = Array.Empty<object>() });
        });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Contains("Read-only recovery mode", await client.GetStringAsync("/"));
        Assert.Equal(0, reads);
        Assert.Contains("read-only-recovery", await client.GetStringAsync("/api/recovery"));
        Assert.Equal(1, reads);
        Assert.Contains("readOnly", await client.GetStringAsync("/api/health/heartbeat"));
        foreach (var path in new[] { "/api/deployments", "/api/sprints", "/api/projects", "/api/completions" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        foreach (var path in new[] { "/api/recovery", "/api/deployments", "/api/projects", "/api/sprints/one/completion/abandon" })
        {
            using var request = new HttpRequestMessage(method, path);
            using var response = await client.SendAsync(request);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        Assert.Equal(1, reads);
        await app.StopAsync();
    }
}
