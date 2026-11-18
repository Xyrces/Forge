using Forge.Core;
using Forge.Reviewer;
using Octokit;
using Xunit;
namespace Forge.Tests;
public sealed class NominatedHumanReviewTests
{
    private static FormalDecision Review(string login, string head, PullRequestReviewState state, long id = 1, bool automated = false)
        => new(login,head,state,automated,id,"review","https://github.com/example/repo/pull/1#review");
    [Fact]
    public void NominationRequiresTheEligibleNamedHumanAtCurrentHead()
    {
        var other = Review("another-writer","head",PullRequestReviewState.Approved);
        Assert.False(EffectiveReview.Evaluate([other],"head","Owner").HumanApproved);
        Assert.True(EffectiveReview.Evaluate([other],"head").HumanApproved);
        Assert.True(EffectiveReview.Evaluate([Review("OWNER","head",PullRequestReviewState.Approved)],"head","owner").HumanApproved);
        Assert.False(EffectiveReview.Evaluate([Review("owner","old-head",PullRequestReviewState.Approved)],"head","owner").HumanApproved);
        Assert.False(EffectiveReview.Evaluate([Review("owner","head",PullRequestReviewState.Approved)],"head","owner",author:"OWNER").HumanApproved);
        Assert.False(EffectiveReview.Evaluate([Review("worker[bot]","head",PullRequestReviewState.Approved)],"head","worker[bot]").HumanApproved);
        Assert.False(EffectiveReview.Evaluate([Review("owner","head",PullRequestReviewState.Approved,automated:true)],"head","owner").HumanApproved);
    }
    [Fact]
    public void EveryEligibleHumanObjectionStillBlocksTheNomineeApproval()
    {
        var decision=EffectiveReview.Evaluate([Review("owner","head",PullRequestReviewState.Approved),
            Review("another-writer","previous-head",PullRequestReviewState.ChangesRequested,2)],"head","owner");
        Assert.True(decision.HumanApproved);
        Assert.True(decision.OutstandingHumanObjection);
    }
    [Fact]
    public async Task SettingsAreProjectScopedAuditedAndClearableWithoutChangingMode()
    {
        await using var a = new IssueStore(TempRoot.Instance.NewDbPath("nominee-a"),"a");
        await using var b = new IssueStore(TempRoot.Instance.NewDbPath("nominee-b"),"b");
        await using var ma = new MemoryStore(a.Db); await using var mb = new MemoryStore(b.Db);
        var pa=new PullRequestReviewPolicy(ma);var pb=new PullRequestReviewPolicy(mb);
        await pa.SetAsync(RequiredReview.Both,"Owner","authenticated-operator",default);
        Assert.Equal(new ProjectReviewSettings(RequiredReview.Both,"Owner"),await pa.GetSettingsAsync(default));
        Assert.Equal(new ProjectReviewSettings(RequiredReview.Automated,null),await pb.GetSettingsAsync(default));
        await pa.SetModeAsync(RequiredReview.Human,"authenticated-operator",default);
        Assert.Equal("Owner",(await pa.GetSettingsAsync(default)).NominatedHuman);
        await pa.SetAsync(RequiredReview.Human,null,"authenticated-operator",default);
        Assert.Equal(new ProjectReviewSettings(RequiredReview.Human,null),await pa.GetSettingsAsync(default));
        Assert.Equal(3,(await ma.RecallAsync("review/project-mode-audit/")).Count);
        Assert.Throws<ArgumentException>(()=>PullRequestReviewPolicy.NormalizeNominee("app[bot]"));
    }
}
