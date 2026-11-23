using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Forge.AgentTools;
using Xunit;

namespace Forge.Tests;

[CollectionDefinition("Toolchain process environment", DisableParallelization = true)]
public sealed class ToolchainEnvironmentCollection { }

[Collection("Toolchain process environment")]
public sealed class ToolchainCommandTests
{
    [Fact]
    public async Task PlanAndPublicationRefusalsHappenBeforeSocketConnection()
    {
        var prior = Environment.GetEnvironmentVariable("FORGE_TOOLCHAIN_SOCKET");
        Environment.SetEnvironmentVariable("FORGE_TOOLCHAIN_SOCKET", "/missing-toolchain-socket");
        try
        {
            var tool = new BashTool(Environment.CurrentDirectory, mutationsAllowed: () => false, publicationForbidden: true);
            Assert.Contains("no approved plan", await tool.Bash("rm source.txt"));
            Assert.Contains("publication is disabled", await tool.Bash("git push"));
        }
        finally { Environment.SetEnvironmentVariable("FORGE_TOOLCHAIN_SOCKET", prior); }
    }

    [Fact]
    public async Task SocketRequestCarriesOnlyExplicitProjectSecretsAndReturnsExitStatus()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(Path.GetTempPath(), "tc-" + Guid.NewGuid().ToString("N"));
        using var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        server.Bind(new UnixDomainSocketEndPoint(path)); server.Listen();
        try
        {
            var response = Task.Run(async () =>
            {
                using var peer = await server.AcceptAsync();
                await using var stream = new NetworkStream(peer);
                using var reader = new StreamReader(stream, leaveOpen: true);
                using var request = JsonDocument.Parse((await reader.ReadLineAsync())!);
                var env = request.RootElement.GetProperty("environment");
                Assert.Equal("allowed", env.GetProperty("FORGE_SECRET_TEST").GetString());
                Assert.False(env.TryGetProperty("OPENAI_API_KEY", out _));
                Assert.False(env.TryGetProperty("Db__ConnectionString", out _));
                await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"exitCode\":7,\"stdout\":\"output\",\"stderr\":\"failure\",\"timedOut\":false}"));
            });
            var result = await ToolchainCommand.RunAsync(path, "test", "/checkout", TimeSpan.FromSeconds(5), default,
                new Dictionary<string, string> { ["FORGE_SECRET_TEST"] = "allowed", ["OPENAI_API_KEY"] = "forbidden", ["Db__ConnectionString"] = "forbidden" });
            await response;
            Assert.Equal(7, result.ExitCode); Assert.Equal("output", result.Stdout); Assert.False(result.TimedOut);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CallerCancellationClosesRemoteConnection()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(Path.GetTempPath(), "tc-" + Guid.NewGuid().ToString("N"));
        using var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        server.Bind(new UnixDomainSocketEndPoint(path)); server.Listen();
        using var cancel = new CancellationTokenSource();
        try
        {
            var call = ToolchainCommand.RunAsync(path, "sleep 60", "/checkout", TimeSpan.FromSeconds(60), cancel.Token);
            using var peer = await server.AcceptAsync();
            await using var stream = new NetworkStream(peer);
            using var reader = new StreamReader(stream, leaveOpen: true);
            Assert.NotNull(await reader.ReadLineAsync());
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
            var buffer = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { File.Delete(path); }
    }
}
