using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Forge.AgentTools;

/// <summary>Imports Git data, never repository configuration or executable integration, into a private process-local directory.</summary>
internal sealed class IsolatedGitMetadata : IDisposable
{
    private readonly string _sourceCommon, _sourceGit;
    private readonly string _root;
    private readonly Dictionary<string, byte[]> _initial = new(StringComparer.Ordinal);
    private readonly HashSet<string> _initialObjects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _importedWorktrees = new(StringComparer.Ordinal);
    private readonly string? _permittedWorktreeRoot;
    public string CommonDirectory { get; }
    public string GitDirectory { get; }
    public string Worktree { get; }
    private static readonly string[] DataFiles = ["HEAD", "index", "packed-refs", "shallow", "MERGE_HEAD", "MERGE_MSG", "ORIG_HEAD", "CHERRY_PICK_HEAD"];

    public IsolatedGitMetadata(string worktree, string? repositoryUrl = null, string? permittedWorktreeRoot = null, string? trustedRepositoryRoot = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Private controller Git requires qualified Linux filesystem isolation.");
        Worktree = Path.GetFullPath(worktree);
        _permittedWorktreeRoot = permittedWorktreeRoot is null ? null : Path.GetFullPath(permittedWorktreeRoot).TrimEnd('/');
        var marker = Path.Combine(Worktree, ".git");
        RejectLink(marker);
        _sourceGit = Directory.Exists(marker) ? marker : ResolveGitFile(marker);
        var commonFile = Path.Combine(_sourceGit, "commondir");
        _sourceCommon = File.Exists(commonFile) ? Path.GetFullPath(ReadText(commonFile).Trim(), _sourceGit) : _sourceGit;
        var repositoryRoot = Path.GetDirectoryName(_sourceCommon)!;
        if (Path.GetFileName(_sourceCommon) != ".git"
            || (trustedRepositoryRoot is not null && _sourceCommon != Path.Combine(Path.GetFullPath(trustedRepositoryRoot), ".git"))
            || (trustedRepositoryRoot is null && Worktree != repositoryRoot && !Worktree.StartsWith(repositoryRoot.TrimEnd('/') + "/", StringComparison.Ordinal)))
            throw new InvalidOperationException("Git metadata is outside the authorized repository root.");
        if (_sourceGit != _sourceCommon && Path.GetDirectoryName(_sourceGit) != Path.Combine(_sourceCommon, "worktrees"))
            throw new InvalidOperationException("Selected Git metadata is outside the common repository.");
        RejectLink(_sourceCommon);
        _root = Path.Combine("/tmp", "forge-private-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            CommonDirectory = Path.Combine(_root, "repo.git");
            Directory.CreateDirectory(CommonDirectory);
            CopyMetadata(_sourceCommon, CommonDirectory);
            var sourceWorktrees = Path.Combine(_sourceCommon, "worktrees");
            if (Directory.Exists(sourceWorktrees))
            {
                RejectLink(sourceWorktrees);
                foreach (var entry in Directory.EnumerateDirectories(sourceWorktrees))
                {
                    RejectLink(entry);
                    var markerFile = Path.Combine(entry, "gitdir");
                    if (!File.Exists(markerFile)) continue;
                    var target = ReadText(markerFile).Trim();
                    if (permittedWorktreeRoot is not null && !Path.GetFullPath(target).StartsWith(Path.GetFullPath(permittedWorktreeRoot).TrimEnd('/') + "/", StringComparison.Ordinal))
                        throw new InvalidOperationException("Worktree metadata points outside the authorized worktree root.");
                    _importedWorktrees.Add(Path.GetFileName(entry));
                    var imported = Path.Combine(CommonDirectory, "worktrees", Path.GetFileName(entry));
                    Directory.CreateDirectory(imported);
                    CopyFiles(entry, imported);
                    File.WriteAllText(Path.Combine(imported, "commondir"), "../..\n");
                    File.WriteAllText(Path.Combine(imported, "gitdir"), target + "\n");
                }
            }
            GitDirectory = _sourceGit == _sourceCommon ? CommonDirectory : Path.Combine(CommonDirectory, "worktrees", Path.GetFileName(_sourceGit));
            if (GitDirectory != CommonDirectory)
            {
                Directory.CreateDirectory(GitDirectory);
                CopyFiles(_sourceGit, GitDirectory);
                File.WriteAllText(Path.Combine(GitDirectory, "commondir"), "../..\n");
                File.WriteAllText(Path.Combine(GitDirectory, "gitdir"), marker + "\n");
            }
            var config = "[core]\nrepositoryformatversion = 0\nbare = false\nhooksPath = /dev/null\nfsmonitor = false\n[gc]\nauto = 0\n[user]\nname = Forge\nemail = forge@localhost\n[protocol]\nallow = never\n[protocol \"https\"]\nallow = always\n[http]\nfollowRedirects = false\n[fetch]\nrecurseSubmodules = false\n[push]\nrecurseSubmodules = no\n";
            if (repositoryUrl is not null)
            {
                ValidateRepository(repositoryUrl);
                config += "[remote \"origin\"]\nurl = " + repositoryUrl + "\nfetch = +refs/heads/*:refs/remotes/origin/*\n";
            }
            File.WriteAllText(Path.Combine(CommonDirectory, "config"), config);
            foreach (var file in MutableFiles())
                _initial[Path.GetRelativePath(CommonDirectory, file)] = Hash(file);
            var objects = Path.Combine(CommonDirectory, "objects");
            if (Directory.Exists(objects))
                foreach (var file in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories))
                    _initialObjects.Add(Path.GetRelativePath(CommonDirectory, file));

        }
        catch
        {
            Directory.Delete(_root, recursive: true);
            throw;
        }
    }

    /// <summary>Retire one joined review worker's checkout without loading shared Git configuration.
    /// Git's worktree remove cannot operate on copied metadata: its backlink correctly points
    /// at the original registry. Validate that exact registration instead; never prune siblings.</summary>
    internal static void RemoveDetachedReviewSnapshot(string repositoryRoot, string worktreeRoot, string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Private controller Git requires Linux isolation.");
        repositoryRoot = Path.GetFullPath(repositoryRoot).TrimEnd('/');
        worktreeRoot = Path.GetFullPath(worktreeRoot).TrimEnd('/');
        path = Path.GetFullPath(path);
        var name = Path.GetFileName(path);
        if (Path.GetDirectoryName(path) != worktreeRoot || !Regex.IsMatch(name, "\\Areview-[0-9a-f]{64}\\z")
            || !worktreeRoot.StartsWith(repositoryRoot + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("Review cleanup is outside the authorized repository.");
        var common = Path.Combine(repositoryRoot, ".git");
        var registration = Path.Combine(common, "worktrees", name);
        static void RejectLinkedAncestors(string candidate)
        {
            for (var current = candidate; current is not null; current = Path.GetDirectoryName(current))
                if (Path.Exists(current)) RejectLink(current);
        }
        RejectLinkedAncestors(path);
        RejectLinkedAncestors(registration);
        var checkoutExists = Directory.Exists(path);
        if (!Directory.Exists(registration))
        {
            if (checkoutExists) throw new InvalidOperationException("Review checkout has no matching registration.");
            return;
        }
        if (File.Exists(Path.Combine(registration, "locked"))
            || Path.GetFullPath(ReadText(Path.Combine(registration, "gitdir")).Trim()) != Path.Combine(path, ".git")
            || Path.GetFullPath(ReadText(Path.Combine(registration, "commondir")).Trim(), registration) != common
            || !Regex.IsMatch(ReadText(Path.Combine(registration, "HEAD")).Trim(), "\\A[0-9a-f]{40}\\z"))
            throw new InvalidOperationException("Review cleanup requires an unlocked, detached, exactly registered snapshot.");
        if (checkoutExists)
        {
            if (ResolveGitFile(Path.Combine(path, ".git")) != registration)
                throw new InvalidOperationException("Review checkout points to another registration.");
            // Directory.Delete unlinks contained symlinks; it never traverses their targets.
            Directory.Delete(path, recursive: true);
        }
        // An interruption after checkout removal leaves the validated registration for this
        // same operation to retire on retry. No branch refs or sibling metadata are touched.
        Directory.Delete(registration, recursive: true);
    }

    public static void ValidateRepository(string url)
    {
        if (!Regex.IsMatch(url, @"\Ahttps://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("An explicit canonical GitHub HTTPS repository is required.");
    }

    public void Configure(ProcessStartInfo start)
    {
        SanitizeEnvironment(start, _root);
        start.Environment["GIT_DIR"] = GitDirectory;
        start.Environment["GIT_WORK_TREE"] = Worktree;
    }

    internal static void SanitizeEnvironment(ProcessStartInfo start, string root)
    {
        start.FileName = "/usr/bin/git";
        start.WorkingDirectory = root;
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/bin:/bin";
        start.Environment["HOME"] = root;
        start.Environment["LANG"] = "C.UTF-8";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
    }

    // Export only actual changes. A read-only command never rewrites shared metadata.
    public void PublishData()
    {
        var current = MutableFiles().ToDictionary(f => Path.GetRelativePath(CommonDirectory, f), Hash, StringComparer.Ordinal);
        var newWorktrees = Directory.Exists(Path.Combine(CommonDirectory, "worktrees"))
            ? Directory.EnumerateDirectories(Path.Combine(CommonDirectory, "worktrees"))
                .Where(d => !_importedWorktrees.Contains(Path.GetFileName(d))).ToArray() : [];
        // Validate every potential marker destination before any export. Imported gitdir values are never output destinations.
        var markers = new List<(string Marker, string Destination, string PrivateDirectory)>();
        foreach (var directory in newWorktrees)
        {
            if (_permittedWorktreeRoot is null)
                throw new InvalidOperationException("Creating a worktree requires an explicit authorized output root.");
            var marker = Path.GetFullPath(ReadText(Path.Combine(directory, "gitdir")).Trim());
            if (Path.GetFileName(marker) != ".git"
                || Path.GetDirectoryName(Path.GetDirectoryName(marker)) != _permittedWorktreeRoot)
                throw new InvalidOperationException("Generated worktree is outside its authorized output root.");
            markers.Add((marker, Path.Combine(_sourceCommon, "worktrees", Path.GetFileName(directory)), directory));
        }
        // New immutable objects must exist before a ref/index that refers to them is made visible.
        var objects = Path.Combine(CommonDirectory, "objects");
        if (Directory.Exists(objects))
            foreach (var file in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(CommonDirectory, file);
                if (_initialObjects.Contains(relative)) continue;
                if (!Regex.IsMatch(relative, @"\Aobjects/(?:[0-9a-f]{2}/[0-9a-f]{38}|pack/pack-[0-9a-f]{40}\.(?:pack|idx|rev|bitmap))\z")) continue;
                var destination = Path.Combine(_sourceCommon, relative);
                if (!File.Exists(destination)) AtomicGitData.Update(destination, null, file, immutable: true);
            }
        var mutations = new List<AtomicGitData.Change>();
        foreach (var relative in _initial.Keys.Union(current.Keys))
        {
            _initial.TryGetValue(relative, out var before);
            current.TryGetValue(relative, out var after);
            if (before is not null && after is not null && before.SequenceEqual(after)) continue;
            if (before is not null && after is not null && Path.GetFileName(relative) is "gitdir" or "commondir")
                throw new InvalidOperationException("Imported worktree structural pointers cannot be rewritten.");
            mutations.Add(new(Path.Combine(_sourceCommon, relative), before,
                after is null ? null : Path.Combine(CommonDirectory, relative)));
        }
        foreach (var (marker, destination, privateDirectory) in markers)
        {
            mutations.Add(new(Path.Combine(destination, "commondir"), null, Path.Combine(privateDirectory, "commondir")));
            mutations.Add(new(Path.Combine(destination, "gitdir"), null, Path.Combine(privateDirectory, "gitdir")));
            var pointer = Path.Combine(_root, "worktree-pointer-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(pointer, "gitdir: " + destination + "\n");
            var expected = Encoding.UTF8.GetBytes("gitdir: " + privateDirectory + "\n");
            mutations.Add(new(marker, System.Security.Cryptography.SHA256.HashData(expected), pointer));
        }
        AtomicGitData.UpdateMany(mutations);
    }

    private IEnumerable<string> MutableFiles()
    {
        foreach (var name in DataFiles)
            if (File.Exists(Path.Combine(CommonDirectory, name))) yield return Path.Combine(CommonDirectory, name);
        var refs = Path.Combine(CommonDirectory, "refs");
        if (Directory.Exists(refs))
            foreach (var file in Directory.EnumerateFiles(refs, "*", SearchOption.AllDirectories)) yield return file;
        var worktrees = Path.Combine(CommonDirectory, "worktrees");
        if (Directory.Exists(worktrees))
            foreach (var directory in Directory.EnumerateDirectories(worktrees))
                foreach (var name in _importedWorktrees.Contains(Path.GetFileName(directory)) ? DataFiles.Concat(["commondir", "gitdir"]) : DataFiles)
                    if (File.Exists(Path.Combine(directory, name))) yield return Path.Combine(directory, name);
    }

    private static byte[] Hash(string path)
    {
        using var stream = Open(path, false);
        return System.Security.Cryptography.SHA256.HashData(stream);
    }

    private static string ResolveGitFile(string marker)
    {
        var text = ReadText(marker).Trim();
        if (!text.StartsWith("gitdir: ", StringComparison.Ordinal)) throw new InvalidOperationException("Worktree Git metadata is unavailable.");
        return Path.GetFullPath(text[8..], Path.GetDirectoryName(marker)!);
    }
    private static void CopyMetadata(string source, string destination)
    {
        CopyFiles(source, destination);
        foreach (var name in new[] { "objects", "refs" })
        {
            var directory = Path.Combine(source, name);
            if (Directory.Exists(directory)) CopyTree(directory, Path.Combine(destination, name), name);
        }
    }
    private static void CopyFiles(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var name in DataFiles)
        {
            var path = Path.Combine(source, name);
            if (File.Exists(path)) Copy(path, Path.Combine(destination, name));
        }
    }
    private static void CopyTree(string source, string destination, string relative)
    {
        RejectLink(source);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (relative.StartsWith("objects", StringComparison.Ordinal)
                && !Regex.IsMatch(relative + "/" + name, @"\Aobjects/(?:[0-9a-f]{2}/[0-9a-f]{38}|pack/pack-[0-9a-f]{40}\.(?:pack|idx|rev|bitmap))\z")) continue;
            var target = Path.Combine(destination, name);
            // Git objects are immutable and commonly read-only. Never overwrite an existing object.
            if (relative.StartsWith("objects", StringComparison.Ordinal) && File.Exists(target)) continue;
            Copy(file, target);
        }
        foreach (var child in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(child);
            if (relative == "objects" && name != "pack" && !Regex.IsMatch(name, "^[0-9a-f]{2}$")) continue;
            if (relative == "refs" && name == "replace") continue;
            CopyTree(child, Path.Combine(destination, name), relative + "/" + name);
        }
    }
    private static void Copy(string source, string destination)
    {
        using var input = Open(source, false);
        if (!input.CanSeek || input.Length > 512L * 1024 * 1024) throw new InvalidOperationException("Unsupported Git data file.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var output = Open(destination, true);
        input.CopyTo(output);
    }
    private static string ReadText(string path)
    {
        using var input = Open(path, false);
        if (!input.CanSeek || input.Length > 4096) throw new InvalidOperationException("Unsupported Git metadata file.");
        using var reader = new StreamReader(input);
        return reader.ReadToEnd();
    }
    private static void WriteText(string path, string text)
    {
        using var output = Open(path, true);
        var bytes = Encoding.UTF8.GetBytes(text); output.Write(bytes);
    }
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Git metadata links are not accepted.");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow { public ulong Flags, Mode, Resolve; }
    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long number, int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, ref OpenHow how, nuint size);
    private static FileStream Open(string path, bool write)
    {
        // Linux openat2 resolves every component atomically without following links.
        var how = new OpenHow { Flags = write ? 0x80000UL | 1UL | 0x40UL | 0x200UL : 0x80000UL | 0x800UL, Mode = write ? 0x180UL : 0, Resolve = 0x04 };
        var fd = syscall(437, -100, path, ref how, (nuint)Marshal.SizeOf<OpenHow>());
        if (fd < 0) throw new IOException("Git data path could not be opened safely.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return new FileStream(new SafeFileHandle((IntPtr)fd, true), write ? FileAccess.Write : FileAccess.Read);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
