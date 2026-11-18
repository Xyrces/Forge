using System.Text.Json;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Db;
using Forge.Core.Execution;
using Microsoft.Extensions.Logging;

namespace Forge.Orchestrator.Workers;

/// <summary>A worker builds only one project's agent dependencies; it never bootstraps or starts schedulers.</summary>
public static class WorkerHost
{
    public static async Task<int> RunAsync(AgentOptions options, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (!options.Db.IsSqlServer) throw new InvalidOperationException("Workers require Azure SQL state.");
        var worker = options.Worker;
        if (string.IsNullOrWhiteSpace(worker.AttemptId) || string.IsNullOrWhiteSpace(worker.PodUid))
            throw new InvalidOperationException("Worker attempt and pod UID are required.");
        var registry = ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.RegistrySchema, ForgeSchemaProfile.Core, initializeSchema: false);
        var attempts = new WorkerAttemptStore(registry);
        var assignment = await attempts.GetAsync(worker.AttemptId, ct) ?? throw new InvalidOperationException("Unknown worker attempt.");
        if (assignment.DatasetId != options.Controller.DatasetId) throw new InvalidOperationException("Worker dataset mismatch.");
        if (!await attempts.ClaimAsync(assignment.Id, worker.PodUid, ct)) return 0; // duplicate pod is inert
        var workerStartedAt = DateTimeOffset.UtcNow;
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var logger = loggerFactory.CreateLogger("Forge.Worker");
        var monitor = MonitorAsync();
        try
        {
            var invocation = JsonSerializer.Deserialize<WorkerInvocation>(assignment.AssignmentJson)
                ?? throw new InvalidOperationException("Invalid worker assignment.");
            if (!string.Equals(await KubernetesAgentRunner.ReadHeadAsync(assignment.WorktreePath, runCancellation.Token), assignment.SourceSha, StringComparison.Ordinal))
                throw new InvalidOperationException("Worker source changed before pod execution.");
            using var runtime = await BuildRunner(options, assignment, attempts, loggerFactory, runCancellation.Token);
            var context = invocation.Context.ToDictionary(x => x.Key, x => (object)x.Value);
            context["workerAttemptId"] = assignment.Id;
            context["workerStartedAt"] = workerStartedAt.ToString("O");
            var modelMinutes = invocation.Context.TryGetValue("modelTimeoutMinutes", out var configured)
                ? double.Parse(configured, System.Globalization.CultureInfo.InvariantCulture)
                : options.Spawner.AgentRunTimeoutMinutes;
            if (!double.IsFinite(modelMinutes) || modelMinutes < 0)
                throw new InvalidOperationException("Invalid worker model deadline.");
            var result = await RunPhasesAsync(runtime.Runner, invocation, context,
                modelMinutes > 0 ? TimeSpan.FromMinutes(modelMinutes) : null, async token =>
                {
                    await attempts.EnsureOwnerAsync(assignment.Id, worker.PodUid, assignment.AttemptToken, token);
                    return await AgentTools.WorkerVerification.VerifyAsync(assignment.Id, assignment.WorktreePath,
                        runtime.VerifyCommands, logger, token);
                }, runCancellation.Token, async (progress, token) =>
                {
                    if (!await attempts.RecordProgressAsync(assignment.Id, worker.PodUid,
                        assignment.AttemptToken, JsonSerializer.Serialize(progress), token))
                        throw new OperationCanceledException("Worker phase recording lost ownership.", token);
                });
            await attempts.EnsureOwnerAsync(assignment.Id, worker.PodUid, assignment.AttemptToken, runCancellation.Token);
            using var finalize = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (!await attempts.CompleteAsync(assignment.Id, worker.PodUid, assignment.AttemptToken, JsonSerializer.Serialize(result), null, finalize.Token))
                throw new InvalidOperationException("Worker result publication lost ownership.");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Worker attempt {Attempt} failed", assignment.Id);
            using var finalize = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await attempts.CompleteAsync(assignment.Id, worker.PodUid, assignment.AttemptToken, null,
                ex is OperationCanceledException ? "cancelled" : ex.Message, finalize.Token);
            return 1;
        }
        finally
        {
            await monitorCancellation.CancelAsync();
            await monitor;
        }

        async Task MonitorAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), monitorCancellation.Token);
                    await attempts.EnsureOwnerAsync(assignment.Id, worker.PodUid, assignment.AttemptToken, monitorCancellation.Token);
                }
            }
            catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Worker ownership monitor stopped; cancelling tools and model requests");
                await runCancellation.CancelAsync();
            }
        }
    }

    /// <summary>The model deadline ends before independent exact-tree verification starts.
    /// Verification keeps RunVerification's per-command deadline and the Job's overall hard ceiling.</summary>
    internal static async Task<AgentRunResult> RunPhasesAsync(IAgentRunner runner, WorkerInvocation invocation,
        IReadOnlyDictionary<string, object> context, TimeSpan? modelTimeout,
        Func<CancellationToken, Task<VerificationReceipt>> verify, CancellationToken ct,
        Func<AgentRunResult, CancellationToken, Task>? recordProgress = null)
    {
        AgentRunResult result;
        var modelStarted = DateTimeOffset.UtcNow;
        using (var model = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            if (modelTimeout is { } timeout) model.CancelAfter(timeout);
            try
            {
                result = await runner.RunAsync(invocation.Role, invocation.Prompt, invocation.SessionId, context, model.Token);
                model.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (model.IsCancellationRequested && !ct.IsCancellationRequested)
            { throw new TimeoutException($"Worker model phase timed out after {modelTimeout}."); }
        }
        ct.ThrowIfCancellationRequested();
        var engineering = invocation.Role is AgentType.CoreDev or AgentType.ClientDev;
        var modelCompleted = DateTimeOffset.UtcNow;
        DateTimeOffset? Measured(string key) => context.TryGetValue(key, out var value)
            && DateTimeOffset.TryParse(value?.ToString(), out var timestamp) ? timestamp : null;
        result = result with { Phases = new WorkerPhaseTiming(modelStarted, modelCompleted,
            engineering ? modelCompleted : null, EligibleAt: Measured("dispatchEligibleAt"),
            WorkerStartedAt: Measured("workerStartedAt")) };
        if (recordProgress is not null) await recordProgress(result, ct);
        if (engineering)
        {
            var verification = await verify(ct);
            result = result with { Verification = verification,
                Phases = result.Phases! with { VerificationCompletedAt = DateTimeOffset.UtcNow } };
            if (recordProgress is not null) await recordProgress(result, ct);
        }
        ct.ThrowIfCancellationRequested();
        return result;
    }

    public static async Task<WorkerRuntime> BuildRunner(AgentOptions options, WorkerAssignment assignment,
        WorkerAttemptStore attempts, ILoggerFactory logs, CancellationToken ct)
    {
        var registry = ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.RegistrySchema, ForgeSchemaProfile.Core, initializeSchema: false);
        var registryIssues = new IssueStore(registry);
        var projects = new ProjectStore(registryIssues);
        var project = await projects.GetAsync(assignment.ProjectId, ct) ?? throw new InvalidOperationException("Worker project is not registered.");
        if (string.IsNullOrEmpty(project.LocalPath)) throw new InvalidOperationException("Worker project has no clone path.");
        var workload = ForgeDb.SqlServer(options.Db.ConnectionString, ForgeDb.SchemaForProject(project.Id), initializeSchema: false);
        var issues = new IssueStore(workload, project.Id);
        var memory = new MemoryStore(workload);
        var secrets = new SecretStore(registryIssues, Program.CreateSecretProtection(options), logs.CreateLogger<SecretStore>());
        var config = LlmConfigAdapter.FromOptions(options.Llm);
        var overrides = new RoleModelOverrides(memory);
        await overrides.LoadAsync(ct);
        var keys = new ProviderApiKeyResolver(secrets, _ => Task.FromResult<IReadOnlyList<string>>([project.Id]), logs.CreateLogger<ProviderApiKeyResolver>());
        await keys.RefreshAsync(config.Providers.Select(p => p.Name), ct);
        var factory = new OpenAICompatibleChatClientFactory
        {
            Overrides = overrides, KeyResolver = keys, RateLimits = new ModelRateLimitTracker(),
            MaxConcurrentRequests = options.Llm.MaxConcurrentRequests,
            OverloadRetryCount = 0, MinRequestInterval = TimeSpan.FromMilliseconds(options.Llm.MinRequestIntervalMs),
            RequestPolicy = Control.BudgetRequestPolicy.Create(options, registry,
                token => attempts.EnsureOwnerAsync(assignment.Id, options.Worker.PodUid, assignment.AttemptToken, token))
        };
        var roles = new RoleAgentRegistry();
        var artifacts = new DesignArtifactStore(workload);
        var runner = new MafAgentRunner(factory, config, roles, logs.CreateLogger<MafAgentRunner>(),
            skills: new SqliteSkillSource(new SkillStore(registryIssues), roles),
            rolePromptsRoot: RolePromptRoot.Resolve(project.LocalPath),
            projectRootLookup: id => id == project.Id ? project.LocalPath : null,
            projectTerritoryLookup: id => id == project.Id ? project.Territories : null,
            verifyCommandsLookup: id => id == project.Id ? project.VerifyCommands : null,
            memory: memory, handoffs: new ContextHandoffStore(workload), designArtifacts: () => artifacts,
            specs: () => new SpecStore(issues, designArtifacts: artifacts), artOutputs: () => new ArtOutputStore(workload),
            secrets: secrets, issues: issues, runs: new AgentRunStore(workload), modelOverrides: overrides, gates: options.Gates);
        return new(runner, factory, project.VerifyCommands ?? AgentTools.RunVerification.DefaultCommands(assignment.WorktreePath));
    }
}

public sealed class WorkerRuntime(IAgentRunner runner, IDisposable owner, IReadOnlyList<string> verifyCommands) : IDisposable
{
    public IAgentRunner Runner { get; } = runner;
    public IReadOnlyList<string> VerifyCommands { get; } = verifyCommands;
    public void Dispose() => owner.Dispose();
}
