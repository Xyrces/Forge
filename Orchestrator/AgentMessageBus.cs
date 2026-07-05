using System.Threading.Channels;

namespace Forge.Orchestrator;

/// <summary>
/// Per-agent message inbox. Send-to-agent from the dashboard enqueues here;
/// the orchestrator drains the queue right before sending the next prompt and
/// prepends the messages under an "Operator messages" header.
///
/// In-process only; messages are not persisted (single orchestrator case).
/// For multi-process safety, persist in agent_message SQLite table (future).
/// </summary>
public sealed class AgentMessageBus
{
    private readonly Dictionary<string, Channel<string>> _channels = new();
    private readonly object _lock = new();

    public void Enqueue(string agentId, string message)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(message)) return;
        var ch = ChannelFor(agentId);
        ch.Writer.TryWrite(message);
    }

    public string Drain(string agentId)
    {
        Channel<string>? ch;
        lock (_lock) { _channels.TryGetValue(agentId, out ch); }
        if (ch is null) return string.Empty;
        var sb = new System.Text.StringBuilder();
        while (ch.Reader.TryRead(out var msg))
        {
            if (sb.Length > 0) sb.Append(Environment.NewLine);
            sb.Append(msg);
        }
        return sb.ToString();
    }

    public int Count(string agentId)
    {
        lock (_lock)
        {
            if (!_channels.TryGetValue(agentId, out var ch)) return 0;
            var n = 0;
            while (ch.Reader.TryRead(out _)) n++;
            return n;
        }
    }

    private Channel<string> ChannelFor(string agentId)
    {
        lock (_lock)
        {
            if (!_channels.TryGetValue(agentId, out var ch))
            {
                ch = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
                _channels[agentId] = ch;
            }
            return ch;
        }
    }
}




