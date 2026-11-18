using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Forge.Core;
using Forge.Dashboard;
using Forge.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Forge.Configuration;
using Forge.Orchestrator.Control;
using Xunit;

namespace Forge.Tests;

public sealed class ProjectProvisioningTests
{
    [Fact]
    public async Task ColdBootstrapRejectsAnotherProjectBeforeOpeningSql()
    {
        var options = new AgentOptions();
        options.Db.Provider = "sqlserver";
        options.Controller.Mode = "initialize";
        options.Controller.DatasetId = Guid.NewGuid().ToString();
        options.Bootstrap.ProjectId = "second";
        options.Bootstrap.ControllerPrincipal = "controller";
        options.Bootstrap.WorkerPrincipal = "worker";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ControllerInitializer.RunAsync(options,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance));
        Assert.Contains("only the Forge project", error.Message);
    }

    [Fact]
    public async Task DeniedSupervisorCreationFailsWithoutSqlAttestation()
    {
        var options = new AgentOptions();
        options.Worker.Image = "registry/forge@sha256:" + new string('a', 64);
        options.Worker.Namespace = "fixture";
        options.Controller.DatasetId = Guid.NewGuid().ToString();
        using var client = new HttpClient(new DeniedJobs()) { BaseAddress = new Uri("https://cluster/") };
        var provisioner = new KubernetesProjectProvisioner(() => client, options)
        { RuntimeFactory = _ => throw new Exception("Must not attest failed provisioning") };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.EnsureReadyAsync("second", default));
        Assert.Contains("could not be created", error.Message);
    }

    private sealed class DeniedJobs : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Get ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden));
    }

    [Fact]
    public async Task HttpRegistrationPublishesOnlyAfterQualificationAndAllowsRetry()
    {
        var directory = TempRoot.Instance.NewDirectory("project-provision-http");
        await using var issues = new IssueStore(Path.Combine(directory, "registry.db"));
        await using var projects = new ProjectStore(issues);
        await projects.UpsertAsync(new("existing", "Existing", "file:///existing", "main"));
        var provisioner = new Provisioner(projects);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IProjectStore>(projects);
        builder.Services.AddSingleton<IProjectProvisioner>(provisioner);
        builder.Services.AddSingleton(new ProjectCloner(directory));
        builder.Services.AddSingleton(new GitHubOptions());
        builder.Services.AddSingleton<ISecretStore>(new SecretStore(issues, DataProtectionProvider.Create("fixture")));
        await using var app = builder.Build();
        var token = new string('x', 48);
        new DashboardMutationAuthentication(token).Protect(app);
        app.MapPost("/api/projects", ProjectsEndpoints.AddProjectAsync);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        var request = new ProjectsEndpoints.AddProjectRequest("second", "Second", "file:///nonexistent-forge-fixture", "main");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/projects", request)).StatusCode);
        Assert.Equal(0, provisioner.Calls);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/projects", request)).StatusCode);
        Assert.Single(await projects.ListAsync());
        provisioner.Fail = false;
        var registered = await client.PostAsJsonAsync("/api/projects", request);
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode); // Clone failure remains repairable as before.
        Assert.Equal(2, (await projects.ListAsync()).Count);
        Assert.NotNull(await projects.GetAsync("existing"));
        Assert.NotNull(await projects.GetAsync("second"));
        Assert.Equal(2, provisioner.Calls);
        await app.StopAsync();
    }

    private sealed class Provisioner(ProjectStore store) : IProjectProvisioner
    {
        public bool Fail = true;
        public int Calls;
        public async Task EnsureReadyAsync(string projectId, CancellationToken ct)
        {
            Calls++;
            Assert.Null(await store.GetAsync(projectId, ct));
            if (Fail) throw new InvalidOperationException("isolated qualification failure");
        }
    }

    [Theory]
    [InlineData("second-project", "proj_second-project")]
    [InlineData("second_project", "proj_second_project")]
    public void CanonicalSchemaPreservesDistinctSlugNames(string project, string expected)
        => Assert.Equal(expected, ProjectStorageInitializer.Schema(project));

    [Theory]
    [InlineData("Second")]
    [InlineData("../second")]
    [InlineData("")]
    public void NoncanonicalProjectIsRejected(string project)
        => Assert.Throws<ArgumentException>(() => ProjectStorageInitializer.Schema(project));

    [Fact]
    public void SupervisorHasNoSqlSecretsAndUsesExistingReleaseAccount()
    {
        var options = new AgentOptions();
        options.Worker.Image = "registry/forge@sha256:" + new string('a', 64);
        options.Controller.DatasetId = Guid.NewGuid().ToString();
        var manifest = JsonSerializer.SerializeToElement(KubernetesProjectProvisioner.Manifest(options, "second", "fixture"));
        var pod = manifest.GetProperty("spec").GetProperty("template").GetProperty("spec");
        Assert.Equal("forge-release", pod.GetProperty("serviceAccountName").GetString());
        Assert.False(pod.TryGetProperty("volumes", out _));
        Assert.DoesNotContain("sql-connection-string", manifest.ToString());
        Assert.Contains("project_provision.py", manifest.ToString());
        Assert.True(KubernetesProjectProvisioner.Contains(manifest, manifest));
        Assert.False(KubernetesProjectProvisioner.Contains(JsonSerializer.SerializeToElement(new { spec = new { } }), manifest));
    }

    [Fact]
    public void ProjectGrantsKeepWorkerApprovalTablesReadOnlyInThatSchema()
    {
        var sql = ControllerInitializer.InitiativeWorkerPermissionsSql("worker", "proj_second-project");
        Assert.Contains("[proj_second-project].[initiative_proposal]", sql);
        Assert.Contains("DENY INSERT,UPDATE,DELETE", sql);
        Assert.DoesNotContain("proj_forge", sql);
    }
}
