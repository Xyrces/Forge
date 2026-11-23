using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Forge.AgentTools;

/// <summary>Only command text and explicitly scoped project secrets cross into the toolchain.
/// The stock worker owns model clients, SQL, plan gates and verification receipts.</summary>
internal static class ToolchainCommand
{
    internal sealed record Result(int ExitCode, string Stdout, string Stderr, bool TimedOut);
    internal static string? SocketPath => Environment.GetEnvironmentVariable("FORGE_TOOLCHAIN_SOCKET");

    internal static async Task<Result> RunAsync(string socketPath, string command, string cwd,
        TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? projectEnvironment = null, bool verificationShell = false)
    {
        var environment = projectEnvironment?.Where(p => p.Key.StartsWith("FORGE_SECRET_", StringComparison.Ordinal))
            .ToDictionary(p => p.Key, p => p.Value) ?? [];
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout + TimeSpan.FromSeconds(10));
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), deadline.Token);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var request = JsonSerializer.SerializeToUtf8Bytes(new { command, cwd,
                timeoutMs = checked((int)timeout.TotalMilliseconds), environment, shell = verificationShell ? "bash" : "sh" });
            await stream.WriteAsync(request, deadline.Token);
            await stream.WriteAsync(new byte[] { (byte)'\n' }, deadline.Token);
            // A compromised custom image must not exhaust the credentialed worker's memory.
            var bytes = new byte[4096];
            using var response = new MemoryStream();
            while (true)
            {
                var count = await stream.ReadAsync(bytes, deadline.Token);
                if (count == 0) break;
                if (response.Length + count > 1024 * 1024) throw new InvalidOperationException("Toolchain response exceeds 1 MiB.");
                response.Write(bytes, 0, count);
            }
            return JsonSerializer.Deserialize<Result>(response.ToArray(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Toolchain returned no result.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(-1, "", "Toolchain command timed out.", true);
        }
        // Closing the connection cancels the remote process group, including descendants.
    }
}
