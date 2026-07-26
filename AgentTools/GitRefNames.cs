using System.Text;

namespace Forge.AgentTools;

/// <summary>
/// Shared git ref-name and directory-path sanitization.
/// Used by both <see cref="GitWorktreeService"/> and
/// <see cref="Orchestrator.Workflow.WorktreeExecutor"/>
/// so an issue id always maps to the same branch/directory
/// name regardless of which component computes it.
/// </summary>
public static class GitRefNames
{
    /// <summary>
    /// Characters invalid in git ref names (per git-check-ref-format)
    /// plus <see cref="Path.GetInvalidFileNameChars"/>, which on
    /// Linux only covers '\0' and '/'.
    /// </summary>
    private static readonly HashSet<char> InvalidRefChars = BuildInvalidSet();

    private static HashSet<char> BuildInvalidSet()
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());

        // Git-check-ref-format rejects these even though they are
        // valid in filenames on Linux: '~', '^', ':', '?', '*',
        // '[', '\', ' ', '@', '!'.
        foreach (var c in "~^:?*[\\ @!")
            invalid.Add(c);

        // Control characters (0x00–0x1F and 0x7F).
        for (var i = 0; i < 0x20; i++)
            invalid.Add((char)i);
        invalid.Add((char)0x7F);

        return invalid;
    }

    /// <summary>
    /// Replaces every character invalid in a git ref name with '_'.
    /// Also handles git's ".." prohibition and the no-leading/
    /// trailing-dot rule by replacing those '.' with '_'.
    /// </summary>
    public static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? string.Empty;

        // First pass: replace known-invalid chars with '_'.
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(InvalidRefChars.Contains(c) ? '_' : c);

        // Second pass: prevent ".." sequences and leading/trailing '.'.
        var result = sb.ToString();
        if (!result.Contains("..") && !result.StartsWith('.') && !result.EndsWith('.'))
            return result;

        sb = new StringBuilder(result.Length);
        for (var i = 0; i < result.Length; i++)
        {
            var c = result[i];
            if (c == '.')
            {
                // Replace '.' that forms ".." with its neighbor.
                if ((i > 0 && result[i - 1] == '.') ||
                    (i < result.Length - 1 && result[i + 1] == '.'))
                {
                    sb.Append('_');
                    continue;
                }
                // Replace leading/trailing '.'.
                if (i == 0 || i == result.Length - 1)
                {
                    sb.Append('_');
                    continue;
                }
            }
            sb.Append(c);
        }

        return sb.ToString();
    }
}
