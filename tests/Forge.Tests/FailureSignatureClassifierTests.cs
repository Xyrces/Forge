using Forge.Core;
using Xunit;

namespace Forge.Tests;

public sealed class FailureSignatureClassifierTests
{
    private static (string Signature, string Classification) Classify(
        string? error, params (string Key, string Value)[] metadata)
        => FailureSignatureClassifier.Classify(
            error, metadata.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));

    [Fact]
    public void Llm429Quota_IsTransientUpstream()
    {
        var (sig, cls) = Classify("HttpRequestException: HTTP 429 rate limit (quota, provider code 1302): too many requests");
        Assert.Equal("llm-429-quota", sig);
        Assert.Equal("transient-upstream", cls);
    }

    [Fact]
    public void Llm529Overload_IsTransientUpstream()
    {
        var (sig, cls) = Classify("HTTP 529 overloaded_error: the model is temporarily overloaded");
        Assert.Equal("llm-529-overload", sig);
        Assert.Equal("transient-upstream", cls);
    }

    [Fact]
    public void Gateway5xx_IsTransientUpstream()
    {
        var (sig, cls) = Classify("kilo gateway returned 503 Service Unavailable");
        Assert.Equal("gateway-5xx", sig);
        Assert.Equal("transient-upstream", cls);
    }

    [Fact]
    public void SessionPairing400_IsStatePoison()
    {
        var (sig, cls) = Classify("invalid params, tool call result does not follow tool call (2013)");
        Assert.Equal("session-pairing-400", sig);
        Assert.Equal("state-poison", cls);
    }

    [Fact]
    public void SessionPoison_TokenLimitVariant_IsStatePoison()
    {
        var (sig, cls) = Classify("total message size 35670664 exceeds limit");
        Assert.Equal("session-pairing-400", sig);
        Assert.Equal("state-poison", cls);
    }

    [Fact]
    public void ReworkFossil_IsStatePoison()
    {
        var (sig, cls) = Classify("rework branch diverged from PR head (non-fast-forward push)");
        Assert.Equal("rework-fossil", sig);
        Assert.Equal("state-poison", cls);
    }

    [Fact]
    public void MergedTarpit_IsStatePoison()
    {
        var (sig, cls) = Classify("PR branch is already merged into main");
        Assert.Equal("merged-tarpit", sig);
        Assert.Equal("state-poison", cls);
    }

    [Fact]
    public void NoDiffBounce_IsNoProgress()
    {
        var (sig, cls) = Classify("agent produced no diff in 3 attempts (last response truncated)");
        Assert.Equal("no-diff-bounce", sig);
        Assert.Equal("no-progress", cls);
    }

    [Fact]
    public void NoDiffBounce_FromAttemptsMetadata_IsNoProgress()
    {
        var (sig, _) = Classify(null, ("noProgressAttempts", "3"));
        Assert.Equal("no-diff-bounce", sig);
    }

    [Fact]
    public void VerificationTimeout_IsVerification()
    {
        var (sig, cls) = Classify("pre-push verification timed out after 600s");
        Assert.Equal("verification-timeout", sig);
        Assert.Equal("verification", cls);
    }

    [Fact]
    public void VerificationFail_IsVerification()
    {
        var (sig, cls) = Classify("pre-push verification failed:\nbuild errors in Foo.cs");
        Assert.Equal("verification-fail", sig);
        Assert.Equal("verification", cls);
    }

    [Fact]
    public void PlanGateTerritory_IsGateLoop()
    {
        var (sig, cls) = Classify("plan rejected: plan-territory gate — PortHorizon.Core/Foo.cs is outside the role territory");
        Assert.Equal("plan-gate-territory", sig);
        Assert.Equal("gate-loop", cls);
    }

    [Fact]
    public void PlanGateRevisions_IsGateLoop()
    {
        var (sig, cls) = Classify("plan gate revision budget exhausted after 2 revisions");
        Assert.Equal("plan-gate-revisions", sig);
        Assert.Equal("gate-loop", cls);
    }

    [Fact]
    public void ReviewChangesLoop_IsReviewLoop()
    {
        var (sig, cls) = Classify("reviewer issued RequestChanges: changes requested on the pushed head");
        Assert.Equal("review-changes-loop", sig);
        Assert.Equal("review-loop", cls);
    }

    [Fact]
    public void BreakerExhausted_FromLastEvent_IsCapabilityBound()
    {
        var (sig, cls) = Classify(null, ("lastEvent", "BreakerTripped"));
        Assert.Equal("breaker-exhausted", sig);
        Assert.Equal("capability-bound", cls);
    }

    [Fact]
    public void BreakerExhausted_FromStrikes_IsCapabilityBound()
    {
        var (sig, _) = Classify("some unrecognised failure", ("reworkAttempts", "3"));
        Assert.Equal("breaker-exhausted", sig);
    }

    [Fact]
    public void Unknown_IsOther()
    {
        var (sig, cls) = Classify("pr-stale");
        Assert.Equal("other", sig);
        Assert.Equal("unclassified", cls);
    }

    [Fact]
    public void NullError_NoMetadata_IsOther()
    {
        var (sig, cls) = Classify(null);
        Assert.Equal("other", sig);
        Assert.Equal("unclassified", cls);
    }
}
