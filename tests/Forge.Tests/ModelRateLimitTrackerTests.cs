using Forge.Core;
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

public class LlmAuthFailureClassificationTests
{
    [Fact]
    public void ClientResult401_Classifies()
    {
        var ex = new InvalidOperationException("run failed",
            new System.ClientModel.ClientResultException("HTTP 401 (: PAID_MODEL_AUTH_REQUIRED)", null));
        Assert.True(Forge.Orchestrator.OrchestratorAgent.IsLlmAuthFailure(ex));
    }

    [Fact]
    public void PaidMarker_InStringForm_Classifies()
    {
        // The lastError path wraps the recorded error string in a
        // plain InvalidOperationException — the marker must match.
        var ex = new InvalidOperationException(
            "ClientResultException: HTTP 401 (: PAID_MODEL_AUTH_REQUIRED)  You need to sign in to use this model.");
        Assert.True(Forge.Orchestrator.OrchestratorAgent.IsLlmAuthFailure(ex));
    }

    [Fact]
    public void Generic500_DoesNotClassify()
    {
        var ex = new System.ClientModel.ClientResultException("HTTP 500", null);
        Assert.False(Forge.Orchestrator.OrchestratorAgent.IsLlmAuthFailure(ex));
    }

    [Fact]
    public void RateLimit429_DoesNotClassify_AsAuth()
    {
        var ex = new System.ClientModel.ClientResultException("HTTP 429 Too Many Requests", null);
        Assert.False(Forge.Orchestrator.OrchestratorAgent.IsLlmAuthFailure(ex));
    }

    [Fact]
    public void Bare401String_WithoutMarker_DoesNotClassify()
    {
        // A GitHub-ish 401 in string form must not park the task as
        // an LLM auth outage — only the LLM client's typed exception
        // or the gateway marker counts.
        var ex = new InvalidOperationException("request failed: 401 Unauthorized");
        Assert.False(Forge.Orchestrator.OrchestratorAgent.IsLlmAuthFailure(ex));
    }

    [Fact]
    public void ClientResult402_Classifies()
    {
        var ex = new InvalidOperationException("run failed",
            new System.ClientModel.ClientResultException("HTTP 402 (: ) Add credits to continue", null));
        Assert.True(Forge.Orchestrator.OrchestratorAgent.IsLlmAuthFailure(ex));
    }

    [Fact]
    public void Http402_InStringForm_Classifies()
    {
        var ex = new InvalidOperationException(
            "ClientResultException: HTTP 402 (: )  Add credits to continue");
        Assert.True(Forge.Orchestrator.OrchestratorAgent.IsLlmAuthFailure(ex));
    }
}
