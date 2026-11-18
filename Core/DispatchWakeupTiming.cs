using System.Text.Json;

namespace Forge.Core;

/// <summary>A durable queue event to observed admission interval, NOT pure scheduler latency.
/// Dependency, review, stage, cooldown and capacity waits may intervene. Missing attribution stays unknown.</summary>
public sealed record DispatchWakeupTiming(string Cause, long EventId, string EventKind, string EventDetail,
    DateTimeOffset EventAt, DateTimeOffset ObservedAdmissionAt)
{
    public double ElapsedSeconds => (ObservedAdmissionAt - EventAt).TotalSeconds;
    public bool IncludesIntentionalWaits => true;

    public static DispatchWakeupTiming? Read(IssueRecord task)
    {
        var value = task.GetMetadata("dispatchWakeup");
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonSerializer.Deserialize<DispatchWakeupTiming>(value); }
        catch (JsonException) { return null; } // Older/unstructured metadata has no measured evidence.
    }
}
