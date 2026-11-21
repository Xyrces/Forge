using Microsoft.Extensions.Logging;

namespace Forge.Dashboard;

/// <summary>
/// Reads the running image's revision artifact, never a checkout or assembly revision.
/// A missing, unreadable or malformed artifact has no authoritative revision.
/// </summary>
public sealed class RunningRevisionReader
{
    public const string ArtifactPath = "/app/source-revision";

    private readonly ILogger _logger;
    private readonly Func<string, CancellationToken, Task<string>> _readAllText;

    public RunningRevisionReader(ILogger logger) : this(logger, File.ReadAllTextAsync) { }

    // Inject I/O, not the artifact path: every consumer must use the same authority.
    internal RunningRevisionReader(ILogger logger, Func<string, CancellationToken, Task<string>> readAllText)
    {
        _logger = logger;
        _readAllText = readAllText;
    }

    public async Task<string?> ReadAsync(CancellationToken ct = default)
    {
        string revision;
        try
        {
            revision = (await _readAllText(ArtifactPath, ct)).Trim();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Running revision artifact unavailable at {Path}", ArtifactPath);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Running revision artifact unreadable at {Path}", ArtifactPath);
            return null;
        }

        if (revision.Length == 40 && revision.All(char.IsAsciiHexDigit))
            return revision;

        _logger.LogWarning("Running revision artifact at {Path} is not a full 40-character hexadecimal SHA", ArtifactPath);
        return null;
    }
}
