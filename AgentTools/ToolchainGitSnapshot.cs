using System.Diagnostics;

namespace Forge.AgentTools;

/// <summary>Runs before the untrusted image starts. Exports data for this task's HEAD only,
/// never original config, hooks, remotes, credentials, or sibling worktree metadata.</summary>
public static class ToolchainGitSnapshot
{
    public static async Task PrepareAsync(string worktree, string destination, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidOperationException("Toolchain Git destination must be a new empty volume.");
        using var isolated = new IsolatedGitMetadata(worktree);
        var bundle = Path.Combine(Path.GetTempPath(), "forge-toolchain-" + Guid.NewGuid().ToString("N") + ".bundle");
        var repo = Path.Combine(destination, "repo");
        try
        {
            await GitAsync(["bundle", "create", bundle, "HEAD"], worktree, isolated, ct);
            Directory.CreateDirectory(repo);
            await GitAsync(["init", "--bare", repo], destination, null, ct);
            await GitAsync(["--git-dir", repo, "fetch", "--no-tags", bundle, "HEAD"], destination, null, ct);
            var head = (await GitAsync(["--git-dir", repo, "rev-parse", "FETCH_HEAD"], destination, null, ct)).Trim();
            await GitAsync(["--git-dir", repo, "update-ref", "HEAD", head], destination, null, ct);
            await GitAsync(["--git-dir", repo, "read-tree", "HEAD"], destination, null, ct);
            await File.WriteAllTextAsync(Path.Combine(repo, "config"),
                "[core]\nrepositoryformatversion = 0\nbare = false\nhooksPath = /dev/null\nfsmonitor = false\n[user]\nname = Forge\nemail = forge@example.invalid\n[gc]\nauto = 0\n", ct);
            await File.WriteAllTextAsync(Path.Combine(destination, "marker"), "gitdir: /run/forge-toolchain-git/repo\n", ct);
        }
        finally { File.Delete(bundle); }
    }

    private static async Task<string> GitAsync(string[] arguments, string cwd, IsolatedGitMetadata? isolated, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        if (isolated is not null) isolated.Configure(start);
        else
        {
            start.Environment.Clear();
            start.Environment["PATH"] = "/usr/bin:/bin";
            start.Environment["HOME"] = "/tmp";
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not prepare task Git metadata.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException("Task Git snapshot failed: " + await stderr);
        return await stdout;
    }
}
