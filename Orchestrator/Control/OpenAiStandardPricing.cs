using System.Text.Json;

namespace Forge.Orchestrator.Control;

/// <summary>Qualified global Standard tariffs for text and function calls.</summary>
internal sealed class OpenAiStandardPricing(string model, string version, decimal inputRate, decimal cacheReadRate,
    decimal shortCacheWriteRate, decimal longCacheWriteRate, decimal outputRate)
{
    // Qualified 2026-11-17 UTC. Standard rates per million tokens:
    // https://developers.openai.com/api/docs/models/gpt-6.1-sol.md
    // https://developers.openai.com/api/docs/models/gpt-6.2-sol.md
    // https://developers.openai.com/api/docs/models/gpt-6.2-astra.md
    // https://developers.openai.com/api/docs/guides/prompt-caching.md
    private static readonly OpenAiStandardPricing Sol61 = new("gpt-6.1-sol", "openai-sol-standard-2026-11-17", 2m, .1m, 2.5m, 5m, 10m);
    private static readonly OpenAiStandardPricing Sol62 = new("gpt-6.2-sol", "openai-6.2-sol-standard-2026-11-17", 2m, .1m, 2.5m, 5m, 10m);
    private static readonly OpenAiStandardPricing Astra62 = new("gpt-6.2-astra", "openai-6.2-astra-standard-2026-11-17", 8m, .4m, 10m, 20m, 40m);

    // All three qualified models have the same context and long-context thresholds.
    internal const int ContextTokens = 1_050_000;
    internal const int MaxInputTokens = 922_000;
    internal const int MaxOutputTokens = 128_000;
    private const int LongContextThreshold = 272_000;
    private const decimal LongContextInputMultiplier = 2m;
    private const decimal LongContextOutputMultiplier = 1.5m;

    internal static OpenAiStandardPricing? ForModel(string model) => model switch
    {
        "gpt-6.1-sol" => Sol61,
        "gpt-6.2-sol" => Sol62,
        "gpt-6.2-astra" => Astra62,
        _ => null,
    };

    internal string Version { get; } = version;
    internal decimal MinimumInputUsdPerMillion => longCacheWriteRate * LongContextInputMultiplier;
    internal decimal MinimumOutputUsdPerMillion => outputRate * LongContextOutputMultiplier;

    internal sealed record Calculation(decimal CostUsd, bool CompleteBreakdown);
    internal decimal? Estimate(string evidenceJson, long countedInput, int outputBound) =>
        Calculate(evidenceJson, countedInput, outputBound, out _) is { CompleteBreakdown: true } result ? result.CostUsd : null;

    internal Calculation? Calculate(string evidenceJson, long countedInput, int outputBound, out bool unexpectedProfile)
    {
        unexpectedProfile = false;
        try
        {
            using var doc = JsonDocument.Parse(evidenceJson);
            var response = doc.RootElement;
            var evidenceModel = ReadString(response, "model");
            var evidenceTier = ReadString(response, "service_tier");
            unexpectedProfile = (evidenceModel is not null && evidenceModel != model) ||
                (evidenceTier is not null && evidenceTier != "default");
            if (unexpectedProfile || evidenceModel is null || evidenceTier is null ||
                response.GetProperty("status").GetString() is not ("completed" or "incomplete")) return null;
            var usage = response.GetProperty("usage");
            var input = Count(usage, "input_tokens");
            var output = Count(usage, "output_tokens");
            if (input > countedInput || input > MaxInputTokens || output > outputBound ||
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
                    weightedInput = (input - cached - writes) * inputRate + cached * cacheReadRate
                        + shortWrites * shortCacheWriteRate + longWrites * longCacheWriteRate;
                    complete = true;
                }
                else
                {
                    // Explicit zero writes is sufficient evidence even when optional TTL fields are omitted.
                    weightedInput = (input - cached - writes) * inputRate + cached * cacheReadRate + writes * longCacheWriteRate;
                    complete = writes == 0;
                }
            }
            else
            {
                // Do not invent zero writes. Honor verified reads and upper-bound every remaining
                // token at the most expensive qualified cache-write rate (including a 12h upgrade).
                weightedInput = (input - cached) * longCacheWriteRate + cached * cacheReadRate;
            }
            // Reasoning tokens are already included in output_tokens. Never charge them twice.
            if (usage.TryGetProperty("output_tokens_details", out var outputDetails) &&
                outputDetails.TryGetProperty("reasoning_tokens", out _) && Count(outputDetails, "reasoning_tokens") > output) return null;
            var longContext = input > LongContextThreshold;
            var inputCost = weightedInput * (longContext ? LongContextInputMultiplier : 1m);
            var outputCost = output * (longContext ? MinimumOutputUsdPerMillion : outputRate);
            return new((inputCost + outputCost) / 1_000_000m, complete);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or OverflowException or FormatException)
        {
            // No trustworthy complete breakdown: caller retains conservative liability.
            return null;
        }
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Count(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name).GetInt64();
        if (value < 0 || value > ContextTokens) throw new InvalidOperationException("Invalid token usage.");
        return value;
    }
}
