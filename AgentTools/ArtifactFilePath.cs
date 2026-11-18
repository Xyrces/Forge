namespace Forge.AgentTools;

/// <summary>Resolve an artist output to the file visible on the shared worker volume.</summary>
internal static class ArtifactFilePath
{
    public static string? Resolve(string root, string specId, string body)
    {
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(specId)
            || specId is "." or ".." || specId.IndexOfAny(['/', '\\', ':']) >= 0)
            return null;

        // Artist downloads are rooted beneath their spec. Treat either platform's
        // separators as separators so legacy values cannot bypass this boundary.
        var relative = body.Replace('\\', '/');
        if (relative.Split('/').Any(part => part is "." or "..")
            || (!Path.IsPathFullyQualified(body) && relative.Contains(':')))
            return null;
        var declaredRoot = Path.GetFullPath(root);
        var specRoot = Path.GetFullPath(Path.Combine(declaredRoot, specId));
        var path = Path.GetFullPath(Path.IsPathRooted(body) ? body : Path.Combine(declaredRoot, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(specRoot + Path.DirectorySeparatorChar, comparison))
            return null;

        // The configured storage root may use an operator-managed mount/symlink.
        // Canonicalize that trusted root, then reject links within its spec subtree.
        var actualRoot = Path.GetPathRoot(declaredRoot)!;
        foreach (var component in declaredRoot[actualRoot.Length..].Split(Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(actualRoot, component));
            actualRoot = directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName;
        }
        path = Path.Combine(actualRoot, Path.GetRelativePath(declaredRoot, path));
        for (FileSystemInfo? part = new FileInfo(path); part is not null && part.FullName != actualRoot;
            part = part is FileInfo file ? file.Directory : ((DirectoryInfo)part).Parent)
        {
            if (part.LinkTarget is not null) return null;
        }
        // Artist outputs contain bytes. Empty entries also include Unix FIFOs and
        // sockets, which must be rejected before opening can block a worker.
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return null;
        using var readable = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return path;
    }
}
