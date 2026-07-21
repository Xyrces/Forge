namespace Forge.Orchestrator;

/// <summary>
/// P5.5: abstraction over the auto-memory extraction step that
/// runs after a successful PR open. Production wires
/// <see cref="MemoryExtractor"/> (which calls the kilo gateway).
/// Tests can swap in <see cref="NoOpMemoryExtractor"/>
/// or a scripted implementation that returns a fixed
/// <see cref="ExtractionResult"/>.
/// </summary>
public interface IMemoryExtractor
{
    Task<ExtractionResult> ExtractAsync(
        string issueId, string? modelText, CancellationToken ct = default);
}

/// <summary>
/// Test + default fallback. Returns an empty result with no
/// error. Used in tests that don't exercise P5.5 and as a
/// safety net if a real extractor is misconfigured.
/// </summary>
public sealed class NoOpMemoryExtractor : IMemoryExtractor
{
    public Task<ExtractionResult> ExtractAsync(
        string issueId, string? modelText, CancellationToken ct = default)
        => Task.FromResult(new ExtractionResult(
            IssueId: issueId,
            SourceChars: modelText?.Length ?? 0,
            ExtractedCount: 0,
            PersistedKeys: Array.Empty<string>(),
            Error: null));
}