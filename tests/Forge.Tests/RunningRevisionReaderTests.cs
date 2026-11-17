using Forge.Dashboard;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public class RunningRevisionReaderTests
{
    internal const string FullSha = "0123456789aBcDeF0123456789AbCdEf01234567";

    public static TheoryData<string> ValidContent => new()
    {
        FullSha,
        FullSha + "\n",
        " \t\r\n" + FullSha + "\r\n\t ",
        new string('a', 40),
        new string('F', 40)
    };

    public static TheoryData<string> InvalidContent => new()
    {
        "", " \t\n", "Unavailable", "0123456", new string('a', 39),
        new string('a', 41), new string('g', 40), new string('０', 40),
        FullSha + "\n" + FullSha, FullSha + " " + FullSha,
        FullSha + "," + FullSha, FullSha.Insert(20, "\n"),
        "\0" + FullSha, "https://github.com/Xyrces/Forge/commit/" + FullSha,
        "1.0.0+" + FullSha
    };

    [Theory]
    [MemberData(nameof(ValidContent))]
    public async Task Read_TrimsAndPreservesExactlyTheFullHexSha(string content)
    {
        var reader = new RunningRevisionReader(NullLogger.Instance, (_, _) => Task.FromResult(content));

        Assert.Equal(content.Trim(), await reader.ReadAsync());
    }

    [Theory]
    [MemberData(nameof(InvalidContent))]
    public async Task Read_InvalidArtifactHasNoFallback(string content)
    {
        var calls = new List<string>();
        var reader = new RunningRevisionReader(NullLogger.Instance, (path, _) =>
        {
            calls.Add(path);
            // Any attempted alternate-source read would return a valid revision.
            return Task.FromResult(path == "/app/source-revision" ? content : FullSha);
        });

        Assert.Null(await reader.ReadAsync());
        Assert.Equal(new[] { "/app/source-revision" }, calls);
    }

    public static TheoryData<Exception> ReadFailures => new()
    {
        new FileNotFoundException("Injected missing artifact"),
        new DirectoryNotFoundException("Injected missing /app directory"),
        new UnauthorizedAccessException("Injected unreadable artifact"),
        new IOException("Injected artifact read failure")
    };

    [Theory]
    [MemberData(nameof(ReadFailures))]
    public async Task Read_FilesystemFailureIsUnavailableWithoutTryingAnotherSource(Exception failure)
    {
        var calls = new List<string>();
        var reader = new RunningRevisionReader(NullLogger.Instance, (path, _) =>
        {
            calls.Add(path);
            return path == "/app/source-revision" ? Task.FromException<string>(failure) : Task.FromResult(FullSha);
        });

        Assert.Null(await reader.ReadAsync());
        Assert.Equal(new[] { "/app/source-revision" }, calls);
    }

    [Fact]
    public async Task Read_UsesOnlyTheAbsoluteArtifactPathAndPassesCancellation()
    {
        using var cts = new CancellationTokenSource();
        var calls = new List<string>();
        var reader = new RunningRevisionReader(NullLogger.Instance, (path, ct) =>
        {
            calls.Add(path);
            Assert.Equal(cts.Token, ct);
            return Task.FromResult(FullSha);
        });

        Assert.Equal(FullSha, await reader.ReadAsync(cts.Token));
        Assert.Equal(new[] { "/app/source-revision" }, calls);
    }

    [Fact]
    public async Task Read_DoesNotHideCancellationAsUnavailable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reader = new RunningRevisionReader(NullLogger.Instance, (_, ct) => Task.FromCanceled<string>(ct));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cts.Token));
    }
}
