using Forge.Core;
using Xunit;

namespace Forge.Tests;

/// <summary>Deterministic triage guardrails (phase 2, plan §4): the
/// daily cap and the same-signature-same-action burn rule. Pure over
/// ledger history — store-enforced, never LLM judgment.</summary>
public class TriageGuardrailsTests
{
    private static readonly DateTime Now = new(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

    private static FailureTriageEntry Row(
        string signature, string? actor, string? action, DateTime? actedAt, string? outcome)
        => new(1, "task-1", Now.AddHours(-2), signature, "transient-upstream",
            null, action, actor, actedAt, outcome, null, null);

    [Fact]
    public void NoHistory_IsAllowed()
    {
        var decision = TriageGuardrails.Evaluate(
            Array.Empty<FailureTriageEntry>(), "llm-429-quota", Now);
        Assert.Equal(TriageGuardrails.Decision.Allowed, decision);
    }

    [Fact]
    public void UnderDailyCap_IsAllowed()
    {
        var history = new[]
        {
            Row("llm-429-quota", FailureTriageActors.Triage, FailureTriageActions.TriageRequeue, Now.AddHours(-3), FailureTriageOutcomes.Pending),
        };
        var decision = TriageGuardrails.Evaluate(history, "llm-429-quota", Now);
        Assert.Equal(TriageGuardrails.Decision.Allowed, decision);
    }

    [Fact]
    public void DailyCapReached_Parks()
    {
        var history = new[]
        {
            Row("llm-429-quota", FailureTriageActors.Triage, FailureTriageActions.TriageRequeue, Now.AddHours(-3), FailureTriageOutcomes.FailedAgain),
            Row("llm-529-overload", FailureTriageActors.Triage, FailureTriageActions.TriagePark, Now.AddHours(-1), null),
        };
        var decision = TriageGuardrails.Evaluate(history, "gateway-5xx", Now);
        Assert.Equal(TriageGuardrails.Decision.DailyCapReached, decision);
    }

    [Fact]
    public void OperatorActions_DoNotCountTowardCap()
    {
        var history = new[]
        {
            Row("llm-429-quota", FailureTriageActors.Operator, FailureTriageActions.OperatorRequeue, Now.AddHours(-3), FailureTriageOutcomes.Succeeded),
            Row("llm-429-quota", FailureTriageActors.Operator, FailureTriageActions.OperatorResetStrikes, Now.AddHours(-1), null),
        };
        var decision = TriageGuardrails.Evaluate(history, "llm-429-quota", Now);
        Assert.Equal(TriageGuardrails.Decision.Allowed, decision);
    }

    [Fact]
    public void ActionsOlderThan24h_DoNotCountTowardCap()
    {
        var history = new[]
        {
            Row("llm-429-quota", FailureTriageActors.Triage, FailureTriageActions.TriageRequeue, Now.AddDays(-2), FailureTriageOutcomes.Succeeded),
            Row("llm-429-quota", FailureTriageActors.Triage, FailureTriageActions.TriageRequeue, Now.AddDays(-3), FailureTriageOutcomes.Succeeded),
        };
        var decision = TriageGuardrails.Evaluate(history, "llm-429-quota", Now);
        Assert.Equal(TriageGuardrails.Decision.Allowed, decision);
    }

    [Fact]
    public void SameSignatureRequeueTwiceWithoutSuccess_Parks()
    {
        var history = new[]
        {
            Row("llm-429-quota", FailureTriageActors.Triage, FailureTriageActions.TriageRequeue, Now.AddDays(-2), FailureTriageOutcomes.FailedAgain),
            Row("llm-429-quota", FailureTriageActors.Triage, FailureTriageActions.TriageRequeue, Now.AddDays(-4), FailureTriageOutcomes.FailedAgain),
        };
        var decision = TriageGuardrails.Evaluate(history, "llm-429-quota", Now);
        Assert.Equal(TriageGuardrails.Decision.SameActionBurnLoop, decision);
    }

    [Fact]
    public void SameSignatureRequeuesWithASuccess_DoNotTrip()
    {
        var history = new[]
        {
            Row("llm-429-quota", FailureTriageActors.Triage, FailureTriageActions.TriageRequeue, Now.AddDays(-2), FailureTriageOutcomes.Succeeded),
            Row("llm-429-quota", FailureTriageActors.Triage, FailureTriageActions.TriageRequeue, Now.AddDays(-4), FailureTriageOutcomes.FailedAgain),
        };
        var decision = TriageGuardrails.Evaluate(history, "llm-429-quota", Now);
        Assert.Equal(TriageGuardrails.Decision.Allowed, decision);
    }

    [Fact]
    public void BurnRule_IsSignatureScoped()
    {
        var history = new[]
        {
            Row("llm-429-quota", FailureTriageActors.Triage, FailureTriageActions.TriageRequeue, Now.AddDays(-2), FailureTriageOutcomes.FailedAgain),
            Row("gateway-5xx", FailureTriageActors.Triage, FailureTriageActions.TriageRequeue, Now.AddDays(-4), FailureTriageOutcomes.FailedAgain),
        };
        var decision = TriageGuardrails.Evaluate(history, "llm-529-overload", Now);
        Assert.Equal(TriageGuardrails.Decision.Allowed, decision);
    }
}
