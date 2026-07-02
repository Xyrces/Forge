using PortHorizon.Agents.Core;

namespace PortHorizon.Agents.Dashboard;

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
        _filePath = Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.Combine(workspaceRoot, relativePath);
    }

    public string AbsolutePath => _filePath;

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
}

public sealed record VisionSnapshot(
    bool Exists,
    string Path,
    string Content,
    DateTime? LastModifiedUtc)
{
    public static readonly VisionSnapshot Missing = new(false, "", "", null);
}