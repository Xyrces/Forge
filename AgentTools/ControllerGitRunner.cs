using System.Diagnostics;
using System.Text;

namespace Forge.AgentTools;

/// <summary>Git processes with App credentials use only private configuration and an explicit repository URL.</summary>
internal static class ControllerGitRunner
{
    internal sealed record Result(int ExitCode, string Stdout, string Stderr);

    public static async Task<Result> RunAsync(string cwd, string[] arguments,
        GitHubAppIdentities.Repository? repository, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Controller App Git requires Linux isolation.");
        var network = arguments.FirstOrDefault() is "clone" or "fetch" or "pull" or "ls-remote";
        var clone = arguments.FirstOrDefault() == "clone";
        if (network && repository is null)
            throw new InvalidOperationException("App-enabled controller Git needs a configured repository scope.");
        var url = repository is null ? null : $"https://github.com/{repository.Owner}/{repository.Name}";
        if (url is not null) IsolatedGitMetadata.ValidateRepository(url);
        var privateRoot = Path.Combine("/tmp", "forge-controller-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using var metadata = !clone && (Directory.Exists(Path.Combine(cwd, ".git")) || File.Exists(Path.Combine(cwd, ".git")))
                ? new IsolatedGitMetadata(cwd, url) : null;
            var start = NewProcess();
            if (metadata is null) IsolatedGitMetadata.SanitizeEnvironment(start, privateRoot);
            else metadata.Configure(start);
            var effective = arguments.Select(a => network && (a == "origin" || a.StartsWith("https://github.com/", StringComparison.Ordinal)) ? url! : a).ToArray();
            if (clone) effective[^1] = Path.Combine(privateRoot, "clone");
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.hooksPath=/dev/null");
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("http.followRedirects=false");
            for (var i = 0; i < effective.Length; i++)
            {
                start.ArgumentList.Add(effective[i]);
                if (clone && i == 0) start.ArgumentList.Add("--no-checkout");
            }
            string? authorization = null;
            if (network)
            {
                var token = await GitHubAppIdentities.CredentialsFor(repository!, reviewer: false).GetTokenAsync().WaitAsync(ct);
                authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));
                start.Environment["GIT_CONFIG_COUNT"] = "2";
                start.Environment["GIT_CONFIG_KEY_0"] = "credential.helper";
                start.Environment["GIT_CONFIG_VALUE_0"] = "";
                start.Environment["GIT_CONFIG_KEY_1"] = "http." + url + ".extraheader";
                start.Environment["GIT_CONFIG_VALUE_1"] = "AUTHORIZATION: " + authorization;
            }
            var result = await ExecuteAsync(start, ct);
            if (authorization is not null)
                result = result with { Stdout = result.Stdout.Replace(authorization, "[redacted]", StringComparison.Ordinal), Stderr = result.Stderr.Replace(authorization, "[redacted]", StringComparison.Ordinal) };
            if (result.ExitCode != 0) return result;
            metadata?.PublishData();
            if (clone)
            {
                var privateClone = Path.Combine(privateRoot, "clone");
                using var checkoutMetadata = new IsolatedGitMetadata(privateClone, url);
                var checkout = NewProcess();
                checkoutMetadata.Configure(checkout);
                checkout.ArgumentList.Add("reset"); checkout.ArgumentList.Add("--hard"); checkout.ArgumentList.Add("HEAD");
                if ((await ExecuteAsync(checkout, ct)).ExitCode != 0) throw new InvalidOperationException("Private clone checkout failed.");
                checkoutMetadata.PublishData();
                CopyPrivateClone(privateClone, cwd);
            }
            return result;
        }
        finally { Directory.Delete(privateRoot, recursive: true); }
    }

    internal static ProcessStartInfo NewProcess() => new("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    internal static async Task<Result> ExecuteAsync(ProcessStartInfo start, CancellationToken ct)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start controller Git.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); throw; }
        return new(process.ExitCode, await output, await error);
    }

    private static void CopyPrivateClone(string source, string destination)
    {
        if (Directory.Exists(destination) && new DirectoryInfo(destination).LinkTarget is not null)
            throw new InvalidOperationException("Clone destination may not be a link.");
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null) throw new InvalidOperationException("Linked clone content requires a separately qualified import path.");
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo) CopyPrivateClone(entry.FullName, target);
            else File.Copy(entry.FullName, target, overwrite: false);
        }
    }
}
