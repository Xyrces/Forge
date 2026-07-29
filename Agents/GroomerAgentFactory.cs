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
    private readonly MemoryStore? _memory;
    private readonly string? _projectRoot;
    private readonly Func<string, string?>? _projectRootLookup;

    public GroomerAgentFactory(
        IIssueStore issues,
        ISpecStore specs,
        IDashboardEventBus events,
        IChatClientFactory chatClientFactory,
        LlmConfig config,
        ILoggerFactory loggerFactory,
        MemoryStore? memory = null,
        string? projectRoot = null,
        // Multi-project grounding: resolves a spec's project id to its
        // clone root so the repo-shape digest describes the RIGHT
        // repo. Wins over the static projectRoot when both resolve.
        Func<string, string?>? projectRootLookup = null)
    {
        _issues = issues;
        _specs = specs;
        _events = events;
        _chatClientFactory = chatClientFactory;
        _config = config;
        _loggerFactory = loggerFactory;
        _memory = memory;
        _projectRoot = projectRoot;
        _projectRootLookup = projectRootLookup;
    }

    public GroomerAgent Create(string? runId = null) => new(
        _issues, _specs, _events, _chatClientFactory, _config,
        _loggerFactory.CreateLogger<GroomerAgent>(),
        runId: runId, memory: _memory, projectRoot: _projectRoot,
        projectRootLookup: _projectRootLookup);
}