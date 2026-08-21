using Forge.Core;

namespace Forge.Dashboard;

/// <summary>
/// P0.5: loads the project's vision document. The file path
/// defaults to <c>docs/MASTER_DESIGN.md</c> relative to the
/// workspace root; the operator can override via
/// <c>appsettings.json</c> -> <c>vision.path</c>.
///
/// <para>
/// Vision is loaded once at startup. The dashboard's Vision tab
/// shows the loaded content + a refresh button that re-reads the
/// file (so the operator can edit it without restarting the
/// orchestrator). Vision is also injected into every agent
/// prompt as a high-priority memory key (<c>vision/master</c>).
/// </para>
/// </summary>
public sealed class VisionStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private VisionSnapshot _snapshot = VisionSnapshot.Missing;

    public VisionStore(string workspaceRoot, string relativePath)
    {
        RelativePath = relativePath;
        _filePath = Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.Combine(workspaceRoot, relativePath);
    }

    public string AbsolutePath => _filePath;

    /// <summary>The configured relative path (per-project vision
    /// resolution re-applies it against each project's root).</summary>
    public string RelativePath { get; }

    public VisionSnapshot Get() { lock (_lock) return _snapshot; }

    public VisionSnapshot Reload()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _snapshot = VisionSnapshot.Missing;
                    return _snapshot;
                }
                var content = File.ReadAllText(_filePath);
                var lastModified = File.GetLastWriteTimeUtc(_filePath);
                _snapshot = new VisionSnapshot(true, _filePath, content, lastModified);
            }
            catch (Exception ex)
            {
                _snapshot = new VisionSnapshot(false, _filePath, $"error reading vision: {ex.Message}", null);
            }
            return _snapshot;
        }
    }

    /// <summary>
    /// Writes (creating parent dirs as needed) and reloads the
    /// vision document. This is the dashboard editor's save path —
    /// previously the file had to "magically exist" with no in-app
    /// mechanism to create it.
    /// </summary>
    public VisionSnapshot Write(string content)
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, content);
        }
        return Reload();
    }
}

public sealed record VisionSnapshot(
    bool Exists,
    string Path,
    string Content,
    DateTime? LastModifiedUtc)
{
    public static readonly VisionSnapshot Missing = new(false, "", "", null);
}