using System.Text.Json;

namespace Forge.Core;

/// <summary>
/// One flat verdict from a run-quality gate evaluation. Derived from
/// the task's <c>planGate</c> metadata — read-only, no new write path.
/// </summary>
public sealed record GateVerdictRecord(
    string TaskId,
    string Gate,
    string Outcome,
    string Feedback,
    DateTime Timestamp);

/// <summary>
/// Read-model helper that queries issues with <c>planGate</c>
/// metadata (run-quality-gate verdicts) and returns a flat,
/// time-ordered list of the most recent verdicts.
///
/// <para>No new write path — reads existing <c>issue.metadata_json</c>
/// only. Efficient enough at current scale via the
/// <c>ix_issue_updated_at</c> index; the <c>LIKE</c> filter on
/// <c>metadata_json</c> is acceptable for low-thousands row counts.</para>
/// </summary>
public sealed class GateVerdictReader
{
    private readonly IIssueStore _issues;

    public GateVerdictReader(IIssueStore issues)
    {
        _issues = issues ?? throw new ArgumentNullException(nameof(issues));
    }

    /// <summary>
    /// Return the most recent <paramref name="limit"/> gate verdicts
    /// across all tasks, ordered by the task's last-updated timestamp
    /// descending (most recent first). Unparseable <c>planGate</c>
    /// metadata rows are silently skipped.
    /// </summary>
    public async Task<IReadOnlyList<GateVerdictRecord>> ListRecentAsync(
        int limit = 50, CancellationToken ct = default)
    {
        if (limit <= 0) return Array.Empty<GateVerdictRecord>();

        // ISSUE STORE does not expose a raw SQL query, so we go
        // through the public store interface:
        //   ListAsync with an IssueFilter that matches all tasks
        // We need to scan issues. Use a generous max — at current
        // scale the total task count is low thousands.
        var all = await _issues.ListAsync(new IssueFilter { Type = "task" }, ct);
        if (all.Count == 0) return Array.Empty<GateVerdictRecord>();

        var verdicts = new List<GateVerdictRecord>();
        foreach (var task in all)
        {
            var raw = task.GetMetadata("planGate");
            if (raw is null) continue;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("verdicts", out var arr) ||
                    arr.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var v in arr.EnumerateArray())
                {
                    var gate = v.TryGetProperty("gate", out var g) ? g.GetString() ?? "" : "";
                    var outcome = v.TryGetProperty("outcome", out var o) ? o.GetString() ?? "" : "";
                    var feedback = v.TryGetProperty("feedback", out var f) ? f.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(gate)) continue;

                    verdicts.Add(new GateVerdictRecord(
                        TaskId: task.Id,
                        Gate: gate,
                        Outcome: outcome,
                        Feedback: feedback,
                        Timestamp: task.UpdatedAt));
                }
            }
            catch (JsonException)
            {
                // Malformed row — skip gracefully.
                continue;
            }
        }

        // Sort by timestamp descending, take limit.
        verdicts.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
        return verdicts.Count <= limit
            ? verdicts.AsReadOnly()
            : verdicts.GetRange(0, limit).AsReadOnly();
    }

    /// <inheritdoc cref="ListRecentAsync(int, CancellationToken)"/>
    public Task<IReadOnlyList<GateVerdictRecord>> ListRecentAsync(CancellationToken ct)
        => ListRecentAsync(50, ct);
}
