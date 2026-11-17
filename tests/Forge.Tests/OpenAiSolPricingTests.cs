using System.Text.Json.Nodes;
using Forge.Orchestrator.Control;
using Xunit;

namespace Forge.Tests;

public sealed class OpenAiSolPricingTests
{
    private static JsonObject Evidence(long input = 1000) => new()
    {
        ["id"] = "resp", ["model"] = "gpt-6.1-sol", ["service_tier"] = "default", ["status"] = "completed",
        ["usage"] = new JsonObject
        {
            ["input_tokens"] = input, ["output_tokens"] = 20, ["total_tokens"] = input + 20,
            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 800, ["cache_write_tokens"] = 150,
                ["cache_write_tokens_details"] = new JsonObject { ["ttl_30m_tokens"] = 100, ["ttl_12h_tokens"] = 50 } },
            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 15 },
        },
    };

    [Fact]
    public void ExplicitZeroWritesDoesNotRequireOptionalTtlDetails()
    {
        var response = Evidence(); var details = (JsonObject)response["usage"]!["input_tokens_details"]!;
        details["cache_write_tokens"] = 0; details.Remove("cache_write_tokens_details");
        Assert.Equal(.00068m, OpenAiSolPricing.Estimate(response.ToJsonString(), 1000, 20));
    }

    [Fact]
    public void MissingWriteCountsHonorKnownReadsWithoutAssumingUncachedMeansOrdinary()
    {
        var response = Evidence(); var details = (JsonObject)response["usage"]!["input_tokens_details"]!;
        details.Remove("cache_write_tokens"); details.Remove("cache_write_tokens_details");
        var result = OpenAiSolPricing.Calculate(response.ToJsonString(), 1000, 20)!;
        Assert.False(result.CompleteBreakdown); Assert.Equal(.00128m, result.CostUsd);
    }

    [Theory]
    [InlineData(272000, 0.54288)]
    [InlineData(272001, 1.085664)]
    public void LongContextThresholdUsesFullRequestInputAndDoesNotDoubleCountReasoning(long input, decimal expected)
        => Assert.Equal(expected, OpenAiSolPricing.Estimate(Evidence(input).ToJsonString(), input, 20));

    [Theory]
    [InlineData("fractional")]
    [InlineData("huge")]
    [InlineData("negative")]
    [InlineData("cached-overlap")]
    [InlineData("write-sum")]
    [InlineData("missing")]
    [InlineData("tier")]
    [InlineData("model")]
    [InlineData("reasoning")]
    [InlineData("output-bound")]
    public void InvalidUsageCannotEarnDiscount(string problem)
    {
        var response = Evidence(); var usage = response["usage"]!;
        var details = usage["input_tokens_details"]!;
        if (problem == "fractional") usage["input_tokens"] = 1.5;
        if (problem == "huge") usage["input_tokens"] = decimal.MaxValue;
        if (problem == "negative") usage["input_tokens"] = -1;
        if (problem == "cached-overlap") details["cached_tokens"] = 900;
        if (problem == "write-sum") details["cache_write_tokens"] = 100;
        if (problem == "missing") ((JsonObject)details).Remove("cache_write_tokens");
        if (problem == "tier") response["service_tier"] = "priority";
        if (problem == "model") response["model"] = "different";
        if (problem == "reasoning") usage["output_tokens_details"]!["reasoning_tokens"] = 21;
        Assert.Null(OpenAiSolPricing.Estimate(response.ToJsonString(), 1000, problem == "output-bound" ? 19 : 20));
    }
}
