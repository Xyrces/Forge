using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

namespace Forge.AgentTools;

/// <summary>
/// Paginated access to a PR's full unified diff for the Reviewer
/// role. The review prompt inlines a size-bounded, file-wise excerpt;
/// this tool serves the complete diff in windows so the reviewer can
/// read any hunk in full without blowing up the prompt context
/// (operator direction 2026-07-30: bounded paste + paginated drill-in
/// instead of either a blind 12k cut or a 200k dump).
/// </summary>
public sealed class PrDiffTool
{
    public const int DefaultWindowChars = 12_000;

    private readonly string _diff;
    private readonly List<(string Path, int Start, int Length)> _files = new();

    public PrDiffTool(string diff)
    {
        _diff = diff ?? "";
        var index = 0;
        while (index < _diff.Length)
        {
            var next = _diff.IndexOf("\ndiff --git ", index + 1, StringComparison.Ordinal);
            var end = next < 0 ? _diff.Length : next + 1;
            var headerEnd = _diff.IndexOf('\n', index);
            var header = headerEnd < 0 ? _diff[index..] : _diff[index..headerEnd];
            _files.Add((PathFromHeader(header), index, end - index));
            index = end;
        }
    }

    /// <summary>File manifest: (path, char offset, length) per file in
    /// the diff. Internal for tests.</summary>
    internal IReadOnlyList<(string Path, int Start, int Length)> Files => _files;

    private static string PathFromHeader(string header)
    {
        // "diff --git a/Foo.cs b/Foo.cs" -> "Foo.cs" (take the b/ side;
        // renames read better as the new path).
        const string marker = " b/";
        var i = header.IndexOf(marker, StringComparison.Ordinal);
        return i < 0 ? header.Trim() : header[(i + marker.Length)..].Trim();
    }

    public AIFunction AsAIFunction() => AIFunctionFactory.Create(
        ([Description("Optional file path from the manifest to read just that file's diff. Omit for the whole-diff window (or the manifest on the first call).")] string? file = null,
         [Description("Char offset within the selected scope (whole diff or one file). Defaults to 0.")] int offset = 0,
         [Description("Max chars to return. Defaults to 12000.")] int limit = DefaultWindowChars)
            => Page(file, offset, limit),
        name: "pr_diff",
        description: "Page through the PR's full unified diff. Call with no arguments first for the file " +
                     "manifest (paths, sizes), then drill into a file or window. Use this to read any part " +
                     "of the change in full before judging it.");

    /// <summary>Serve a window of the diff. Internal for tests.</summary>
    internal string Page(string? file, int offset, int limit)
    {
        if (_diff.Length == 0) return "(empty diff)";
        limit = Math.Clamp(limit, 500, 50_000);

        if (file is null && offset == 0)
        {
            var sb = new StringBuilder();
            sb.Append("diff manifest: ").Append(_files.Count).Append(" file(s), ")
                .Append(_diff.Length).Append(" chars total\n");
            foreach (var (path, start, length) in _files)
            {
                sb.Append("- ").Append(path).Append("  [chars ").Append(start)
                    .Append("..").Append(start + length).Append(", ").Append(length).Append(" chars]\n");
            }
            sb.Append("Call pr_diff(file: \"<path>\") for one file, or pr_diff(offset: N) to window the whole diff.");
            return sb.ToString();
        }

        string scope;
        string scopeLabel;
        if (file is not null)
        {
            var match = _files.FirstOrDefault(f =>
                string.Equals(f.Path, file, StringComparison.Ordinal));
            if (match == default)
            {
                return $"file '{file}' not in the diff manifest — call pr_diff() with no arguments to list files";
            }
            scope = _diff.Substring(match.Start, match.Length);
            scopeLabel = $"file {match.Path}";
        }
        else
        {
            scope = _diff;
            scopeLabel = "whole diff";
        }

        offset = Math.Clamp(offset, 0, scope.Length);
        var take = Math.Min(limit, scope.Length - offset);
        var window = scope.Substring(offset, take);
        var end = offset + take;
        var footer = end < scope.Length
            ? $"\n--- showing chars {offset}..{end} of {scope.Length} ({scopeLabel}); call pr_diff({(file is not null ? $"file: \"{file}\", " : "")}offset: {end}) for the next window ---"
            : $"\n--- end of {scopeLabel} ({scope.Length} chars) ---";
        return window + footer;
    }
}
