using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Forge.Core;

public sealed record WorkerDependencyRequest(string Id, string Tool, string Reason, string SuggestedChange,
    string Status = "pending", DateTime? ReviewedAt = null);

public static class WorkerDependencyRequests
{
    internal const string Prefix = "workerDependencyRequest/";
    internal const string DecisionPrefix = "workerDependencyDecision/";
    internal sealed record Proposal(string Tool, string Reason, string SuggestedChange);
    internal sealed record Decision(string Status, DateTime ReviewedAt);

    public static IReadOnlyList<WorkerDependencyRequest> Read(IssueRecord issue)
    {
        using var document = JsonDocument.Parse(issue.MetadataJson);
        var result = new List<WorkerDependencyRequest>();
        foreach (var field in document.RootElement.EnumerateObject())
        {
            if (!field.Name.StartsWith(Prefix, StringComparison.Ordinal) || field.Value.ValueKind == JsonValueKind.Null) continue;
            var proposal = field.Value.Deserialize<Proposal>() ?? throw new JsonException("Missing dependency proposal.");
            var id = field.Name[Prefix.Length..];
            var decision = document.RootElement.TryGetProperty(DecisionPrefix + id, out var value)
                ? value.Deserialize<Decision>() : null;
            result.Add(new(id, proposal.Tool, proposal.Reason, proposal.SuggestedChange,
                decision?.Status ?? "pending", decision?.ReviewedAt));
        }
        return result.OrderBy(x => x.Status != "pending").ThenBy(x => x.Tool, StringComparer.Ordinal).ToArray();
    }

    internal static (string Id, Proposal Value) Create(string tool, string reason, string suggestedChange)
    {
        static string Validate(string value, string name, int maximum)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximum)
                throw new ArgumentException($"{name} must contain 1–{maximum} characters.");
            return value.Trim();
        }
        var proposal = new Proposal(Validate(tool, "Tool", 128), Validate(reason, "Reason", 2000),
            Validate(suggestedChange, "Suggested change", 2000));
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(proposal)))).ToLowerInvariant();
        return (id, proposal);
    }
}

public sealed partial class IssueStore
{
    public async Task<string> RequestWorkerDependencyAsync(string taskId, string tool, string reason,
        string suggestedChange, CancellationToken ct = default)
    {
        var (id, proposal) = WorkerDependencyRequests.Create(tool, reason, suggestedChange);
        // Immutable, content-addressed proposals and separate operator decisions ensure
        // a repeated worker call cannot overwrite or reset an operator's review.
        await PatchMetadataOnlyAsync(taskId, new Dictionary<string, object>
        { [WorkerDependencyRequests.Prefix + id] = proposal }, null, ct);
        return id;
    }

    public async Task ReviewWorkerDependencyAsync(string taskId, string requestId, string status,
        CancellationToken ct = default)
    {
        if (status is not ("acknowledged" or "dismissed")) throw new ArgumentException("Choose acknowledged or dismissed.");
        var task = await GetAsync(taskId, ct) ?? throw new InvalidOperationException("Task not found.");
        if (!WorkerDependencyRequests.Read(task).Any(x => x.Id == requestId))
            throw new ArgumentException("Dependency request not found.");
        await PatchMetadataOnlyAsync(taskId, new Dictionary<string, object>
        { [WorkerDependencyRequests.DecisionPrefix + requestId] = new WorkerDependencyRequests.Decision(status, DateTime.UtcNow) }, null, ct);
    }
}
