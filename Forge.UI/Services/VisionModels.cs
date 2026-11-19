namespace Forge.Dashboard.Services;

/// <summary>Receive headroom includes JSON escaping around bounded editor and session payloads.</summary>
public static class VisionUiLimits
{
    public const int MaxSourceCharacters = 256 * 1024;
    public const int MaxSessionBytes = 512 * 1024;
    public const int MaxHubMessageBytes = 4 * 1024 * 1024;
}

public sealed record VisionMapDto(string Revision, string Source, IReadOnlyList<VisionNodeDto> Nodes);
public sealed record VisionNodeDto(string Id, string? ParentId, string Title, string Text);
public sealed record VisionEditDto(string NodeId, string Operation, string Before, string After, string? Title = null);
public sealed record VisionMessageDto(string Role, string Text);
public sealed record VisionReplyDto(string Message, IReadOnlyList<VisionEditDto> Edits);

public sealed class VisionSessionDto
{
    public string BaseRevision { get; set; } = "";
    public List<VisionMessageDto> Messages { get; set; } = [];
    public List<VisionEditDto> Pending { get; set; } = [];
    public string Input { get; set; } = "";
    public string? SelectedId { get; set; }
    public string? SourceDraft { get; set; }
    public string? SourceRevision { get; set; }
    public bool GuidancePending { get; set; }
    public bool MustRefresh { get; set; }
}

public sealed class VisionRequestException(string message, System.Net.HttpStatusCode status, string? code)
    : HttpRequestException(message, null, status)
{
    public string? Code { get; } = code;
}
