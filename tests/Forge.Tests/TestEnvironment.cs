using System.Runtime.CompilerServices;

namespace Forge.Tests;

/// <summary>
/// Test-assembly-level environment setup. The Linux container/VM used
/// for verification has a low per-user inotify instance limit (128).
/// Many integration tests spin up a <see cref="WebApplication"/> host
/// in parallel; the default configuration provider adds a
/// FileSystemWatcher for appsettings.json, and once ~128 are alive the
/// next host creation throws an IOException.
///
/// Setting DOTNET_USE_POLLING_FILE_WATCHER=1 before any host is built
/// switches the ASP.NET Core file-watching implementation to polling,
/// eliminating the inotify-instance exhaustion without changing the
/// behavior the tests assert.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1");
    }
}
