using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Forge.AgentTools;

/// <summary>Git-compatible lock/CAS publication. Directory descriptors pin output locations; readers see whole files.</summary>
internal static class AtomicGitData
{
    internal sealed record Change(string Destination, byte[]? ExpectedHash, string? Source, bool Immutable = false);

    public static void Update(string destination, byte[]? expectedHash, string? source, bool immutable = false)
        => UpdateMany([new(destination, expectedHash, source, immutable)]);

    public static void UpdateMany(IEnumerable<Change> changes)
    {
        var staged = new List<Pending>();
        try
        {
            // Acquire the complete write set in a consistent order, then validate every CAS
            // before making any mutable file visible. A late conflict publishes nothing.
            foreach (var change in changes.OrderBy(c => c.Destination, StringComparer.Ordinal))
                staged.Add(new Pending(change));
            foreach (var pending in staged) pending.Validate();
            foreach (var pending in staged) pending.Stage();
            foreach (var pending in staged) pending.Publish();
        }
        finally { foreach (var pending in staged) pending.Dispose(); }
    }

    private sealed class Pending : IDisposable
    {
        private readonly Change _change;
        private readonly SafeFileHandle _directory;
        private readonly FileStream _write;
        private readonly string _name, _lockName;
        private readonly int _fd;
        private bool _renamed, _skip;
        public Pending(Change change)
        {
            _change = change;
            _directory = OpenDirectory(Path.GetDirectoryName(change.Destination)!);
            _fd = _directory.DangerousGetHandle().ToInt32();
            _name = Path.GetFileName(change.Destination); _lockName = _name + ".lock";
            try { _write = OpenAt(_fd, _lockName, write: true, optional: false)!; }
            catch { _directory.Dispose(); throw; }
        }
        public void Validate()
        {
            using var existing = OpenAt(_fd, _name, write: false, optional: true);
            if (_change.Immutable && existing is not null) { _skip = true; return; }
            var observed = existing is null ? null : SHA256.HashData(existing);
            if (_change.ExpectedHash is null ? observed is not null : observed is null || !_change.ExpectedHash.SequenceEqual(observed))
                throw new InvalidOperationException("Shared Git data changed during the controller operation; retry from fresh state.");
        }
        public void Stage()
        {
            if (_skip || _change.Source is null) return;
            using (var input = OpenAbsolute(_change.Source)) input.CopyTo(_write);
            _write.Flush(flushToDisk: true);
        }
        public void Publish()
        {
            if (_skip) return;
            if (_change.Source is null)
            {
                if (unlinkat(_fd, _name, 0) != 0 && Marshal.GetLastPInvokeError() != 2)
                    throw Error("Could not remove obsolete Git data.");
                return;
            }
            if (renameat(_fd, _lockName, _fd, _name) != 0) throw Error("Could not publish Git data atomically.");
            _renamed = true;
        }
        public void Dispose()
        {
            _write.Dispose();
            try
            {
                if (!_renamed && unlinkat(_fd, _lockName, 0) != 0 && Marshal.GetLastPInvokeError() != 2)
                    throw Error("Could not release Git data lock.");
            }
            finally { _directory.Dispose(); }
        }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var how = new OpenHow { Flags = 0x80000 | 0x10000, Resolve = 0x04 };
        var full = Path.GetFullPath(path);
        using var parent = full == "/" ? null : OpenDirectory(Path.GetDirectoryName(full)!);
        var parentFd = parent?.DangerousGetHandle().ToInt32() ?? -100;
        var component = full == "/" ? "/" : Path.GetFileName(full);
        var fd = syscall(437, parentFd, component, ref how, (nuint)Marshal.SizeOf<OpenHow>());
        if (fd < 0 && Marshal.GetLastPInvokeError() == 2)
        {
            if (mkdirat(parentFd, component, 0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17)
                throw Error("Git output directory cannot be created safely.");
            fd = syscall(437, parentFd, component, ref how, (nuint)Marshal.SizeOf<OpenHow>());
        }
        if (fd < 0) throw Error("Git output directory cannot be opened safely.");
        return new SafeFileHandle((IntPtr)fd, true);
    }

    private static FileStream OpenAbsolute(string path) => OpenAt(-100, path, false, false)!;
    private static FileStream? OpenAt(int dirfd, string path, bool write, bool optional)
    {
        var how = new OpenHow { Flags = write ? 0x80000UL | 1UL | 0x40UL | 0x80UL : 0x80000UL | 0x800UL, Mode = write ? 0x180UL : 0, Resolve = 0x04 };
        var fd = syscall(437, dirfd, path, ref how, (nuint)Marshal.SizeOf<OpenHow>());
        if (fd < 0)
        {
            if (optional && Marshal.GetLastPInvokeError() == 2) return null;
            throw Error("Git data is locked or cannot be opened safely.");
        }
        return new FileStream(new SafeFileHandle((IntPtr)fd, true), write ? FileAccess.Write : FileAccess.Read);
    }
    private static IOException Error(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));
    [StructLayout(LayoutKind.Sequential)] private struct OpenHow { public ulong Flags, Mode, Resolve; }
    [DllImport("libc", SetLastError = true)] private static extern long syscall(long number, int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, ref OpenHow how, nuint size);
    [DllImport("libc", SetLastError = true)] private static extern int unlinkat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int renameat(int olddirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldpath, int newdirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string newpath);
    [DllImport("libc", SetLastError = true)] private static extern int mkdirat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
