using System.Text.Json;

namespace Forge.Orchestrator.Control;

/// <summary>Qualified global Standard Sol tariff; estimates API token charges, never an invoice.</summary>
internal static class OpenAiSolPricing
{
    internal const string Version = "openai-sol-standard-2026-11-17";
    // Sources: developers.openai.com/api/docs/models/gpt-6.1-sol.md and
    // developers.openai.com/api/docs/guides/prompt-caching.md. Rates per million tokens.
    internal sealed record Calculation(decimal CostUsd, bool CompleteBreakdown);
    internal static decimal? Estimate(string evidenceJson, long countedInput, int outputBound) =>
        Calculate(evidenceJson, countedInput, outputBound) is { CompleteBreakdown: true } result ? result.CostUsd : null;

    internal static Calculation? Calculate(string evidenceJson, long countedInput, int outputBound)
    {
        try
        {
            using var doc = JsonDocument.Parse(evidenceJson);
            var response = doc.RootElement;
            if (response.GetProperty("model").GetString() != BudgetRequestPolicy.QualifiedModel ||
                response.GetProperty("service_tier").GetString() != "default" ||
                response.GetProperty("status").GetString() is not ("completed" or "incomplete")) return null;
            var usage = response.GetProperty("usage");
            var input = Count(usage, "input_tokens");
            var output = Count(usage, "output_tokens");
            if (input > countedInput || input > 922_000 || output > outputBound ||
                Count(usage, "total_tokens") != input + output) return null;
            var details = usage.GetProperty("input_tokens_details");
            var cached = Count(details, "cached_tokens");
            if (cached > input) return null;
            decimal weightedInput;
            var complete = false;
            if (details.TryGetProperty("cache_write_tokens", out _))
            {
                var writes = Count(details, "cache_write_tokens");
                if (cached + writes > input) return null;
                if (details.TryGetProperty("cache_write_tokens_details", out var ttl))
                {
                    var shortWrites = Count(ttl, "ttl_30m_tokens");
                    var longWrites = Count(ttl, "ttl_12h_tokens");
                    if (shortWrites + longWrites != writes) return null;
                    weightedInput = (input - cached - writes) * 2m + cached * .1m + shortWrites * 2.5m + longWrites * 5m;
                    complete = true;
                }
                else
                {
                    // Explicit zero writes is sufficient evidence even when optional TTL fields are omitted.
                    weightedInput = (input - cached - writes) * 2m + cached * .1m + writes * 5m;
                    complete = writes == 0;
                }
            }
            else
            {
                // Do not invent zero writes. Honor verified reads and upper-bound every remaining
                // token at the most expensive qualified cache-write rate (including a 12h upgrade).
                weightedInput = (input - cached) * 5m + cached * .1m;
            }
            // Reasoning tokens are already included in output_tokens. Never charge them twice.
            if (usage.TryGetProperty("output_tokens_details", out var outputDetails) &&
                outputDetails.TryGetProperty("reasoning_tokens", out _) && Count(outputDetails, "reasoning_tokens") > output) return null;
            var inputMultiplier = input > 272_000 ? 2m : 1m;
            var outputRate = input > 272_000 ? 15m : 10m;
            return new(weightedInput * inputMultiplier / 1_000_000m + output * outputRate / 1_000_000m, complete);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or OverflowException or FormatException)
        {
            // No trustworthy complete breakdown: caller retains conservative liability.
            return null;
        }
    }

    private static long Count(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name).GetInt64();
        if (value < 0 || value > 1_050_000) throw new InvalidOperationException("Invalid token usage.");
        return value;
    }
}
