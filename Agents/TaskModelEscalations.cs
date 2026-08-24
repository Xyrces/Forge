using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Core;

namespace Forge.Agents;

/// <summary>
/// Single-shot per-task model-escalation markers (failure-triage
/// phase 3). The triage agent's <c>escalate_model</c> action writes a
/// marker (<c>llm/taskModel/&lt;projectId&gt;/&lt;taskId&gt;</c>) in
/// the primary project's MemoryStore — the same store as
/// <see cref="RoleModelOverrides"/>. The dispatch path PEEKS the
/// marker to route the task's next dev run onto the role's
/// escalation model (and its own concurrency pool), then CONSUMES it
/// exactly once when the run is built — a consumed marker is gone
/// even when the run fails (no refund; the triage agent may
/// re-escalate on the next failure crossing, spending another of the
/// task's 2/day triage actions).
///
/// <para>
/// Live in-memory snapshot + sync reads, same shape as
/// <see cref="RoleModelOverrides"/>: writes update the snapshot
/// synchronously (no restart), <see cref="LoadAsync"/> rehydrates on
/// startup, and the single-orchestrator process keeps the snapshot
/// authoritative.
/// </para>
/// </summary>
public sealed class TaskModelEscalations
{
    private const string Prefix = "llm/taskModel/";

    public sealed record EscalationMarker(string Note, string Actor, DateTime EscalatedAt);

    private readonly MemoryStore _memory;
    // Cache key: "<projectId>|<taskId>".
    private readonly ConcurrentDictionary<string, EscalationMarker> _cache = new(StringComparer.OrdinalIgnoreCase);

    public TaskModelEscalations(MemoryStore memory) => _memory = memory;

    private static string Key(string projectId, string taskId) => $"{Prefix}{projectId}/{taskId}";
    private static string CacheKey(string projectId, string taskId) => projectId + "|" + taskId;

    /// <summary>Synchronous snapshot read for the dispatch hot path:
    /// does this task carry an unconsumed escalation marker?</summary>
    public bool Peek(string projectId, string taskId)
        => _cache.ContainsKey(CacheKey(projectId, taskId));

    /// <summary>Written by the triage tool AFTER the ledger action is
    /// recorded. An existing marker is overwritten (a re-escalation
    /// before dispatch just replaces the note). Markers carry a 7-day
    /// TTL: an unconsumed marker (rollback to a build that never
    /// consumes them, a parked-then-much-later-requeued task) must not
    /// rehydrate and fire against an unrelated future dispatch —
    /// expired rows are skipped by <see cref="MemoryStore.RecallAsync"/>
    /// on startup.</summary>
    public async Task WriteAsync(string projectId, string taskId, string note, CancellationToken ct = default)
    {
        var marker = new EscalationMarker(note, FailureTriageActors.Triage, DateTime.UtcNow);
        await _memory.RememberAsync(Key(projectId, taskId), Serialize(marker), ttlDays: 7, ct);
        _cache[CacheKey(projectId, taskId)] = marker;
    }

    /// <summary>Read-and-delete: the dispatch path consumes the marker
    /// exactly once. Second and later calls return null.</summary>
    public async Task<EscalationMarker?> ConsumeAsync(string projectId, string taskId, CancellationToken ct = default)
    {
        var key = CacheKey(projectId, taskId);
        if (!_cache.TryRemove(key, out var marker))
            return null;
        await _memory.ForgetAsync(Key(projectId, taskId), ct);
        return marker;
    }

    /// <summary>Rehydrate the snapshot from the store (startup). Key
    /// shape after the prefix is always
    /// <c>&lt;projectId&gt;/&lt;taskId&gt;</c>; malformed rows are
    /// skipped.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        foreach (var row in await _memory.RecallAsync(Prefix, ct))
        {
            var rest = row.Key[Prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0 || slash == rest.Length - 1) continue;
            var parsed = Parse(row.Body);
            if (parsed is null) continue;
            _cache[CacheKey(rest[..slash], rest[(slash + 1)..])] = parsed;
        }
    }

    private static string Serialize(EscalationMarker marker)
        => JsonSerializer.Serialize(new
        {
            note = marker.Note,
            actor = marker.Actor,
            at = marker.EscalatedAt.ToString("O"),
        });

    private static EscalationMarker? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var note = root.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "";
            var actor = root.TryGetProperty("actor", out var a) ? a.GetString() ?? FailureTriageActors.Triage : FailureTriageActors.Triage;
            var at = root.TryGetProperty("at", out var t)
                && DateTime.TryParse(t.GetString(), out var parsed)
                ? parsed.ToUniversalTime()
                : DateTime.UtcNow;
            return new EscalationMarker(note, actor, at);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
