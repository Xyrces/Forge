using System.Text;
using Forge.Core;
using Forge.Projects;

namespace Forge.Dashboard;

internal sealed record ProjectVisionFile(string? ProjectId, string Path, bool Primary);
internal sealed class VisionRevisionConflict() : Exception("The vision changed since this draft began. Refresh it before applying changes.");
internal sealed class VisionPathError(string message) : Exception(message);
internal sealed class VisionMemoryPending(Exception cause) : Exception(
    "The vision was saved. Refresh the vision to retry synchronizing agent guidance.", cause);

/// <summary>Registered projects import repository Markdown until their first edit; edited visions
/// live in the owning database so normal planning can keep refreshing a clean checkout.</summary>
internal sealed class ProjectVisionDocuments(VisionStore primary, ProjectContextFactory? projects, MemoryStore? memory)
{
    private readonly SemaphoreSlim _writer = new(1, 1);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal ProjectVisionFile? Find(string? id)
    {
        var primaryRoot = Path.TrimEndingDirectorySeparator(primary.WorkspaceRoot);
        var list = projects?.KnownProjects;
        var project = id is null
            ? list?.FirstOrDefault(p => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Root)), primaryRoot, PathComparison))
            : list?.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (project is null && (id is not null || projects is not null)) return null;
        var root = project is null ? primaryRoot : Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.Root));
        var isPrimary = string.Equals(root, primaryRoot, PathComparison);
        var path = isPrimary ? Path.GetFullPath(primary.AbsolutePath)
            : Path.GetFullPath(Path.Combine(root, Path.GetRelativePath(primaryRoot, Path.GetFullPath(primary.AbsolutePath))));
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            if (!isPrimary || !Path.IsPathRooted(primary.RelativePath))
                throw new VisionPathError("The configured vision file is outside this project's checkout.");
        }
        else
        {
            // Repository-controlled symlinks must never redirect a project read, write or model prompt.
            var current = root;
            foreach (var part in relative.Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, part);
                if (new FileInfo(current).LinkTarget is not null)
                    throw new VisionPathError("The configured vision path contains a symbolic link.");
            }
        }
        return new(project?.Id, path, isPrimary);
    }

    internal async Task<VisionSnapshot> ReadAsync(ProjectVisionFile file, CancellationToken ct = default)
    {
        if (file.ProjectId is { } id)
        {
            var saved = await Owner(file).GetAsync(VisionMemory.DocumentKey(id), ct);
            if (saved is not null) return new(true, file.Path, saved.Body, saved.CreatedAt);
        }
        return ReadRepository(file);
    }

    private MemoryStore Owner(ProjectVisionFile file)
    {
        var project = projects?.Find(file.ProjectId!)
            ?? throw new InvalidOperationException("The vision's project is no longer registered.");
        return new MemoryStore(((IssueStore)project.Issues).Db);
    }

    internal static VisionSnapshot ReadRepository(ProjectVisionFile file)
    {
        try
        {
            var source = File.ReadAllText(file.Path);
            return new(true, file.Path, source, File.GetLastWriteTimeUtc(file.Path));
        }
        catch (FileNotFoundException) { return new(false, file.Path, "", null); }
        catch (DirectoryNotFoundException) { return new(false, file.Path, "", null); }
    }

    internal async Task<VisionSnapshot> SaveAsync(ProjectVisionFile file, string? revision, Func<string, string> edit, CancellationToken ct)
    {
        await _writer.WaitAsync(ct);
        try
        {
            var snapshot = await ReadAsync(file, ct);
            if (revision is not null && !string.Equals(VisionMap.Parse(snapshot.Content).Revision, revision, StringComparison.Ordinal))
                throw new VisionRevisionConflict();
            var updated = edit(snapshot.Content);
            if (file.ProjectId is { } id)
            {
                if (!string.Equals(updated, snapshot.Content, StringComparison.Ordinal))
                {
                    var current = await ReadAsync(file, ct);
                    if (current.Exists != snapshot.Exists || current.Content != snapshot.Content)
                        throw new VisionRevisionConflict();
                    await Owner(file).RememberAsync(VisionMemory.DocumentKey(id), updated, ct: ct);
                }
            }
            else if (!snapshot.Exists || !string.Equals(updated, snapshot.Content, StringComparison.Ordinal))
            {
                var directory = Path.GetDirectoryName(file.Path)!;
                Directory.CreateDirectory(directory);
                var temporary = Path.Combine(directory, $".forge-vision-{Guid.NewGuid():N}.tmp");
                try
                {
                    Encoding encoding = new UTF8Encoding(false);
                    if (snapshot.Exists)
                    {
                        using var reader = new StreamReader(file.Path, encoding, detectEncodingFromByteOrderMarks: true);
                        reader.Peek();
                        encoding = reader.CurrentEncoding;
                    }
                    await File.WriteAllTextAsync(temporary, updated, encoding, ct);
                    if (snapshot.Exists && !OperatingSystem.IsWindows())
                        File.SetUnixFileMode(temporary, File.GetUnixFileMode(file.Path));
                    var current = ReadRepository(file);
                    if (current.Exists != snapshot.Exists || current.Content != snapshot.Content)
                        throw new VisionRevisionConflict();
                    File.Move(temporary, file.Path, overwrite: true);
                }
                finally { File.Delete(temporary); }
            }
            var saved = await ReadAsync(file, CancellationToken.None);
            if (file.Primary && file.ProjectId is null) primary.Reload();
            // Once persisted, a disconnected browser must not cancel memory synchronization.
            try { await SyncAsync(file, saved.Content, CancellationToken.None); }
            catch (Exception ex) { throw new VisionMemoryPending(ex); }
            return saved;
        }
        finally { _writer.Release(); }
    }

    internal async Task<VisionSnapshot> RefreshAsync(ProjectVisionFile file, CancellationToken ct)
    {
        await _writer.WaitAsync(ct);
        try
        {
            var snapshot = await ReadAsync(file, ct);
            if (file.Primary && file.ProjectId is null) primary.Reload();
            // A removed document withdraws its old guidance from this project's agents.
            await SyncAsync(file, snapshot.Content, ct);
            return snapshot;
        }
        finally { _writer.Release(); }
    }

    private async Task SyncAsync(ProjectVisionFile file, string source, CancellationToken ct)
    {
        if (file.ProjectId is { } id)
        {
            // K3s workers use the owning project's schema. The planning agents use the shared
            // keyed project memory. In production these are the same store for the primary project.
            var owner = Owner(file);
            await owner.RememberAsync($"vision/{id}", source, ct: ct);
            if (memory is not null && (owner.Db.Qualifier != memory.Db.Qualifier || owner.ConnectionString != memory.ConnectionString))
                await memory.RememberAsync($"vision/{id}", source, ct: ct);
        }
        if (file.Primary && memory is not null) await memory.RememberAsync("vision/master", source, ct: ct);
    }
}
