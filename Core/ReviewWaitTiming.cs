using System.Globalization;

namespace Forge.Core;

/// <summary>Measured observation intervals, not inferred time since a task or PR was created.</summary>
public static class ReviewWaitTiming
{
    public static Dictionary<string, object> Observe(IssueRecord task, bool waiting, DateTimeOffset now)
    {
        var patch = new Dictionary<string, object>();
        var active = DateTimeOffset.TryParse(task.GetMetadata("humanReviewWaitStartedAt"), out var start)
            && string.IsNullOrEmpty(task.GetMetadata("humanReviewWaitEndedAt"));
        if (waiting && !active)
        {
            patch["humanReviewWaitStartedAt"] = now.ToString("O");
            patch["humanReviewWaitEndedAt"] = null!;
        }
        else if (!waiting && active)
        {
            patch["humanReviewWaitEndedAt"] = now.ToString("O");
            patch["humanReviewWaitSeconds"] = (CompletedSeconds(task) + Math.Max(0, (now - start).TotalSeconds))
                .ToString("R", CultureInfo.InvariantCulture);
        }
        return patch;
    }

    public static double CompletedSeconds(IssueRecord task) =>
        double.TryParse(task.GetMetadata("humanReviewWaitSeconds"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds > 0 ? seconds : 0;
}
