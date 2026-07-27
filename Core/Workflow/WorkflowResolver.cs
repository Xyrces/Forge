using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.Core.Workflow;

/// <summary>
/// Resolves the effective workflow definition: the published
/// override at memory key <c>workflow/live</c> when present and
/// valid, otherwise the built-in default. Resolution happens per
/// read — publishing never requires a restart, and machinery that
/// consumes policies sees them at its next evaluation. A corrupt
/// override falls back to the default (fail-safe: the pipeline can
/// never be bricked by a bad publish).
/// </summary>
public sealed class WorkflowResolver
{
    public const string LiveKey = "workflow/live";
    public const string DraftKey = "workflow/draft";
    public const string VersionsPrefix = "workflow/versions/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly MemoryStore? _memory;

    public WorkflowResolver(MemoryStore? memory) => _memory = memory;

    public async Task<WorkflowDefinition> ResolveAsync(CancellationToken ct = default)
    {
        if (_memory is null)
        {
            return WorkflowDefaults.Definition;
        }
        var body = (await _memory.RecallAsync(LiveKey, ct)).FirstOrDefault()?.Body;
        return TryParse(body) ?? WorkflowDefaults.Definition;
    }

    public static WorkflowDefinition? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            var d = JsonSerializer.Deserialize<WorkflowDefinition>(body, JsonOptions);
            return d is { Steps.Count: > 0 } ? d : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize(WorkflowDefinition definition)
        => JsonSerializer.Serialize(definition, JsonOptions);
}
