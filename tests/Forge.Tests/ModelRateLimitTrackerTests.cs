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

    [Fact]
    public void ProviderWideCooldown_CoolsEveryModelOnTheProvider()
    {
        var t = new ModelRateLimitTracker();
        t.RecordProviderRateLimit("kimi", TimeSpan.FromMinutes(3));

        Assert.True(t.IsCoolingDown("kimi", "kimi-for-coding"));
        Assert.True(t.IsCoolingDown("kimi", "k3"));
        Assert.False(t.IsCoolingDown("kilo-gateway", "kimi-k3"));
    }

    [Fact]
    public void ProviderWideCooldown_ReturnsTheLaterExpiry()
    {
        var t = new ModelRateLimitTracker();
        t.RecordRateLimit("kimi", "k3", TimeSpan.FromMinutes(1));
        t.RecordProviderRateLimit("kimi", TimeSpan.FromMinutes(10));

        var until = t.CoolingDownUntil("kimi", "k3");
        Assert.NotNull(until);
        Assert.True(until.Value > DateTime.UtcNow.AddMinutes(9));
    }

    [Fact]
    public void Clear_LiftsModelCooldown()
    {
        var t = new ModelRateLimitTracker();
        t.RecordRateLimit("kimi", "k3", TimeSpan.FromMinutes(3));

        t.Clear("kimi", "k3");

        Assert.False(t.IsCoolingDown("kimi", "k3"));
    }

    [Fact]
    public void Clear_KeepsProviderWideCooldown()
    {
        var t = new ModelRateLimitTracker();
        t.RecordRateLimit("kimi", "k3", TimeSpan.FromMinutes(3));
        t.RecordProviderRateLimit("kimi", TimeSpan.FromMinutes(3));

        t.Clear("kimi", "k3");

        Assert.True(t.IsCoolingDown("kimi", "k3"));  // provider-wide still active
    }

    [Fact]
    public void RecordRateLimit_NeverShortensAnActiveLongerCooldown()
    {
        // The 2026-08-08 herd bug: a generic 3-minute handler
        // flattened the client layer's longer escalating cooldown.
        var t = new ModelRateLimitTracker();
        t.RecordRateLimit("minimax", "MiniMax-M3", TimeSpan.FromMinutes(10));
        t.RecordRateLimit("minimax", "MiniMax-M3", TimeSpan.FromMinutes(3));

        var until = t.CoolingDownUntil("minimax", "MiniMax-M3");
        Assert.NotNull(until);
        Assert.True(until.Value > DateTime.UtcNow.AddMinutes(9));
    }

    [Fact]
    public void AccountQuota_EscalatesPerStrike_ProviderWide()
    {
        var t = new ModelRateLimitTracker();
        var first = t.RecordAccountQuota("minimax");
        var second = t.RecordAccountQuota("minimax");

        // 1m then 2m (±30% jitter) from now; the second is later.
        Assert.True(first > DateTime.UtcNow.AddSeconds(30));
        Assert.True(first < DateTime.UtcNow.AddMinutes(2));
        Assert.True(second > DateTime.UtcNow.AddMinutes(1));
        Assert.True(second < DateTime.UtcNow.AddMinutes(3));
        Assert.Equal(2, t.AccountStrikes("minimax"));
        // Provider-wide: every model on the key cools.
        Assert.True(t.IsCoolingDown("minimax", "MiniMax-M3"));
        Assert.True(t.IsCoolingDown("minimax", "any-other-model"));
        Assert.False(t.IsCoolingDown("kimi", "k3"));
        Assert.NotNull(t.ProviderCoolingDownUntil("minimax"));
    }

    [Fact]
    public void AccountQuota_EscalationCapsAtMax()
    {
        var t = new ModelRateLimitTracker();
        DateTime last = default;
        for (var i = 0; i < 12; i++) last = t.RecordAccountQuota("minimax");

        Assert.True(last < DateTime.UtcNow
            + ModelRateLimitTracker.AccountQuotaMaxCooldown
            + TimeSpan.FromMinutes(10)); // cap + ±30% jitter headroom
    }

    [Fact]
    public void Clear_ResetsAccountQuotaEscalation()
    {
        var t = new ModelRateLimitTracker();
        t.RecordAccountQuota("minimax");
        t.RecordAccountQuota("minimax");

        t.Clear("minimax", "MiniMax-M3");   // a success proves budget

        Assert.Equal(0, t.AccountStrikes("minimax"));
        var next = t.RecordAccountQuota("minimax");
        Assert.True(next < DateTime.UtcNow.AddMinutes(2)); // back to 1m base
    }
}

public class LlmRateLimitClassificationTests
{
    [Fact]
    public void MiniMax2062_Classifies_AccountQuota()
    {
        const string body = """{"type":"error","error":{"type":"rate_limit_error","message":"Token Plan rate limit reached: Upgrade your Token Plan or switch to pay-as-you-go API usage. (2062)"},"request_id":"abc123"}""";
        Assert.Equal(Forge.Agents.RateLimitKind.AccountQuota,
            Forge.Agents.LlmRateLimitException.Classify(body));
        Assert.Equal("2062", Forge.Agents.LlmRateLimitException.ExtractErrorCode(body));
        Assert.Equal("abc123", Forge.Agents.LlmRateLimitException.ExtractRequestId(body));
    }

    [Fact]
    public void MiniMax2056_AndTokenPlanPhrase_Classify_AccountQuota()
    {
        Assert.Equal(Forge.Agents.RateLimitKind.AccountQuota,
            Forge.Agents.LlmRateLimitException.Classify("usage limit exceeded (2056)"));
        Assert.Equal(Forge.Agents.RateLimitKind.AccountQuota,
            Forge.Agents.LlmRateLimitException.Classify("Token Plan rate limit reached"));
    }

    [Fact]
    public void BaseRespStatusCode_Extracts()
    {
        Assert.Equal("2062", Forge.Agents.LlmRateLimitException.ExtractErrorCode(
            """{"base_resp":{"status_code":2062,"status_msg":"rate limit"}}"""));
    }

    [Fact]
    public void Overload_StillClassifies_Overloaded()
    {
        Assert.Equal(Forge.Agents.RateLimitKind.Overloaded,
            Forge.Agents.LlmRateLimitException.Classify("the engine is currently overloaded"));
    }

    [Fact]
    public void Generic429_Classifies_BurstQuota()
    {
        Assert.Equal(Forge.Agents.RateLimitKind.Quota,
            Forge.Agents.LlmRateLimitException.Classify("Too Many Requests"));
        Assert.Null(Forge.Agents.LlmRateLimitException.ExtractErrorCode("Too Many Requests"));
        Assert.Null(Forge.Agents.LlmRateLimitException.ExtractRequestId("nope"));
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
