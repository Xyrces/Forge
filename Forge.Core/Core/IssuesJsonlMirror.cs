using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Forge.Core;

/// <summary>
/// Background service that mirrors the issue store to a JSONL file
/// for human consumption (<c>tail</c>, <c>grep</c>, git-tracked). This
/// is Phase 4 of <c>docs/embedded-issues.md</c> — a viewer artifact,
/// not the source of truth. The DB is canonical on startup; the
/// JSONL is rewritten on a fixed interval (default 5s).
///
/// <para>
/// Atomicity: writes go to <c>{path}.tmp</c>, then are renamed over
/// the target. <c>tail -f</c> readers never see a half-written file.
/// </para>
///
/// <para>
/// Sort order: by issue id, lexicographic. Same order the dashboard
/// uses; lets <c>git diff</c> on the file show clean add/remove lines.
/// </para>
/// </summary>
public sealed class IssuesJsonlMirror : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IIssueStore _issues;
    private readonly string _path;
    private readonly TimeSpan _interval;
    private readonly ILogger<IssuesJsonlMirror> _logger;

    public IssuesJsonlMirror(IIssueStore issues, string path, ILogger<IssuesJsonlMirror> logger,
        TimeSpan? interval = null)
    {
        _issues = issues;
        _path = path;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// One-shot rewrite. Used by the tests and exposed publicly so
    /// callers can force a refresh after a write that matters (e.g.
    /// after the orchestrator's claim loop).
    /// </summary>
    public async Task RewriteAsync(CancellationToken ct)
    {
        try
        {
            var issues = await _issues.ListAsync(new IssueFilter(), ct);
            var sorted = issues.OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read))
            await using (var sw = new StreamWriter(fs))
            {
                foreach (var issue in sorted)
                {
                    var line = JsonSerializer.Serialize(ToWireFormat(issue), JsonOptions);
                    await sw.WriteLineAsync(line);
                }
            }
            // Atomic on POSIX; on Windows File.Move with overwrite=true
            // uses ReplaceFile internally and is good enough for our
            // tail -f use case.
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Mirror failure must never break the orchestrator.
            _logger.LogWarning(ex, "JSONL mirror rewrite failed for {Path}", _path);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Rewrite once at startup so the file exists even before the
        // first interval tick (operators tailing the file immediately
        // after launch see a current snapshot).
        await RewriteAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RewriteAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private static object ToWireFormat(IssueRecord r) => new
    {
        id = r.Id,
        type = r.Type,
        title = r.Title,
        description = r.Description,
        status = r.Status.ToString(),
        priority = r.Priority,
        assignee = r.Assignee,
        createdAt = r.CreatedAt,
        updatedAt = r.UpdatedAt,
        closedAt = r.ClosedAt,
        parentIssueId = r.ParentIssueId,
        metadata = ParseMetadata(r.MetadataJson),
    };

    private static Dictionary<string, object>? ParseMetadata(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var dict = new Dictionary<string, object>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText())!;
            }
            return dict;
        }
        catch
        {
            return null;
        }
    }
}