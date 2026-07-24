using Forge.Orchestrator;
using Xunit;

namespace Forge.Tests;

public class ModelRateLimitTrackerTests
{
    [Fact]
    public void UnknownModel_IsNotCoolingDown()
    {
        var t = new ModelRateLimitTracker();
        Assert.False(t.IsCoolingDown("kilo-gateway", "minimax/minimax-m3"));
        Assert.Null(t.CoolingDownUntil("kilo-gateway", "minimax/minimax-m3"));
    }

    [Fact]
    public void Record_CoolsOnlyThatModel()
    {
        var t = new ModelRateLimitTracker();
        t.RecordRateLimit("kilo-gateway", "minimax/minimax-m3", TimeSpan.FromMinutes(3));

        Assert.True(t.IsCoolingDown("kilo-gateway", "minimax/minimax-m3"));
        Assert.False(t.IsCoolingDown("kilo-gateway", "kimi-k3"));
        Assert.False(t.IsCoolingDown("openai", "minimax/minimax-m3"));
        Assert.NotNull(t.CoolingDownUntil("kilo-gateway", "minimax/minimax-m3"));
    }

    [Fact]
    public void ExpiredCooldown_IsClaimableAgain()
    {
        var t = new ModelRateLimitTracker();
        t.RecordRateLimit("kilo-gateway", "minimax/minimax-m3", TimeSpan.FromMilliseconds(1));
        Thread.Sleep(20);
        Assert.False(t.IsCoolingDown("kilo-gateway", "minimax/minimax-m3"));
    }

    [Fact]
    public void Snapshot_OnlyShowsLiveCooldowns()
    {
        var t = new ModelRateLimitTracker();
        t.RecordRateLimit("kilo-gateway", "minimax/minimax-m3", TimeSpan.FromMinutes(3));
        t.RecordRateLimit("kilo-gateway", "stale-model", TimeSpan.FromMilliseconds(1));
        Thread.Sleep(20);

        var snap = t.Snapshot();
        Assert.Single(snap);
        Assert.True(snap.ContainsKey("kilo-gateway|minimax/minimax-m3"));
    }
}
