namespace Forge.AgentTools;

/// <summary>
/// Deterministic pre-push hygiene gate: files ADDED by the branch are
/// refused when they are junk artifacts (editor/backup/merge-reject
/// residue) or oversized new blobs. This class of mistake must not
/// depend on an LLM catching it — observed live 2026-07-30:
/// porthorizon task-17 pushed UmbilicalConnectorSystem.cs.bak (a
/// working backup swept in by git add -A) and the reviewer had to
/// burn a round flagging it.
/// </summary>
public static class PushHygiene
{
    /// <summary>New-file size ceiling. Larger blobs need operator
    /// sign-off (fixtures/assets policy or LFS), not a silent add.</summary>
    public const long MaxNewFileBytes = 1_000_000;

    private static readonly string[] JunkExtensions = { ".bak", ".orig", ".rej", ".tmp", ".swp", ".swo" };

    /// <summary>Violations for the added files (empty = clean). Pure
    /// function apart from the on-disk size probe.</summary>
    public static IReadOnlyList<string> Check(string worktreePath, IReadOnlyList<string> addedFiles)
    {
        var violations = new List<string>();
        foreach (var file in addedFiles)
        {
            var ext = Path.GetExtension(file);
            var junk = Array.Exists(JunkExtensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))
                || file.EndsWith("~", StringComparison.Ordinal);
            if (junk)
            {
                violations.Add(
                    $"{file}: junk artifact ('{(file.EndsWith("~", StringComparison.Ordinal) ? "~" : ext)}') — " +
                    "backup/reject/editor residue does not belong in version control. Delete the file and commit the removal.");
                continue;
            }
            try
            {
                var full = Path.Combine(worktreePath, file);
                if (File.Exists(full) && new FileInfo(full).Length > MaxNewFileBytes)
                {
                    violations.Add(
                        $"{file}: {new FileInfo(full).Length:N0} bytes — new files over {MaxNewFileBytes / 1_000_000} MB " +
                        "need operator sign-off (fixtures/assets policy or LFS). Remove it or shrink it.");
                }
            }
            catch
            {
                // Unreadable/deleted between diff and probe: not a
                // hygiene finding.
            }
        }
        return violations;
    }
}
