using System.Data;
using System.Text.Json;
using Forge.Core.Db;

namespace Forge.Core;

public sealed record ReviewOriginObservation(long Id, string Author, string ReportedHead, DateTimeOffset? SubmittedAt);
public sealed record VerifiedReviewOrigin(long Id, string Author, string Head, DateTimeOffset? SubmittedAt);

public sealed partial class MemoryStore
{
    private sealed record ReviewOrigin(string Author, DateTimeOffset? SubmittedAt, string? Head);
    private sealed record ReviewOriginLedger(string Head, DateTimeOffset ObservedAt, long PriorReviewId,
        Dictionary<long, ReviewOrigin> Reviews, string? ResetMarker = null);

    /// <summary>
    /// Pin authoring revisions, not GitHub's mutable review commit_id. The first observation
    /// of a head establishes a durable boundary; pre-existing approvals are not grandfathered.
    /// Uses the caller's authenticated GitHub server time, never the host clock.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, string?>> ObserveReviewOriginsAsync(string repository, int pr,
        string head, DateTimeOffset serverTime, IReadOnlyList<ReviewOriginObservation> observations,
        IReadOnlyList<VerifiedReviewOrigin>? authored = null, CancellationToken ct = default, string? resetMarker = "none")
    {
        if (string.IsNullOrWhiteSpace(repository) || pr <= 0 || string.IsNullOrWhiteSpace(head)
            || observations.Count > 10000 || observations.Any(o => o.Id <= 0)
            || observations.Select(o => o.Id).Distinct().Count() != observations.Count)
            throw new ArgumentException("A bounded, unambiguous repository review observation is required.");
        var key = $"review-origins/v1/{repository.ToLowerInvariant()}/{pr}";
        await using var connection = await _db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        string? json;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            var locked = _db.Provider == ForgeDbProvider.SqlServer ? " WITH (UPDLOCK,HOLDLOCK)" : "";
            read.CommandText = $"SELECT body FROM {T("memory")}{locked} WHERE [key]=@key";
            read.AddParam("@key", key);
            json = await read.ExecuteScalarAsync(ct) as string;
        }
        var ledger = json is null ? null : JsonSerializer.Deserialize<ReviewOriginLedger>(json)
            ?? throw new InvalidOperationException("Review provenance ledger is unreadable.");
        var isNewHead = ledger is null || ledger.Head != head || ledger.ResetMarker != resetMarker;
        if (isNewHead)
            ledger = new(head, serverTime, observations.Select(o => o.Id).DefaultIfEmpty(0).Max(), ledger?.Reviews ?? [], resetMarker);
        var current = ledger!;
        if (current.Reviews.Count + observations.Count(o => !current.Reviews.ContainsKey(o.Id)) > 10000)
            throw new InvalidOperationException("Review provenance exceeds its safe bound; operator investigation required.");
        Dictionary<long, string?> result = [];
        foreach (var observation in observations)
        {
            var proof = authored?.SingleOrDefault(p => p.Id == observation.Id
                && string.Equals(p.Author, observation.Author, StringComparison.OrdinalIgnoreCase)
                && p.SubmittedAt == observation.SubmittedAt);
            if (!current.Reviews.TryGetValue(observation.Id, out var origin))
            {
                var originalHead = proof?.Head;
                if (originalHead is null && resetMarker is not null && !isNewHead && observation.Id > current.PriorReviewId
                    && observation.ReportedHead == head && observation.SubmittedAt is { } submitted
                    && submitted >= current.ObservedAt && submitted <= serverTime)
                    originalHead = head;
                origin = new(observation.Author, observation.SubmittedAt, originalHead);
                current.Reviews.Add(observation.Id, origin);
            }
            // A trusted local publication receipt may finish committing after the first poll.
            // It may fill an unknown origin, but cannot replace an already pinned one.
            if (origin.Head is null && proof is not null
                && string.Equals(origin.Author, proof.Author, StringComparison.OrdinalIgnoreCase)
                && origin.SubmittedAt == proof.SubmittedAt)
                current.Reviews[observation.Id] = origin = origin with { Head = proof.Head };
            result[observation.Id] = string.Equals(origin.Author, observation.Author, StringComparison.OrdinalIgnoreCase)
                && origin.SubmittedAt == observation.SubmittedAt ? origin.Head : null;
        }
        var after = JsonSerializer.Serialize(current);
        if (after != json) await UpsertAsync(connection, transaction, key, after, null, DateTime.UtcNow, ct);
        await transaction.CommitAsync(ct);
        return result;
    }
}
