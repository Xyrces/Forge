using System.Globalization;

namespace Forge.Core;

/// <summary>Measured observation intervals, not inferred time since a task or PR was created.</summary>
public static class ReviewWaitTiming
{
    public static Dictionary<string, object> Observe(IssueRecord task, bool waiting, DateTimeOffset now) =>
        Observe(task, waiting, now, "humanReviewWait");

    public static Dictionary<string, object> ObserveExternal(IssueRecord task, bool waiting, DateTimeOffset now) =>
        Observe(task, waiting, now, "reviewExternalWait");

    private static Dictionary<string, object> Observe(IssueRecord task, bool waiting, DateTimeOffset now, string prefix)
    {
        var patch = new Dictionary<string, object>();
        var active = DateTimeOffset.TryParse(task.GetMetadata(prefix + "StartedAt"), out var start)
            && string.IsNullOrEmpty(task.GetMetadata(prefix + "EndedAt"));
        if (waiting && !active)
        {
            patch[prefix + "StartedAt"] = now.ToString("O");
            patch[prefix + "EndedAt"] = null!;
        }
        else if (!waiting && active)
        {
            patch[prefix + "EndedAt"] = now.ToString("O");
            patch[prefix + "Seconds"] = (CompletedSeconds(task, prefix) + Math.Max(0, (now - start).TotalSeconds))
                .ToString("R", CultureInfo.InvariantCulture);
        }
        return patch;
    }

    public static double CompletedSeconds(IssueRecord task) => CompletedSeconds(task, "humanReviewWait");
    public static double ExternalSeconds(IssueRecord task) => CompletedSeconds(task, "reviewExternalWait");

    private static double CompletedSeconds(IssueRecord task, string prefix) =>
        double.TryParse(task.GetMetadata(prefix + "Seconds"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
}
