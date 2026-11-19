using Forge.Core;
using Forge.Reviewer;
using Octokit;
using Xunit;

namespace Forge.Tests;

public sealed class ReviewOriginTests
{
    [Theory]
    [InlineData("review/origins/v1/repo/1")]
    [InlineData("REVIEW/ORIGINS/v1/repo/1")]
    [InlineData(" review/origins/v1/repo/1")]
    [InlineData("réview/origins/v1/repo/1")]
    public async Task GenericMemoryCannotForgeOrDeleteReviewOrigins(string key)
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("review-protected"));
        var memory = new MemoryStore(issues.Db);
        Assert.True(AuthorityMemoryKeys.IsProtected(key));
        await Assert.ThrowsAsync<ArgumentException>(() => memory.RememberAsync(key, "forged"));
        await Assert.ThrowsAsync<ArgumentException>(() => memory.SeedIfMissingAsync(key, "forged"));
        await Assert.ThrowsAsync<ArgumentException>(() => memory.ForgetAsync(key));
        await memory.ObserveReviewOriginsAsync("repo", 1, "head", At, []);
        const string canonical = "review/origins/v1/repo/1";
        var before = await memory.GetAsync(canonical);
        Assert.NotNull(before);
        await Assert.ThrowsAsync<ArgumentException>(() => memory.RememberAsync(canonical, "forged", 1));
        await Assert.ThrowsAsync<ArgumentException>(() => memory.SeedIfMissingAsync(canonical, "forged"));
        await Assert.ThrowsAsync<ArgumentException>(() => memory.ForgetAsync(canonical));
        await memory.PurgeExpiredAsync();
        Assert.Equal(before, await memory.GetAsync(canonical));
        await memory.RememberAsync("ordinary", "ok");
        Assert.True(await memory.ForgetAsync("ordinary"));
    }

    private static readonly DateTimeOffset At = new(2026, 11, 19, 2, 27, 59, TimeSpan.Zero);
    private static ReviewOriginObservation Review(long id, string head, DateTimeOffset? at = null) => new(id, "Xyrces", head, at ?? At);

    [Fact]
    public async Task RewrittenNativeCommitCannotMovePinnedApprovalAcrossRestart()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("review-origin"));
        var memory = new MemoryStore(issues.Db);
        await memory.ObserveReviewOriginsAsync("owner/repo/10/100", 159, "H2", At.AddSeconds(-1), []);
        Assert.Equal("H2", (await memory.ObserveReviewOriginsAsync("owner/repo/10/100", 159, "H2", At, [Review(5968427154, "H2")]))[5968427154]);
        memory = new MemoryStore(issues.Db);
        var rewritten = await memory.ObserveReviewOriginsAsync("owner/repo/10/100", 159, "H3", At.AddMinutes(1), [Review(5968427154, "H3")]);
        Assert.Equal("H2", rewritten[5968427154]);
        var actualNew = await memory.ObserveReviewOriginsAsync("owner/repo/10/100", 159, "H3", At.AddMinutes(2),
            [Review(5968427154, "H3"), Review(5968427155, "H3", At.AddMinutes(2))]);
        Assert.Equal("H3", actualNew[5968427155]);
    }

    [Fact]
    public async Task LegacyFirstObservationIsNeverGrandfatheredButNewSameSecondReviewWorks()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("review-legacy"));
        var memory = new MemoryStore(issues.Db);
        Assert.Null((await memory.ObserveReviewOriginsAsync("owner/repo", 159, "H3", At, [Review(10, "H3")]))[10]);
        Assert.Null((await memory.ObserveReviewOriginsAsync("owner/repo", 159, "H3", At.AddHours(1), [Review(10, "H3")]))[10]);
        Assert.Equal("H3", (await memory.ObserveReviewOriginsAsync("owner/repo", 159, "H3", At.AddHours(1), [Review(11, "H3")]))[11]);
    }

    [Fact]
    public async Task ChangedAuthorOrSubmissionTimeCannotReuseProofAndUnknownApprovalPreservesObjection()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("review-identity"));
        var memory = new MemoryStore(issues.Db);
        await memory.ObserveReviewOriginsAsync("repo", 1, "head", At.AddSeconds(-1), []);
        await memory.ObserveReviewOriginsAsync("repo", 1, "head", At, [Review(1, "head")]);
        Assert.Null((await memory.ObserveReviewOriginsAsync("repo", 1, "head", At, [Review(1, "head") with { Author = "other" }]))[1]);
        Assert.Null((await memory.ObserveReviewOriginsAsync("repo", 1, "head", At.AddHours(1), [Review(1, "head", At.AddMinutes(1))]))[1]);
        var effective = EffectiveReview.Evaluate([
            new("Xyrces", "old", PullRequestReviewState.ChangesRequested, false, 1, "", ""),
            new("Xyrces", "", PullRequestReviewState.Approved, false, 2, "", "")], "head");
        Assert.True(effective.OutstandingHumanObjection);
        Assert.False(effective.HumanApproved);
    }

    [Fact]
    public async Task TrustedLocalReceiptPinsBotOriginalHeadEvenAfterRebinding()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("review-bot"));
        var memory = new MemoryStore(issues.Db);
        var observed = new ReviewOriginObservation(1, "reviewer[bot]", "H3", At);
        var proof = new VerifiedReviewOrigin(1, "reviewer[bot]", "H2", At);
        Assert.Equal("H2", (await memory.ObserveReviewOriginsAsync("repo", 1, "H3", At, [observed], [proof]))[1]);
        Assert.Equal("H2", (await memory.ObserveReviewOriginsAsync("repo", 1, "H3", At, [observed], [proof with { Head = "H3" }]))[1]);
    }

    [Fact]
    public async Task RepositoryRecreationAndProjectStoresCannotBorrowOrigins()
    {
        await using var first = new IssueStore(TempRoot.Instance.NewDbPath("review-project-a"));
        await using var second = new IssueStore(TempRoot.Instance.NewDbPath("review-project-b"));
        var memory = new MemoryStore(first.Db);
        await memory.ObserveReviewOriginsAsync("owner/repo/10/100", 1, "head", At.AddSeconds(-1), []);
        Assert.Equal("head", (await memory.ObserveReviewOriginsAsync("owner/repo/10/100", 1, "head", At, [Review(1, "head")]))[1]);
        Assert.Null((await memory.ObserveReviewOriginsAsync("owner/repo/11/101", 1, "head", At, [Review(1, "head")]))[1]);
        Assert.Null((await new MemoryStore(second.Db).ObserveReviewOriginsAsync("owner/repo/10/100", 1, "head", At, [Review(1, "head")]))[1]);
    }

    [Fact]
    public async Task ConcurrentBaselineAndRetriesNeverGrandfatherExistingDecision()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("review-concurrent"));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => new MemoryStore(issues.Db)
            .ObserveReviewOriginsAsync("repo", 1, "head", At, [Review(1, "head")])));
        Assert.All(results, result => Assert.Null(result[1]));
    }

    [Fact]
    public async Task UnobservedHeadReversionChangesResetMarkerAndCannotAdoptReboundApproval()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("review-reversion"));
        var memory = new MemoryStore(issues.Db);
        await memory.ObserveReviewOriginsAsync("repo", 1, "A", At, [], resetMarker: "reset-1");
        // B and its approval happened between polls; GitHub now reports A again.
        var rebound = await memory.ObserveReviewOriginsAsync("repo", 1, "A", At.AddMinutes(2),
            [Review(2, "A", At.AddMinutes(1))], resetMarker: "reset-2");
        Assert.Null(rebound[2]);
        Assert.Equal("A", (await memory.ObserveReviewOriginsAsync("repo", 1, "A", At.AddMinutes(3),
            [Review(3, "A", At.AddMinutes(3))], resetMarker: "reset-2"))[3]);
    }

    [Fact]
    public void LatestUnknownApprovalDoesNotResurrectOlderApprovalOnHeadReversion()
    {
        var effective = EffectiveReview.Evaluate([
            new("Xyrces", "A", PullRequestReviewState.Approved, false, 10, "", ""),
            new("Xyrces", "", PullRequestReviewState.Approved, false, 11, "", "")], "A");
        Assert.False(effective.HumanApproved);
    }

    [Fact]
    public void LaterSubmittedOldDraftObjectionSupersedesNewerCreatedApproval()
    {
        var effective = EffectiveReview.Evaluate([
            new("Xyrces", "A", PullRequestReviewState.Approved, false, 11, "", "", At),
            new("Xyrces", "A", PullRequestReviewState.ChangesRequested, false, 10, "", "", At.AddMinutes(1))], "A");
        Assert.False(effective.HumanApproved);
        Assert.True(effective.OutstandingHumanObjection);
    }

    [Fact]
    public async Task UnavailableResetMarkerCannotAuthorizeUnseenApprovalButLocalProofWorks()
    {
        await using var issues = new IssueStore(TempRoot.Instance.NewDbPath("review-marker"));
        var memory = new MemoryStore(issues.Db);
        await memory.ObserveReviewOriginsAsync("repo", 1, "A", At, [], resetMarker: null);
        Assert.Null((await memory.ObserveReviewOriginsAsync("repo", 1, "A", At.AddMinutes(1),
            [Review(1, "A", At.AddMinutes(1))], resetMarker: null))[1]);
        var proof = new VerifiedReviewOrigin(2, "reviewer[bot]", "A", At);
        Assert.Equal("A", (await memory.ObserveReviewOriginsAsync("repo", 1, "A", At.AddMinutes(1),
            [new(2, proof.Author, "A", At)], [proof], resetMarker: null))[2]);
    }
}
