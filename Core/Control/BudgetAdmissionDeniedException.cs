using System.Text.Json;

namespace Forge.Core.Control;

/// <summary>A confirmed ledger refusal before provider generation, not a billed cost or uncertain publication.</summary>
public sealed class BudgetAdmissionDeniedException(decimal requestedUsd, decimal availableUsd)
    : InvalidOperationException(FormattableString.Invariant($"This request needs a ${requestedUsd:0.######} reservation. The available budget is ${availableUsd:0.######}. Forge stopped this request before generation."))
{
    public decimal RequestedUsd { get; } = requestedUsd;
    public decimal AvailableUsd { get; } = availableUsd;
    private const string Prefix = "forge-budget-denied-v1:";
    public string ToWorkerError() => Prefix + JsonSerializer.Serialize(new[] { RequestedUsd, AvailableUsd });
    public static BudgetAdmissionDeniedException? FromWorkerError(string? error)
    {
        if (error is null || !error.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var values = JsonSerializer.Deserialize<decimal[]>(error[Prefix.Length..]);
            return values is { Length: 2 } && values[0] > 0 && values[0] > values[1]
                ? new(values[0], values[1]) : null;
        }
        catch (JsonException) { return null; } // Invalid envelopes retain the ordinary worker failure path.
    }
}
