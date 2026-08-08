using Forge.Configuration;
using Forge.Core;
using Forge.Core.Messaging;
using Forge.Messaging;
using Forge.Orchestrator.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Talaria.Core.Abstractions;
using Talaria.Transports.InMemory;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// Composition smoke test (PR #90 review finding: nothing referenced
/// ForgeComposition in tests — an omitted consumer registration or a
/// broken factory lambda failed only at deploy time). Builds the FULL
/// runtime graph against a throwaway SQLite state dir + local git repo
/// and resolves the messaging services + every consumer + DashboardHost
/// (whose factory lambda touches most of the graph).
/// </summary>
public sealed class ForgeCompositionTests : IDisposable
{
    private readonly string _dataRoot;
    private readonly string _repoDir;

    public ForgeCompositionTests()
    {
        _dataRoot = TempRoot.Instance.NewDirectory("composition-state");
        _repoDir = TempRoot.Instance.NewDirectory("composition-repo");
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(_repoDir);
        InitRepo(_repoDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); } catch { }
        try { Directory.Delete(_repoDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Composition_Builds_EveryConsumerRegistered_AndGraphResolves()
    {
        // Seed the project registry (DB-only registry) with a local repo.
        var registryDb = ForgesystemPaths.IssuesDb(_dataRoot, "default");
        var registryIssues = new IssueStore(registryDb);
        var projectStore = new ProjectStore(registryIssues);
        await projectStore.UpsertAsync(new NewProject(
            Id: "smoke", Name: "Smoke", RepoUrl: _repoDir, DefaultBranch: "main"));

        var options = new AgentOptions
        {
            Forgesystem = new ForgesystemOptions { DataRoot = _dataRoot },
            GitHub = new GitHubOptions { Owner = "smoke", Repo = "smoke", Token = "x" },
            Dashboard = new DashboardOptions { Enabled = false },
        };

        await using var provider = await ForgeComposition.BuildAsync(
            options, NullLoggerFactory.Instance);

        // Messaging spine.
        Assert.IsType<InMemoryTransport>(provider.GetRequiredService<ITransport>());
        Assert.IsType<TalariaEventPublisher>(provider.GetRequiredService<IEventPublisher>());
        Assert.NotNull(provider.GetService<SweepTickPublisher>());

        // Every consumer registered under the marker — the set Program
        // starts. Nine topics, nine consumers, one per topic.
        var consumers = provider.GetServices<IEventConsumerService>().ToList();
        Assert.Equal(9, consumers.Count);
        Assert.Equal(9, consumers.Select(c => c.GetType()).Distinct().Count());

        // The heavy factory registrations resolve (DashboardHost's
        // factory lambda touches most of the graph).
        Assert.NotNull(provider.GetService<Forge.Dashboard.DashboardHost>());
        Assert.NotNull(provider.GetService<Forge.Orchestrator.OrchestratorAgent>());
        Assert.NotNull(provider.GetService<Forge.Orchestrator.Sprint.SprintAssembler>());
        Assert.NotNull(provider.GetService<Forge.Orchestrator.ScheduledGroomer>());
    }

    private static void InitRepo(string path)
    {
        RunGit(path, "init", "-q -b main");
        RunGit(path, "config", "user.email a@b");
        RunGit(path, "config", "user.name a");
        File.WriteAllText(Path.Combine(path, "README.md"), "# Test\n");
        RunGit(path, "add", "README.md");
        RunGit(path, "commit", "-q -m initial");
    }

    private static void RunGit(string cwd, string verb, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.ArgumentList.Add(verb);
        foreach (var part in args.Split(' ')) psi.ArgumentList.Add(part);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(60_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {verb} {args} failed: {p.StandardError.ReadToEnd()}");
    }
}
