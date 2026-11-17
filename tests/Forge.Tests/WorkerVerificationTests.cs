using System.Diagnostics;
using Forge.AgentTools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class WorkerVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-receipt-" + Guid.NewGuid().ToString("N"));
    public WorkerVerificationTests()
    {
        Directory.CreateDirectory(_root);
        Git("init", "-q"); Git("config", "user.email", "test@example.invalid"); Git("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_root, "source.txt"), "original"); Git("add", "."); Git("commit", "-qm", "initial");
    }

    [Fact]
    public async Task ReceiptBindsUntrackedAndTrackedSourceAndExactCommands()
    {
        File.WriteAllText(Path.Combine(_root, "new.txt"), "new source");
        File.WriteAllText(Path.Combine(_root, "source.txt"), "changed source");
        var stagedBefore = Git("diff", "--cached", "--name-only");
        var receipt = await WorkerVerification.VerifyAsync("attempt", _root, [], NullLogger.Instance, default);
        Assert.Equal(stagedBefore, Git("diff", "--cached", "--name-only"));
        Assert.True(await WorkerVerification.MatchesAsync(receipt, _root, [], default));
        Assert.False(await WorkerVerification.MatchesAsync(receipt, _root, ["different-command"], default));
        File.WriteAllText(Path.Combine(_root, "new.txt"), "changed after verification");
        Assert.False(await WorkerVerification.MatchesAsync(receipt, _root, [], default));
    }

    [Fact]
    public async Task ReceiptSurvivesCommitOfExactlyVerifiedTree()
    {
        File.WriteAllText(Path.Combine(_root, "source.txt"), "changed source");
        var receipt = await WorkerVerification.VerifyAsync("attempt", _root, [], NullLogger.Instance, default);
        Git("add", "-A"); Git("commit", "-qm", "verified");
        Assert.True(await WorkerVerification.MatchesAsync(receipt, _root, [], default));
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
        process.WaitForExit(); Assert.True(process.ExitCode == 0, error); return output;
    }
    public void Dispose()
    {
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}

public sealed class ToolSecretRedactionTests
{
    [Fact]
    public async Task BashKeepsPackageCredentialsUsableButMasksEchoedValues()
    {
        const string token = "fake_packages_token_123456";
        var tool = new BashTool(Environment.CurrentDirectory, envVars: new Dictionary<string, string>
        { ["GITHUB_PACKAGES_USER"] = "fake-user", ["GITHUB_PACKAGES_PAT"] = token });
        var result = await tool.Bash(OperatingSystem.IsWindows() ? "echo %GITHUB_PACKAGES_PAT%" : "printf '%s' \"$GITHUB_PACKAGES_PAT\"");
        Assert.StartsWith("exit=0", result); Assert.Contains("[REDACTED]", result); Assert.DoesNotContain(token, result);
    }

    [Fact]
    public void KnownTokensAreMaskedInRawUrlEncodedAndBasicAuthForms()
    {
        const string token = "fake+token/123";
        var redactor = new ToolSecretRedactor(new Dictionary<string, string?>
        { ["GITHUB_PACKAGES_USER"] = "user", ["GITHUB_PACKAGES_PAT"] = token });
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("user:" + token));
        Assert.Equal("[REDACTED] [REDACTED] [REDACTED]", redactor.Redact(token + " " + Uri.EscapeDataString(token) + " " + encoded));
    }
}
