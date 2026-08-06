using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Dashboard;
using Forge.Orchestrator;
using Forge.Orchestrator.Workflow;
using Forge.Projects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Composition-wiring regression: the per-bundle PRWatcher must
/// receive the lifecycle machine. Without it every watcher report
/// no-ops, reworkForSha never lands, the rework guard stops
/// suppressing repeat strikes, and a queue-starved watched task eats
/// a circuit-breaker trip without a single rework round (observed
/// live 2026-07-31: porthorizon tasks 9/12/13).
/// </summary>
public class ProjectDispatchBundleFactoryTests : IDisposable
{
    private readonly string _workDir;

    public ProjectDispatchBundleFactoryTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-bf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public void Build_WiresLifecycleIntoPrWatcher()
    {
        var factory = new ProjectDispatchBundleFactory(
            new AgentOptions
            {
                Workspace = new WorkspaceOptions { Root = _workDir, WorktreeRoot = ".wt", DefaultBranch = "main" },
            },
            dataRoot: _workDir,
            projectStore: new ProjectStore(new IssueStore(Path.Combine(_workDir, "issues.db"))),
            cloner: new ProjectCloner(_workDir),
            runner: new NoOpRunner(),
            roleRegistry: new RoleAgentRegistry(),
            dispatcher: new NoOpDispatcher(),
            messageBus: new AgentMessageBus(),
            events: new InMemoryDashboardEventBus(),
            loggerFactory: NullLoggerFactory.Instance,
            lifecycle: new TaskStateMachine(writeAuthority: false, NullLogger.Instance));

        var bundle = factory.Build(new ProjectOptions
        {
            Id = "t", Name = "t", RepoUrl = "", Root = _workDir, DefaultBranch = "main",
        });

        Assert.True(bundle.PrWatcher.HasLifecycle);
    }

    private sealed class NoOpRunner : IAgentRunner
    {
        public Task<AgentRunResult> RunAsync(AgentType role, string prompt, string? sessionId, IReadOnlyDictionary<string, object>? context, CancellationToken ct)
            => Task.FromResult(new AgentRunResult("", null, 0, 0, TimeSpan.Zero));
    }

    private sealed class NoOpDispatcher : IWorkflowDispatcher
    {
        public Task DispatchAsync(IssueRecord issue, ProjectDispatchBundle bundle, CancellationToken ct)
            => Task.CompletedTask;
        public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
