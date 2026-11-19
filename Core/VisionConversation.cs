namespace Forge.Core;

public sealed record VisionChatMessage(string Role, string Text);

/// <summary>The complete proposed draft after this turn, relative to the supplied map revision.</summary>
public sealed record VisionReply(string Message, IReadOnlyList<VisionMapEdit> Edits);

public interface IVisionConversation
{
    Task<VisionReply> ReplyAsync(
        string projectId,
        VisionMapDocument map,
        IReadOnlyList<VisionChatMessage> messages,
        IReadOnlyList<VisionMapEdit> pending,
        CancellationToken ct);
}
