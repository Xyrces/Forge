using System.Text.Json;

namespace Forge.Core;

public enum RequiredReview { Automated, Human, Both }

public sealed record ProjectReviewSettings(RequiredReview Mode, string? NominatedHuman);

public sealed record HumanReviewHold(bool Required, bool SyncPending);

/// <summary>Durable project policy and task intent live outside worker-authored issue metadata.</summary>
public sealed class PullRequestReviewPolicy(MemoryStore memory)
{
    private const string PolicyKey = "review/project-mode";
    private static string HoldKey(string taskId) => $"review/task-hold/{taskId}";

    public async Task<ProjectReviewSettings> GetSettingsAsync(CancellationToken ct)
    {
        var body = (await memory.GetAsync(PolicyKey, ct))?.Body;
        if (body is null) return new(RequiredReview.Automated, null);
        var setting = body.StartsWith('{')
            ? JsonSerializer.Deserialize<ProjectReviewSettings>(body) ?? throw new InvalidDataException("Invalid review policy.")
            : new ProjectReviewSettings(Enum.Parse<RequiredReview>(body, true), null);
        if (!Enum.IsDefined(setting.Mode)) throw new InvalidDataException("Invalid review mode.");
        return setting with { NominatedHuman = NormalizeNominee(setting.NominatedHuman) };
    }
    public static string? NormalizeNominee(string? login)
    {
        login = login?.Trim();
        if (string.IsNullOrEmpty(login)) return null;
        if (!System.Text.RegularExpressions.Regex.IsMatch(login, "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$"))
            throw new ArgumentException("Enter a human GitHub login, without @, or leave it blank for any eligible human.");
        return login;
    }
    public async Task<RequiredReview> GetModeAsync(CancellationToken ct) => (await GetSettingsAsync(ct)).Mode;
    public async Task SetModeAsync(RequiredReview mode, CancellationToken ct)
        => await memory.RememberAsync(PolicyKey, JsonSerializer.Serialize((await GetSettingsAsync(ct)) with { Mode = mode }), ct: ct);
    public async Task SetModeAsync(RequiredReview mode, string actor, CancellationToken ct)
        => await SetAsync(mode, (await GetSettingsAsync(ct)).NominatedHuman, actor, ct);
    public Task SetAsync(RequiredReview mode, string? nominatedHuman, string actor, CancellationToken ct)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Invalid review mode.");
        return memory.RememberAuditedAsync(PolicyKey, JsonSerializer.Serialize(new ProjectReviewSettings(mode, NormalizeNominee(nominatedHuman))), actor, "review/project-mode-audit/", ct);
    }

    public async Task<HumanReviewHold> GetHoldAsync(string taskId, CancellationToken ct)
    {
        var record = await memory.GetAsync(HoldKey(taskId), ct);
        return record is null ? new(false, false) : JsonSerializer.Deserialize<HumanReviewHold>(record.Body)
            ?? throw new InvalidDataException("Invalid saved review hold.");
    }

    public Task SetHoldAsync(string taskId, HumanReviewHold hold, CancellationToken ct)
        => memory.RememberAsync(HoldKey(taskId), JsonSerializer.Serialize(hold), ct: ct);

    public Task SetHoldAsync(string taskId, HumanReviewHold hold, string actor, CancellationToken ct)
        => memory.RememberAuditedAsync(HoldKey(taskId), JsonSerializer.Serialize(hold), actor, "review/task-hold-audit/", ct);
}
