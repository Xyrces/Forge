namespace Forge.Dashboard.Features.Budget;

/// <summary>Nullable fields preserve the distinction between missing data and a real zero.</summary>
public sealed record BudgetSummary(bool? Configured, DateTime? GeneratedAt, decimal? LimitUsd,
    decimal? UsageEstimatedUsd, decimal? ConservativeSettledUsd, decimal? OutstandingUsd,
    long? UncertainRequestCount, decimal? AvailableAfterHoldsUsd, decimal? OperatorReportedUsd)
{
    public void Validate()
    {
        if (Configured is null)
            throw new InvalidDataException("Spending response omitted its configuration state.");
        if (Configured == false) return;
        if (GeneratedAt is null || GeneratedAt == default(DateTime)
            || LimitUsd is null or < 0 || UsageEstimatedUsd is null or < 0
            || ConservativeSettledUsd is null or < 0 || OutstandingUsd is null or < 0
            || OperatorReportedUsd is null or < 0
            || UncertainRequestCount is null or < 0 || AvailableAfterHoldsUsd is null or < 0
            || AvailableAfterHoldsUsd > LimitUsd)
            throw new InvalidDataException("Spending response is incomplete or invalid.");
    }
}
