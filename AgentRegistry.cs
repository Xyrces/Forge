using System.Text.Json.Serialization;

namespace PortHorizon.Agents.Core;

public sealed class AgentRegistry
{
    private readonly Dictionary<AgentType, AgentConfig> _agents = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public void Register(AgentConfig config)
    {
        _agents[config.Type] = config;
    }

    public AgentConfig? Get(AgentType type)
        => _agents.GetValueOrDefault(type);

    public IEnumerable<AgentConfig> GetAll()
        => _agents.Values;

    public bool TryGet(AgentType type, out AgentConfig? config)
        => _agents.TryGetValue(type, out config);

    public async Task<AgentConfig?> GetLeastLoadedAsync(AgentType type, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            return _agents.Values
                .Where(a => a.Type == type && a.Status == AgentStatus.Idle)
                .OrderBy(a => a.CurrentTasksCount)
                .FirstOrDefault();
        }
        finally
        {
            _lock.Release();
        }
    }
}

public record AgentConfig(
    AgentType Type,
    string Name,
    string ProjectPath,
    string Instructions,
    List<string> Rules,
    int MaxConcurrentTasks = 1
)
{
    public AgentStatus Status { get; set; } = AgentStatus.Idle;
    public int CurrentTasksCount { get; set; } = 0;
}