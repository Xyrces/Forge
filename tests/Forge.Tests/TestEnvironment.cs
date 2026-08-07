using System.Runtime.CompilerServices;

namespace Forge.Tests;

/// <summary>
/// The suite spins up ~30 Kestrel hosts in parallel (endpoint test
/// classes). Each host's PhysicalFileProvider defaults to inotify
/// on Linux; ~128 instances per user is the kernel cap and parallel
/// full-suite runs exhausted it (FileSystemWatcher.StartRaisingEvents
/// IOException, 1ms fixture failures). Polling watchers use a timer
/// instead — immune to the cap.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void UsePollingFileWatchers()
        => Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1");
}
