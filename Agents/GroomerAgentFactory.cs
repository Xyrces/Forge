using Microsoft.Extensions.Logging;
using Forge.Core;
using Forge.Dashboard;

namespace Forge.Agents;

/// <summary>
/// Factory for <see cref="GroomerAgent"/> instances. Each operator
/// "Start grooming" click produces one short-lived agent run.
/// </summary>
public sealed class GroomerAgentFactory
{
    private readonly IIssueStore _issues;
    private readonly ISpecStore _specs;
    private readonly IDashboardEventBus _events;
    private readonly IChatClientFactory _chatClientFactory;
    private readonly LlmConfig _config;
    private readonly ILoggerFactory _loggerFactory;

    public GroomerAgentFactory(
        IIssueStore issues,
        ISpecStore specs,
        IDashboardEventBus events,
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        ILoggerFactory loggerFactory)
    {
        _issues = issues;
        _specs = specs;
        _events = events;
        _chatClientFactory = chatClientFactory;
        _config = config;
        _loggerFactory = loggerFactory;
    }

    public GroomerAgent Create(string? runId = null) => new(
        _issues, _specs, _events, _chatClientFactory, _config,
        _loggerFactory.CreateLogger<GroomerAgent>(),
        runId: runId);
}