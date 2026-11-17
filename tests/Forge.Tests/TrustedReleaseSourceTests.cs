using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Configuration;
using Forge.Core;
using Forge.Deploy;
using Xunit;

namespace Forge.Tests;

public sealed class TrustedReleaseSourceTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string Image = "ghcr.io/xyrces/forge@sha256:" + new string('b', 64);
    private static ProjectOptions Project => new() { Id = "forge", RepoUrl = "https://github.com/Xyrces/Forge.git", DefaultBranch = "main",
        Deployment = new() { Kind = DeploymentKind.Kubernetes, RoutineEnabled = true } };

    [Fact]
    public async Task ExactCiAndArchiveDigestQualifyWithoutForwardingCredentialsToDownload()
    {
        using var fixture = new Fixture();
        var receipt = await fixture.Source.FindReceiptAsync(Project, Sha, default);
        Assert.NotNull(receipt);
        Assert.Equal(Image, receipt.ImageDigest);
        Assert.Equal("91", receipt.ValidationRunId);
        Assert.Equal(IssueStore.CurrentSchemaVersion, receipt.SchemaVersion);
        Assert.All(fixture.Api.Requests, r => Assert.Equal("Bearer test-secret", r.Authorization));
        Assert.All(fixture.Download.Requests, r => Assert.Null(r.Authorization));
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("branch")]
    [InlineData("sha")]
    [InlineData("event")]
    [InlineData("receipt-source")]
    [InlineData("image")]
    [InlineData("schema")]
    [InlineData("digest")]
    [InlineData("redirect")]
    [InlineData("artifact-run")]
    public async Task ForgedOrMismatchedEvidenceCannotQualify(string fault)
    {
        using var fixture = new Fixture(fault);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Source.FindReceiptAsync(Project, Sha, default));
    }

    [Fact]
    public async Task DelayedCiIsWaitingThenQualifiesWithoutNewCandidate()
    {
        using var fixture = new Fixture("waiting");
        Assert.Null(await fixture.Source.FindReceiptAsync(Project, Sha, default));
        fixture.Fault = null;
        Assert.NotNull(await fixture.Source.FindReceiptAsync(Project, Sha, default));
    }

    [Fact]
    public async Task NewerFailedRunCannotReuseOlderGreenEvidence()
    {
        using var fixture = new Fixture("newer-failed");
        Assert.Null(await fixture.Source.FindReceiptAsync(Project, Sha, default));
        Assert.Empty(fixture.Download.Requests);
    }

    [Fact]
    public async Task FrontierUsesObservedMergeCommitRatherThanPullRequestHead()
    {
        using var fixture = new Fixture();
        var now = DateTime.UtcNow;
        var issue = new IssueRecord("task-1", "1", "task", "done", null, IssueStatus.Completed, 2, null, now, now, now, "{\"prNumber\":124}");
        var result = await fixture.Source.ObserveMergedFrontierAsync(Project, [issue], default);
        Assert.Equal(Sha, result.Sha);
        Assert.Contains("124", result.Evidence);
        Assert.DoesNotContain(new string('c', 40), result.Evidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EqualMergeTimestampsChooseFrontierByAncestryInEitherOrder(bool reverse)
    {
        using var fixture = new Fixture("tied-merges");
        var now = DateTime.UtcNow;
        IssueRecord Member(int pr) => new("task-" + pr, "1", "task", "done", null, IssueStatus.Completed, 2, null,
            now, now, now, "{\"prNumber\":" + pr + "}");
        var members = reverse ? new[] { Member(125), Member(124) } : new[] { Member(124), Member(125) };
        var result = await fixture.Source.ObserveMergedFrontierAsync(Project, members, default);
        Assert.Equal(new string('d', 40), result.Sha);
    }

    private sealed class Fixture : IDisposable
    {
        public string? Fault;
        public Handler Api { get; }
        public Handler Download { get; }
        public TrustedGitHubReleaseSource Source { get; }
        private readonly byte[] archive;
        private readonly string digest;
        public Fixture(string? fault = null)
        {
            Fault = fault;
            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("forge-image-receipt.json").Open()))
                writer.Write(JsonSerializer.Serialize(new { sourceSha = fault == "receipt-source" ? new string('c', 40) : Sha,
                    imageDigest = fault == "image" ? "ghcr.io/other/forge@sha256:" + new string('b', 64) : Image,
                    buildPassed = true, testPassed = true, workflow = "controller-image.yml", workflowRunId = "92",
                    schemaVersion = fault == "schema" ? 999 : IssueStore.CurrentSchemaVersion }));
            archive = stream.ToArray();
            digest = "sha256:" + Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            Api = new Handler(Respond);
            Download = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
            Source = new(new HttpClient(Api), new HttpClient(Download), (_, _) => Task.FromResult<string?>("test-secret"), () => Sha);
        }
        public void Dispose() => Source.Dispose();
        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/pulls/124")) return Json(new { merged = true, merge_commit_sha = Sha, merged_at = DateTimeOffset.UnixEpoch,
                @base = new { @ref = "main", repo = new { full_name = "Xyrces/Forge" } }, head = new { sha = new string('c', 40) } });
            if (path.EndsWith("/pulls/125")) return Json(new { merged = true, merge_commit_sha = new string('d', 40), merged_at = DateTimeOffset.UnixEpoch,
                @base = new { @ref = "main", repo = new { full_name = "Xyrces/Forge" } } });
            if (path.Contains("/compare/")) return Json(new { status = path.EndsWith("..." + new string('d', 40)) ? "ahead" : "behind" });
            if (Fault == "tied-merges" && path.EndsWith("/branches/main")) return Json(new { commit = new { sha = new string('d', 40) } });
            if (path.EndsWith("/branches/main")) return Json(new { commit = new { sha = Sha } });
            if (path.EndsWith("/ci.yml")) return Json(new { id = 1, path = ".github/workflows/ci.yml" });
            if (path.EndsWith("/controller-image.yml")) return Json(new { id = 2, path = ".github/workflows/controller-image.yml" });
            if (path.EndsWith("/runs"))
            {
                var validation = path.Contains("/1/");
                var values = Fault == "waiting" ? Array.Empty<object>() : new object[] { new {
                    id = validation ? 91 : 92, workflow_id = validation ? 1 : 2, status = "completed", conclusion = "success",
                    head_sha = Fault == "sha" ? new string('c', 40) : Sha,
                    head_branch = Fault == "branch" ? "other" : "main", @event = Fault == "event" ? "pull_request" : "push",
                    repository = new { id = 7, full_name = Fault == "repository" ? "attacker/Forge" : "Xyrces/Forge" },
                    head_repository = new { id = 7, full_name = "Xyrces/Forge" } } };
                if (Fault == "newer-failed") values = new object[] { new { id = 999, status = "completed", conclusion = "failure" } }.Concat(values).ToArray();
                return Json(new { workflow_runs = values });
            }
            if (path.EndsWith("/artifacts")) return Json(new { artifacts = new[] { new { id = 93, name = "forge-image-receipt", expired = false,
                size_in_bytes = archive.Length, digest = Fault == "digest" ? "sha256:" + new string('0', 64) : digest,
                workflow_run = new { id = Fault == "artifact-run" ? 999 : 92, head_sha = Sha, head_branch = "main", repository_id = 7, head_repository_id = 7 } } } });
            if (path.EndsWith("/zip"))
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new(Fault == "redirect" ? "http://127.0.0.1/private" : "https://artifacts.blob.core.windows.net/receipt");
                return response;
            }
            throw new InvalidOperationException("Unexpected fake request " + path);
        }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Authorization)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }
}
