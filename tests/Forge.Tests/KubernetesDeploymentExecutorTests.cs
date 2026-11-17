using System.Net;
using System.Text;
using System.Text.Json;
using Forge.Configuration;
using Forge.Deploy;
using Xunit;

namespace Forge.Tests;

public sealed class KubernetesDeploymentExecutorTests
{
    private static readonly string Sha = new('a', 40);
    private static readonly string Image = "registry/forge@sha256:" + new string('b', 64);
    private static ProjectOptions Project() => new() { Id = "forge", Deployment = new() {
        Kind = DeploymentKind.Kubernetes, KubernetesDatasetId = "6da15971-6ce5-45ef-9f9f-51e1e71c0597",
        KubernetesImageCatalog = "forge-image-catalog",
        KubernetesImages = new() { [Sha] = Image } } };
    private static DeploymentCandidate Candidate(DeploymentStatus status = DeploymentStatus.BuildPassed) => new(
        "deploy-" + new string('c', 32), "forge", Sha, null, status, DateTime.UtcNow, null, null, null, null, null, null);

    [Fact]
    public async Task CatalogIsReadForExactSourceWithoutChangingConfig()
    {
        var project = Project();
        project.Deployment!.KubernetesImages.Clear();
        project.Deployment.KubernetesImageCatalog = "forge-image-catalog";
        var handler = new FakeHandler(JsonSerializer.Serialize(new { data = new Dictionary<string, string> { [Sha] = Image } }));
        var resolved = await new KubernetesDeploymentExecutor(Client(handler)).ResolveProfileAsync(project, Candidate(), default);
        Assert.Equal(Image, KubernetesDeploymentExecutor.ResolveImage(resolved, Candidate()));
        Assert.Empty(project.Deployment.KubernetesImages);
    }

    [Fact]
    public void ReleaseManifestHasIndependentLifetimeAndExactImage()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(KubernetesDeploymentExecutor.BuildManifest(Project(), Candidate())));
        var root = json.RootElement;
        Assert.False(root.GetProperty("metadata").TryGetProperty("ownerReferences", out _));
        Assert.False(root.GetProperty("spec").TryGetProperty("ttlSecondsAfterFinished", out _));
        var pod = root.GetProperty("spec").GetProperty("template").GetProperty("spec");
        Assert.Equal("forge-release", pod.GetProperty("serviceAccountName").GetString());
        Assert.Equal(Image, pod.GetProperty("containers")[0].GetProperty("image").GetString());
        Assert.True(pod.GetProperty("automountServiceAccountToken").GetBoolean());
        foreach (var mount in pod.GetProperty("containers")[0].GetProperty("volumeMounts").EnumerateArray())
        {
            var path = mount.GetProperty("mountPath").GetString()!.Replace("/var/run/", "/run/").TrimEnd('/');
            Assert.False(mount.GetProperty("readOnly").GetBoolean() &&
                (path == "/run/secrets/kubernetes.io/serviceaccount" ||
                 "/run/secrets/kubernetes.io/serviceaccount".StartsWith(path + "/", StringComparison.Ordinal)));
            if (mount.GetProperty("name").GetString() == "control-token") Assert.Equal("/run/forge-control", path);
        }
        Assert.Equal("/run/forge-control/controller-token", new ControllerOptions().TokenFile);
    }

    [Fact]
    public async Task PendingCannotBypassBuildGate()
    {
        var handler = new FakeHandler("{}");
        var result = await new KubernetesDeploymentExecutor(Client(handler)).ExecuteAsync(Project(), Candidate(DeploymentStatus.Pending));
        Assert.False(result.Success);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AcceptedJobRemainsInProgress()
    {
        var handler = new FakeHandler("{}");
        var result = await new KubernetesDeploymentExecutor(Client(handler)).ExecuteAsync(Project(), Candidate());
        Assert.True(result.StillInProgress);
        Assert.False(result.Success);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public void UnknownSourceCannotSelectImage()
    {
        Assert.Throws<InvalidOperationException>(() => KubernetesDeploymentExecutor.ResolveImage(Project(), Candidate() with { CommitSha = new string('d', 40) }));
    }

    [Theory]
    [InlineData("committed", true, false)]
    [InlineData("rolled-back", false, false)]
    [InlineData("rolling-back", false, true)]
    public async Task JournalSurvivesControllerReplacement(string phase, bool success, bool pending)
    {
        var body = JsonSerializer.Serialize(new { data = new { image = Image, dataset = Project().Deployment!.KubernetesDatasetId, phase } });
        var result = await new KubernetesDeploymentExecutor(Client(new FakeHandler(body))).ReadProgressAsync(Project(), Candidate(), default);
        Assert.Equal(success, result.Success);
        Assert.Equal(pending, result.StillInProgress);
    }

    [Fact]
    public async Task WrongDatasetJournalIsNotAccepted()
    {
        var body = JsonSerializer.Serialize(new { data = new { image = Image, dataset = "other", phase = "committed" } });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new KubernetesDeploymentExecutor(Client(new FakeHandler(body)))
            .ReadProgressAsync(Project(), Candidate(), default));
    }

    private static HttpClient Client(FakeHandler handler) => new(handler) { BaseAddress = new Uri("https://kubernetes/") };
    private sealed class FakeHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var payload = request.RequestUri!.AbsolutePath.EndsWith("/forge-image-catalog", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { data = new Dictionary<string, string> { [Sha] = JsonSerializer.Serialize(new {
                    sourceSha = Sha, imageDigest = Image, buildPassed = true, testPassed = true,
                    workflow = "controller-image.yml", workflowRunId = "123" }) } }) : body;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        }
    }
}
