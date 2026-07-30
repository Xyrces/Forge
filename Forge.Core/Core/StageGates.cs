namespace Forge.Core;

/// <summary>
/// Optional operator review gates at the pipeline's major automatic
/// transitions. A held gate pauses that stage (the stage's scheduler
/// skips its tick; a held merge leaves the watch live and unmerged)
/// until the operator releases it via the dashboard or API.
///
/// <para>
/// State lives in the project's MemoryStore as
/// <c>gate/&lt;stage&gt;</c> = "hold" | "open" (absent = open) — no
/// schema change, and the value is inspectable with the normal
/// memory tooling. v1 gates are per-project-memory; Program.cs
/// wires the primary project's store everywhere, so effectively
/// global until multi-project dispatch lands.
/// </para>
/// </summary>
public sealed class StageGates
{
    public const string Design = "design";
    public const string Groom = "groom";
    public const string Sprint = "sprint";
    public const string Merge = "merge";
    public static readonly string[] All = { Design, Groom, Sprint, Merge };

    private const string HoldValue = "hold";
    private const string OpenValue = "open";

    private readonly MemoryStore _memory;

    public StageGates(MemoryStore memory) => _memory = memory;

    public static bool IsKnown(string stage) => All.Contains(stage, StringComparer.OrdinalIgnoreCase);

    private static string Key(string stage) => $"gate/{stage}";

    public async Task<bool> IsHeldAsync(string stage, CancellationToken ct = default)
        => string.Equals(
            (await _memory.RecallAsync(Key(stage), ct)).FirstOrDefault()?.Body,
            HoldValue, StringComparison.Ordinal);

    public async Task HoldAsync(string stage, CancellationToken ct = default)
        => await _memory.RememberAsync(Key(stage), HoldValue, ttlDays: null, ct);

    public async Task ReleaseAsync(string stage, CancellationToken ct = default)
        => await _memory.RememberAsync(Key(stage), OpenValue, ttlDays: null, ct);

    public async Task<IReadOnlyDictionary<string, bool>> SnapshotAsync(CancellationToken ct = default)
    {
        var snap = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var stage in All)
        {
            snap[stage] = await IsHeldAsync(stage, ct);
        }
        return snap;
    }
}
