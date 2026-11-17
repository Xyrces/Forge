using System.Net;
using System.Text.Json;
using Forge.Configuration;
using Forge.Core;
using Forge.Deploy;
using Xunit;

namespace Forge.Tests;

public sealed class TrustedReleaseForwardRecoveryTests
{
    private static readonly string First = new('a', 40);
    private static readonly string Frontier = new('b', 40);
    private static readonly string Running = new('c', 40);
    private static readonly string Tip = new('d', 40);
    private static ProjectOptions Project => new()
    {
        Id = "forge", RepoUrl = "https://github.com/Xyrces/Forge.git", DefaultBranch = "main"
    };
    private static IssueRecord Member(int number) => new("task-" + number, number.ToString(), "task", "Done", null,
        IssueStatus.Completed, 2, null, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow,
        JsonSerializer.Serialize(new { prNumber = number }));

    [Fact]
    public async Task OlderRunningSourceKeepsMinimalMemberFrontierEvenWithNewerBranchTip()
    {
        using var fixture = new Fixture();
        var result = await fixture.Source.ObserveMergedFrontierAsync(Project, [Member(1), Member(2)], default);
        Assert.Equal(Frontier, result.Sha);
        AssertEvidence(result.Evidence, "member-frontier", Frontier);
        Assert.DoesNotContain($"/repos/Xyrces/Forge/compare/{Running}...{Tip}", fixture.Paths);
        await fixture.Source.ValidateForwardAsync(Project, result.Sha, default);
    }

    [Theory]
    [InlineData("behind")]
    [InlineData("diverged")]
    public async Task RepairedRunningSourceSelectsOnlyVerifiedSharedDefaultBranchDescendant(string relationship)
    {
        using var fixture = new Fixture();
        fixture.Relations[Running + "..." + Frontier] = relationship;
        var result = await fixture.Source.ObserveMergedFrontierAsync(Project, [Member(1), Member(2)], default);
        Assert.Equal(Tip, result.Sha);
        AssertEvidence(result.Evidence, "running-source-forward-recovery", Tip);
        foreach (var ancestor in new[] { First, Frontier, Running })
            Assert.Contains($"/repos/Xyrces/Forge/compare/{ancestor}...{Tip}", fixture.Paths);
        await fixture.Source.ValidateForwardAsync(Project, result.Sha, default);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("http-error")]
    public async Task UnavailableRunningAncestryNeverFallsBackToBranchTip(string failure)
    {
        using var fixture = new Fixture();
        fixture.Relations[Running + "..." + Frontier] = failure;
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Source.ObserveMergedFrontierAsync(Project, [Member(1), Member(2)], default));
        Assert.DoesNotContain($"/repos/Xyrces/Forge/compare/{Running}...{Tip}", fixture.Paths);
    }

    [Theory]
    [InlineData("behind")]
    [InlineData("diverged")]
    public async Task BranchTipMustContainRunningRepairAndMemberFrontier(string relationship)
    {
        using var fixture = new Fixture();
        fixture.Relations[Running + "..." + Frontier] = "diverged";
        fixture.Relations[Running + "..." + Tip] = relationship;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Source.ObserveMergedFrontierAsync(Project, [Member(1), Member(2)], default));
        fixture.Relations[Running + "..." + Tip] = "ahead";
        fixture.Relations[Frontier + "..." + Tip] = relationship;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Source.ObserveMergedFrontierAsync(Project, [Member(1), Member(2)], default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    public async Task MissingOrInvalidRunningSourceCannotSelectRelease(string? running)
    {
        using var fixture = new Fixture { CurrentRunning = running };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Source.ObserveMergedFrontierAsync(Project, [Member(1)], default));
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("branch")]
    [InlineData("unmerged")]
    [InlineData("merge-sha")]
    public async Task InvalidMemberMergeCannotAuthorizeForwardRecovery(string fault)
    {
        using var fixture = new Fixture { MemberFault = fault };
        fixture.Relations[Running + "..." + Frontier] = "diverged";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Source.ObserveMergedFrontierAsync(Project, [Member(1)], default));
        Assert.DoesNotContain("/repos/Xyrces/Forge/branches/main", fixture.Paths);
    }

    [Fact]
    public async Task NoMergedMembersCannotSelectRunningOrBranchSource()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Source.ObserveMergedFrontierAsync(Project, [], default));
        Assert.Empty(fixture.Paths);
    }

    [Fact]
    public async Task FinalForwardCheckStillRejectsChangedRunningSourceAfterSelection()
    {
        using var fixture = new Fixture();
        fixture.Relations[Running + "..." + Frontier] = "diverged";
        var result = await fixture.Source.ObserveMergedFrontierAsync(Project, [Member(1), Member(2)], default);
        var newer = new string('e', 40);
        fixture.CurrentRunning = newer;
        fixture.Relations[newer + "..." + Tip] = "behind";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Source.ValidateForwardAsync(Project, result.Sha, default));
    }

    private static void AssertEvidence(string json, string reason, string selected)
    {
        using var evidence = JsonDocument.Parse(json);
        var e = evidence.RootElement;
        Assert.Equal(Frontier, e.GetProperty("memberFrontier").GetString());
        Assert.Equal(Running, e.GetProperty("runningSource").GetString());
        Assert.Equal(selected, e.GetProperty("selectedSource").GetString());
        Assert.Equal(reason, e.GetProperty("selectionReason").GetString());
        Assert.Equal(2, e.GetProperty("merged").GetArrayLength());
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public string? CurrentRunning { get; set; } = Running;
        public string? MemberFault { get; init; }
        public Dictionary<string, string> Relations { get; } = new()
        {
            [First + "..." + Frontier] = "ahead",
            [First + "..." + Tip] = "ahead",
            [Frontier + "..." + Tip] = "ahead",
            [Running + "..." + Frontier] = "ahead",
            [Running + "..." + Tip] = "ahead"
        };
        public List<string> Paths { get; } = [];
        public TrustedGitHubReleaseSource Source { get; }
        public Fixture() => Source = new(new HttpClient(this, disposeHandler: false), new HttpClient(this, disposeHandler: false),
            (_, _) => Task.FromResult<string?>(null), () => CurrentRunning);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            object value;
            if (path.Contains("/pulls/"))
                value = new { merged = MemberFault != "unmerged", merged_at = DateTimeOffset.UnixEpoch,
                    merge_commit_sha = MemberFault == "merge-sha" ? "bad" : path.EndsWith("/1") ? First : Frontier,
                    @base = new { @ref = MemberFault == "branch" ? "other" : "main",
                        repo = new { full_name = MemberFault == "repository" ? "other/Forge" : "Xyrces/Forge" } } };
            else if (path.EndsWith("/branches/main")) value = new { commit = new { sha = Tip } };
            else if (path.Contains("/compare/"))
            {
                var status = Relations[path.Split("/compare/")[1]];
                if (status == "http-error") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                value = status == "missing" ? new { unrelated = true } : (object)new { status };
            }
            else throw new InvalidOperationException("Unexpected HTTP route: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) });
        }
        protected override void Dispose(bool disposing) { if (disposing) Source.Dispose(); base.Dispose(disposing); }
    }
}
