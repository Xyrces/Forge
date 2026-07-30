using Forge.Core;
using System.Reflection;
using System.Runtime.CompilerServices;
using Forge.Core.Db;
using Microsoft.Data.Sqlite;

namespace Forge.Tests;

/// <summary>
/// Central helper for tests that create SQLite databases or temp directories.
/// It disables SQLite connection pooling for the created databases so that the
/// files can be deleted deterministically, and it cleans up the WAL/SHM sidecar
/// files as well. Without this, the default connection pool keeps handles open
/// and tests leak thousands of .db/.db-wal/.db-shm files under /tmp, which
/// eventually exhausts disk quota / inodes and causes environmental failures.
///
/// The helper also moves all temp directories and databases out of /tmp and into
/// a worktree-local root so the pre-push test run is not limited by the global
/// temporary filesystem. It exposes a static <see cref="TempRoot" /> instance
/// that is initialised once per test process and cleaned up when the test
/// assembly unloads.
/// </summary>
public sealed class TempRoot : IDisposable
{
    private static readonly Lazy<TempRoot> _instance = new(() => new TempRoot(), isThreadSafe: true);

    public static TempRoot Instance => _instance.Value;

    private readonly List<string> _dbPaths = new();
    private readonly List<string> _roots = new();
    private readonly string _root;

    private TempRoot()
    {
        // Put the temp root under the worktree so it is not constrained by the
        // global /tmp filesystem. Use the process id plus a short random suffix
        // to avoid collisions between concurrent test runs.
        var worktree = GetWorktreeRoot();
        _root = Path.Combine(worktree, ".test-temp", $"run-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        // Point process-wide temp variables at our root as well. Some library
        // code still calls Path.GetTempPath(), and on Unix that reads TMPDIR.
        // We only change them if they have not already been redirected to a
        // path under the worktree.
        RedirectEnvironmentTemp(_root);
    }

    public string Root => _root;

    /// <summary>
    /// Returns a new empty directory under the worktree-local temp root.
    /// It is deleted when this <see cref="TempRoot"/> is disposed.
    /// </summary>
    public string NewDirectory(string prefix)
    {
        var path = Path.Combine(_root, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        lock (_roots) { _roots.Add(path); }
        return path;
    }

    /// <summary>
    /// Returns a SQLite database file path under the worktree-local temp root.
    /// The file and its WAL/SHM siblings are deleted when this
    /// <see cref="TempRoot"/> is disposed.
    /// </summary>
    public string NewDbPath(string prefix)
    {
        var path = Path.Combine(_root, $"{prefix}-{Guid.NewGuid():N}.db");
        lock (_dbPaths) { _dbPaths.Add(path); }
        return path;
    }

    /// <summary>
    /// Build a SQLite connection string that disables pooling for the given DB
    /// path so that deleting the database file is deterministic.
    /// </summary>
    public static string BuildConnectionString(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = false,
        }.ToString();
    }

    /// <summary>
    /// Build an <see cref="IssueStore"/> against a non-pooled SQLite file and
    /// register the path for cleanup.
    /// </summary>
    public IssueStore NewIssueStore(string prefix)
    {
        var path = NewDbPath(prefix);
        return new IssueStore(ForgeDb.Sqlite(BuildConnectionString(path)));
    }

    /// <summary>
    /// Recursively delete all registered directories and DB files, clearing
    /// SQLite connection pools first. This is safe to call from an assembly
    /// unload hook.
    /// </summary>
    public void Dispose()
    {
        string[] dbs;
        lock (_dbPaths) { dbs = _dbPaths.ToArray(); }

        foreach (var path in dbs)
        {
            DeleteDb(path);
        }

        string[] roots;
        lock (_roots) { roots = _roots.ToArray(); }

        foreach (var root in roots)
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        // Avoid deleting our own root while the process still uses it as the
        // current working directory; falling back to the worktree root keeps
        // spawned git/IO children valid.
        try
        {
            var cwd = Directory.GetCurrentDirectory();
            if (!string.IsNullOrEmpty(cwd) &&
                (cwd.Equals(_root, StringComparison.OrdinalIgnoreCase) ||
                 cwd.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                Directory.SetCurrentDirectory(GetWorktreeRoot());
            }
        }
        catch { }

        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Deletes a SQLite database file after clearing any pooled handles,
    /// including WAL/SHM sidecar files.
    /// </summary>
    public static void DeleteDb(string path)
    {
        try
        {
            using var conn = new SqliteConnection(BuildConnectionString(path));
            conn.Open();
            SqliteConnection.ClearPool(conn);
        }
        catch { }

        TryDelete(path);
        TryDelete(path + "-wal");
        TryDelete(path + "-shm");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static string GetWorktreeRoot()
    {
        // Prefer the repository worktree root. If we cannot determine it, fall
        // back to the current directory, then to the user's local app data, and
        // finally to the system temp path as a last resort.
        try
        {
            var assembly = Assembly.GetExecutingAssembly().Location;
            var dir = new FileInfo(assembly).Directory;
            for (var i = 0; dir != null && i < 10; i++)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                    File.Exists(Path.Combine(dir.FullName, "Forge.sln")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }
        catch { }

        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
                return local;
        }
        catch { }

        return Path.GetTempPath();
    }

    private static void RedirectEnvironmentTemp(string root)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Environment.SetEnvironmentVariable("TEMP", ChooseTempPath(Environment.GetEnvironmentVariable("TEMP"), root));
                Environment.SetEnvironmentVariable("TMP", ChooseTempPath(Environment.GetEnvironmentVariable("TMP"), root));
            }
            else
            {
                Environment.SetEnvironmentVariable("TMPDIR", ChooseTempPath(Environment.GetEnvironmentVariable("TMPDIR"), root));
            }
        }
        catch { }
    }

    private static string ChooseTempPath(string? current, string root)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return root;
        }

        // If TMPDIR already points inside the worktree, leave it alone.
        try
        {
            var currentFull = Path.GetFullPath(current);
            var worktreeRoot = GetWorktreeRoot();
            if (currentFull.StartsWith(worktreeRoot, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }
        }
        catch { }

        return root;
    }
}

/// <summary>
/// Lightweight per-class fixture that wraps <see cref="TempRoot.Instance"/>.
/// Most tests only need to create a DB or directory; this class handles cleanup.
/// </summary>
public sealed class TempDbFixture : IDisposable
{
    private readonly List<string> _dbPaths = new();
    private readonly List<string> _roots = new();

    public string NewDbPath(string prefix)
    {
        var path = TempRoot.Instance.NewDbPath(prefix);
        lock (_dbPaths) { _dbPaths.Add(path); }
        return path;
    }

    public string NewDirectory(string prefix)
    {
        var path = TempRoot.Instance.NewDirectory(prefix);
        lock (_roots) { _roots.Add(path); }
        return path;
    }

    public IssueStore NewIssueStore(string prefix) => TempRoot.Instance.NewIssueStore(prefix);

    public void Dispose()
    {
        foreach (var path in _dbPaths.ToArray())
        {
            TempRoot.DeleteDb(path);
        }

        foreach (var root in _roots.ToArray())
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}

public static class TestModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        // Force the static TempRoot singleton to create its directory and
        // redirect TMPDIR before any test constructs temp paths.
        _ = TempRoot.Instance.Root;

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { TempRoot.Instance.Dispose(); } catch { }
        };
    }
}
