using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Workflow;
using Forge.Dashboard;
using Forge.Reviewer;

namespace Forge.Orchestrator;

public sealed class OrchestratorAgent : IAgent
{
    private readonly IProjectStore _projectStore;
    private readonly IProjectDispatchBundleFactory _bundleFactory;
    private readonly RoleAgentRegistry _roleRegistry;
    private readonly IAgentRunner _runner;
    private readonly AgentMessageBus _messageBus;
    private readonly IWorkflowDispatcher _dispatcher;
    private readonly IDashboardEventBus _events;
    private readonly ILogger<OrchestratorAgent> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ConcurrentDictionary<string, ProjectDispatchBundle> _bundles = new();
    private SpawnerOptions _spawnerOptions = new();
    private readonly Slots.SlotTable _slots;
    private readonly int _maxRetryCount;
    // GitHub rate-limit cooldown for the PR-watch path. When Octokit
    // reports RateLimitExceeded, watch issues are skipped until this
    // time so the loop doesn't hammer the API every dispatch cycle.
    /// <summary>Backoff after a failed dispatch cycle (transient infra
    /// outage). Long enough to let DNS/SQL recover, short enough that
    /// the queue resumes promptly.</summary>
    private const int CycleFailureBackoffSeconds = 30;
    // Watch sweep cadence. Previously every dispatch cycle spawned a
    // parallel Task.Run per Pending watch, and ProcessWatchTaskAsync
    // looped internally every 30s with 3 API calls per iteration —
    // loops multiplied unboundedly (watches are never claimed, so they
    // stay Pending) and vaporized the 5000-req/hr GitHub quota. Now
    // watches are polled in ONE sequential sweep every WatchSweepInterval:
    // 3 calls per watch per sweep (2 watches -> ~24 calls/hr).
    // LLM 429 cooldowns for the engineering dispatch path, keyed by
    // (provider, model) — quotas live at that boundary, so a 429 from
    // minimax must not freeze tasks that would run on a different
    // model (e.g. kimi-k3 reserved for grooming/review). A 429
    // re-queues the task (not a code failure) and pauses new
    // dispatches FOR THAT MODEL ONLY.
    private readonly ModelRateLimitTracker _modelCooldowns;
    private readonly Core.TaskStateMachine? _lifecycle;
    private readonly Core.Workflow.WorkflowResolver? _workflow;
    private readonly WakeupSignal? _wakeup;
    private Agents.LlmConfig? _llmConfig;
    private static readonly TimeSpan LlmRateLimitCooldown = TimeSpan.FromMinutes(3);
    // In-flight dev dispatches. The cycle fire-and-forgets runs via
    // Task.Run so the watch sweep isn't starved — but without this
    // guard, every poll tick can claim MORE work before an earlier
    // run fails (the 429 cooldown is only set when the run fails),
    // which produced burst 429s and same-task double-claims 1ms
    // apart (observed live 2026-07-24). Per-task dedup lives here;
    // per-ROLE parallelism caps live in the SlotTable (a task only
    // claims when its role has a free slot), so e.g. 2 coredevs can
    // work two unblocked tasks while a clientdev slot stays open.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

    public string Id => "orchestrator";
    public string Name => "OrchestratorAgent";
    public AgentType Type => AgentType.Orchestrator;
    public AgentStatus Status { get; private set; } = AgentStatus.Idle;

    public OrchestratorAgent(
        IProjectStore projectStore,
        IProjectDispatchBundleFactory bundleFactory,
        IAgentRunner runner,
        RoleAgentRegistry roleRegistry,
        AgentMessageBus messageBus,
        IWorkflowDispatcher dispatcher,
        IDashboardEventBus events,
        ILogger<OrchestratorAgent> logger,
        ILoggerFactory? loggerFactory = null,
        Slots.SlotTable? slots = null,
        ModelRateLimitTracker? modelCooldowns = null,
        Core.TaskStateMachine? lifecycle = null,
        Core.Workflow.WorkflowResolver? workflow = null,
        WakeupSignal? wakeup = null)
    {
        _projectStore = projectStore;
        _bundleFactory = bundleFactory;
        _runner = runner;
        _roleRegistry = roleRegistry;
        _messageBus = messageBus;
        _dispatcher = dispatcher;
        _events = events;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _slots = slots ?? new Slots.SlotTable();
        _modelCooldowns = modelCooldowns ?? new ModelRateLimitTracker();
        _lifecycle = lifecycle;
        _workflow = workflow;
        _wakeup = wakeup;
        _maxRetryCount = 1;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        Status = AgentStatus.Running;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await DispatchCycleAsync(cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Transient infra failures (SQL outage, DNS flake,
                    // gateway blips OUTSIDE an agent run) must not kill
                    // the loop — log, back off, continue (observed live
                    // 2026-07-30: a 3am Azure SQL outage crashed the
                    // orchestrator and took the dashboard down with it
                    // for hours; systemd saw a live process and never
                    // restarted).
                    _logger.LogError(ex,
                        "Dispatch cycle failed; backing off {BackoffSeconds}s and continuing",
                        CycleFailureBackoffSeconds);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(CycleFailureBackoffSeconds), cancellationToken);
                    }
                    catch (OperationCanceledException) { break; }
                    continue;
                }
                try
                {
                    await WaitForNextCycleAsync(cancellationToken);
                }
                catch (OperationCanceledException) { break; }
            }
            Status = AgentStatus.Idle;
        }
        catch
        {
            Status = AgentStatus.Error;
            throw;
        }
    }

    /// <summary>
    /// Inter-cycle wait. With a <see cref="WakeupSignal"/> wired
    /// (production), the loop sleeps until a message consumer signals
    /// (TaskEnqueued / TaskTransitioned) or a finishing run frees a role
    /// slot; the 15-minute backstop re-derives everything if hints are
    /// lost (crash, machine suspend). Without a signal (tests, legacy
    /// construction) it falls back to the configured poll interval.
    /// </summary>
    internal static readonly TimeSpan DispatchBackstopInterval = TimeSpan.FromMinutes(15);

    private async Task WaitForNextCycleAsync(CancellationToken cancellationToken)
    {
        if (_wakeup is null)
        {
            await Task.Delay(TimeSpan.FromSeconds(_spawnerOptions.PollIntervalSeconds), cancellationToken);
            return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DispatchBackstopInterval);
        try
        {
            await _wakeup.WaitAsync(timeout.Token);
            _logger.LogDebug("Dispatch loop woke on event signal");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Dispatch loop woke on {Minutes}m backstop tick", DispatchBackstopInterval.TotalMinutes);
        }
    }

    /// <summary>
    /// One iteration of the dispatch loop. Walks every registered
    /// project, asks each project's bundle for its ready queue, and
    /// dispatches. Runtime-added projects (via POST /api/projects)
    /// are picked up on the next cycle without a service restart.
    /// </summary>
    private async Task DispatchCycleAsync(CancellationToken cancellationToken)
    {
        var projectRecords = await _projectStore.ListAsync(cancellationToken);
        if (projectRecords.Count == 0) return;

        foreach (var record in projectRecords)
        {
            if (cancellationToken.IsCancellationRequested) return;
            var bundle = GetOrCreateBundle(ProjectRecordToOptions(record));
            if (bundle is null) continue;

            try
            {
                var activeSprint = await bundle.Sprints.GetActiveAsync(cancellationToken);

                // Fetch the full ready queue (limit 0) and filter in
                // memory: containers (epic/story) clog the queue head
                // when the LIMIT is applied before filtering, so real
                // tasks behind them never dispatch (found live: 7
                // stories + a watch starved 4 feature tasks).
                var allReady = await bundle.IssueStore.ReadyAsync(0, sprintId: null, cancellationToken);

                // The PR watch sweep no longer runs in the dispatch
                // cycle: it is message-driven (SweepTick(watch) backstop
                // + PrOpened / ReviewVerdictRecorded / MergeReady fast
                // paths) via WatchSweepService + the watch consumers.

                // Sprint flow gate: ALL engineering work happens inside
                // a sprint. No active sprint => the SprintAssembler
                // hasn't ingested work yet (or nothing is eligible);
                // until then only design/planning stages run.
                if (activeSprint is null)
                {
                    _logger.LogDebug("Dispatch cycle: no active sprint (project={Project}) — engineering dispatch gated off", bundle.Project.Id);
                    continue;
                }
                var sprintMemberIds = new HashSet<string>(
                    await bundle.Sprints.GetIssueIdsAsync(activeSprint.Id, cancellationToken),
                    StringComparer.Ordinal);

                // Engineering dispatch skips pipeline containers.
                // Epics and stories feed the spec -> groom chain;
                // they are not units of engineering work. (Found by
                // the first UI e2e: an intake-accepted epic was
                // claimed directly and implemented, bypassing the
                // entire pipeline.) All other types dispatch,
                // preserving operator-enqueued type names (dev, ecs,
                // ui, bug, ...).
                var ready = allReady.Where(i => i.Type != AgentTaskTypes.PrWatch
                    && !AgentTaskTypes.IsContainer(i.Type)
                    && sprintMemberIds.Contains(i.Id)
                    && !_inFlight.ContainsKey(i.Id)
                    // Operator rule 2026-07-23/31: no task RUNS in a
                    // sprint without technical grooming. The assembler
                    // only adds groomed work, but a blocksIssueId
                    // follow-up is now BORN into the sprint ungroomed
                    // (FollowUpTool) — it sits as a member until the
                    // groomer's ad-hoc pass clears it. Spec-chain tasks
                    // (parented) derive eligibility from their chain.
                    && (i.ParentIssueId is not null
                        || string.Equals(i.GetMetadata("groomed"), "true", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                // Per-(provider, model) 429 cooldowns: skip only the
                // tasks whose model is cooling down. A minimax 429
                // must not starve a task that would run on kimi-k3.
                var coolingModels = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                var claimable = new List<IssueRecord>(ready.Count);
                foreach (var t in ready)
                {
                    var mk = ResolveModelKey(t, bundle.Project.Id);
                    var until = _modelCooldowns.CoolingDownUntil(mk.Provider, mk.Model);
                    if (until is null) claimable.Add(t);
                    else coolingModels[mk.Provider + "/" + mk.Model] = until.Value;
                }
                if (claimable.Count == 0 && coolingModels.Count > 0)
                {
                    foreach (var (model, until) in coolingModels)
                        _logger.LogDebug("Dispatch cycle: {N} dev task(s) skipped — model {Model} cooling down until {Until:HH:mm:ss}",
                            ready.Count, model, until);
                    continue;
                }
                // Per-role parallelism: every ready unblocked sprint
                // task competes for ITS role's slot pool (coredev,
                // clientdev, qa, reviewer). A full pool skips just
                // that role's tasks — other roles keep claiming.
                foreach (var dev in claimable)
                {
                    var role = ResolveRoleName(dev.Type);
                    // Triage model escalation (phase 3): a task
                    // carrying an unconsumed marker acquires ONLY its
                    // (project, escalated-role) slot — never the
                    // normal role pool. Slots are per-model
                    // concurrency limiters and the escalated run rides
                    // a DIFFERENT model with its own limit pool; a
                    // full escalation pool skips just this task this
                    // cycle (it stays Pending), same as a full role
                    // pool.
                    var escalated = TaskEscalations is not null
                        && TaskEscalations.Peek(bundle.Project.Id, dev.Id)
                        && TryResolveEscalationModel(dev.Type, bundle.Project.Id, out _);
                    if (escalated)
                    {
                        var poolRole = EscalationPoolRole(role);
                        if (_slots.MaxFor(bundle.Project.Id, poolRole) == 0)
                            _slots.Configure(bundle.Project.Id, poolRole, EscalationSlotCap);
                    }
                    else
                    {
                        EnsureSlotCap(bundle.Project, role);
                    }
                    var slot = await _slots.TryAcquireAsync(
                        bundle.Project.Id,
                        escalated ? EscalationPoolRole(role) : role,
                        TimeSpan.Zero, cancellationToken);
                    if (slot is null)
                    {
                        _logger.LogDebug("Dispatch cycle: role '{Role}'{Escalated} at cap (project={Project}) — {Id} waits for a free slot",
                            role, escalated ? " (escalation pool)" : "", bundle.Project.Id, dev.Id);
                        continue;
                    }
                    if (!_inFlight.TryAdd(dev.Id, 0))
                    {
                        _logger.LogDebug("Dispatch cycle: {Id} already in flight — skipping double-claim", dev.Id);
                        await slot.DisposeAsync();
                        continue;
                    }
                    _ = Task.Run(async () =>
                    {
                        await using (slot)
                        {
                            try { await DispatchSingleTaskAsync(dev, bundle, cancellationToken); }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Dispatch for {Id} faulted", dev.Id);
                            }
                            finally
                            {
                                _inFlight.TryRemove(dev.Id, out _);
                                // Run finished, slot freed — wake the
                                // loop immediately so queued work that
                                // was waiting on this role's pool
                                // dispatches without a backstop delay.
                                _wakeup?.Signal();
                            }
                        }
                    }, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dispatch cycle for project '{Id}' crashed; skipping this cycle for that project", bundle.Project.Id);
            }
        }
    }

    private ProjectDispatchBundle? GetOrCreateBundle(ProjectOptions project)
    {
        return _bundles.GetOrAdd(project.Id, _ =>
        {
            try
            {
                _logger.LogInformation("Constructing dispatch bundle for project '{Id}' (root={Root}, repo={Repo})",
                    project.Id, project.Root, project.RepoUrl);
                return _bundleFactory.Build(project);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build dispatch bundle for project '{Id}'", project.Id);
                return null!;
            }
        });
    }

    private static ProjectOptions ProjectRecordToOptions(ProjectRecord r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        RepoUrl = r.RepoUrl,
        DefaultBranch = r.DefaultBranch,
        Root = string.Empty,
        Roles = new Dictionary<string, int>(r.Roles, StringComparer.OrdinalIgnoreCase),
    };

    private static bool IsLlmRateLimited(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            // Typed 429 from the chat-client layer — classify without
            // depending on message text (message rewordings must never
            // silently turn a requeue into a task hard-fail).
            if (e is Agents.LlmRateLimitException) return true;
            if (e is System.ClientModel.ClientResultException cre && cre.Status == 429) return true;
            var msg = e.Message;
            if (!msg.Contains("429")) continue;
            if (msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Status: 429"))
                return true;
        }
        return false;
    }

    private static Agents.LlmRateLimitException? FindRateLimitException(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is Agents.LlmRateLimitException rl) return rl;
        }
        return null;
    }

    // Provider auth failure cooldown: much longer than the 429 one —
    // a lapsed key needs the operator, and each probe re-extends it.
    private static readonly TimeSpan LlmAuthFailureCooldown = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Provider-side auth failure (lapsed/revoked key, plan sign-in
    /// required). NOT the task's fault: the run never really started,
    /// so the failure must not consume the task's retry budget — the
    /// task requeues strike-free and dispatch on that model cools
    /// down (observed live 2026-07-29: a kilo-gateway 401 storm
    /// burned 16 porthorizon tasks to Failed in ~10 minutes).
    /// </summary>
    internal static bool IsLlmAuthFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            // 401/403 auth, 402 payment/credits — all provider account
            // state, all need the operator (observed live 2026-07-30:
            // kilo-gateway 402 "Add credits to continue" burned 18
            // sprint tasks overnight).
            if (e is System.ClientModel.ClientResultException cre && cre.Status is 401 or 402 or 403) return true;
            if (e.Message.Contains("PAID_MODEL_AUTH_REQUIRED", StringComparison.Ordinal)) return true;
            // The recorded lastError flattens the exception to a
            // string ("ClientResultException: HTTP 402 (: ) Add
            // credits...") — the typed check can't see it there.
            if (e.Message.Contains("HTTP 402", StringComparison.Ordinal)
                || e.Message.Contains("Add credits", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }


    public async Task<Result> DispatchSingleTaskAsync(IssueRecord issue, ProjectDispatchBundle bundle, CancellationToken cancellationToken)
    {
        // Concurrency is owned by the caller: the dispatch loop holds
        // a per-role SlotTable slot for the whole run.
        var startedAt = DateTime.UtcNow;
        // Phase 3: single-shot triage escalation consume — BEFORE the
        // claim re-fetch so the stamped metadata rides the workflow
        // input into RunAgentExecutor, and before any failure-path
        // cooldown recording (the escalated run cools the ESCALATED
        // model, never the normal one).
        var escalationKey = await ConsumeEscalationMarkerAsync(issue, bundle, cancellationToken);
        var runModelKey = escalationKey ?? ResolveModelKey(issue, bundle.Project.Id);
        if (escalationKey is null && issue.GetMetadata("modelEscalated") is not null)
        {
            // Stale-stamp guard: the escalation stamp is PER-RUN — a
            // later normal dispatch of the same task must not read a
            // previous run's stamp and escalate again.
            await UpdateMetadataAsync(issue.Id, m => MergeDict(m, new Dictionary<string, object>
            {
                ["modelEscalated"] = null!,
                ["modelEscalatedFrom"] = null!,
                ["modelEscalatedTo"] = null!,
                ["modelEscalationNote"] = null!,
            }), bundle, cancellationToken);
        }
        try
        {
            // P3 (final wiring): dispatch is now driven by the MAF
            // Workflows pipeline. ClaimExecutor detects the
            // pre-claim (InProgress + assignee=forge) and passes
            // through; otherwise it claims itself.
            // Report BEFORE the claim changes the record (derivation
            // must see the pre-claim state: Pending or ReworkQueued).
            await ReportLifecycleAsync(issue, Core.TaskEvent.Dispatched, bundle, cancellationToken);
            // Dispatch correlation id (v30): one id threads claim →
            // worktree → agent run → push → journal lines, so a
            // postmortem joins every artifact for this dispatch
            // without timestamp guesswork (operator 2026-08-01).
            // Persisted on the issue (the workflow input carries it
            // to RunAgentExecutor → the agent_run row) and scoped
            // onto every log line this dispatch writes.
            var dispatchId = "d-" + Guid.NewGuid().ToString("N")[..8];
            {
                var claimedMeta = new Dictionary<string, object>();
                var existingMeta = (await bundle.IssueStore.GetAsync(issue.Id, cancellationToken))?.MetadataJson;
                if (!string.IsNullOrWhiteSpace(existingMeta))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(existingMeta);
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        // Store convention: strings as plain values
                        // (GetRawText would double-encode them and
                        // break int.TryParse readers like
                        // reworkAttempts — observed in
                        // DispatchSingleTask_ReworkRound_ tests);
                        // JSON null is the delete idiom — skip it.
                        if (p.Value.ValueKind == System.Text.Json.JsonValueKind.Null) continue;
                        claimedMeta[p.Name] = p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                            ? p.Value.GetString()! : p.Value.GetRawText();
                    }
                }
                claimedMeta["dispatchId"] = dispatchId;
                await bundle.IssueStore.TransitionAsync(issue.Id, issue.Status, error: null, metadata: claimedMeta, ct: cancellationToken);
            }
            using var dispatchScope = _logger.BeginScope("DispatchId={DispatchId}", dispatchId);
            // Claim identity = the owning role (coredev/clientdev/
            // qa), not the opaque literal "forge" — the board shows
            // the assignee chip, and "forge" on a PortHorizon card
            // reads as the wrong project (operator 2026-08-01).
            var claimed = await bundle.IssueStore.ClaimAsync(issue.Id, ResolveRoleName(issue.Type), cancellationToken);
            if (claimed is null)
            {
                _logger.LogDebug("Issue {Id} already claimed elsewhere", issue.Id);
                return new Result(false, "already-claimed");
            }
            await PublishTransition(claimed, IssueStatus.Pending, IssueStatus.InProgress, null, cancellationToken);

            // Re-fetch after the claim/transition so the workflow's
            // input has InProgress + assignee=forge (ClaimExecutor
            // short-circuits on that combination).
            var preClaimed = (await bundle.IssueStore.GetAsync(claimed.Id, cancellationToken))!;

            // P4 Stage B: the dispatcher abstracts over InProcess
            // (current behavior) vs Durable (DTS-backed). Both
            // block until the workflow run reaches a terminal
            // state so the caller can keep its synchronous
            // dispatch-then-check shape.
            try
            {
                await _dispatcher.DispatchAsync(preClaimed, bundle, cancellationToken);
            }
            catch (Exception ex)
            {
                if (IsLlmAuthFailure(ex))
                {
                    var mk = runModelKey;
                    _modelCooldowns.RecordRateLimit(mk.Provider, mk.Model, LlmAuthFailureCooldown);
                    _logger.LogWarning("Issue {Id}: LLM auth failure on {Provider}/{Model}; re-queued strike-free, dispatch on that model cooling down for {Cooldown} — operator must restore provider auth",
                        preClaimed.Id, mk.Provider, mk.Model, LlmAuthFailureCooldown);
                    await SafeTransitionAsync(preClaimed.Id, IssueStatus.Pending, "llm-auth", bundle, cancellationToken);
                    return new Result(false, "llm-auth-failure");
                }
                if (IsLlmRateLimited(ex))
                {
                    var mk = runModelKey;
                    // Max-semantics: never shortens a longer
                    // escalating cooldown the client layer recorded
                    // (the 2026-08-08 herd bug flattened them to 3m).
                    _modelCooldowns.RecordRateLimit(mk.Provider, mk.Model, LlmRateLimitCooldown);
                    var effective = _modelCooldowns.CoolingDownUntil(mk.Provider, mk.Model);
                    var rl = FindRateLimitException(ex);
                    _logger.LogWarning(
                        "Issue {Id}: LLM rate limit (429{Kind}{Code}) on {Provider}/{Model}; re-queued, dispatch cooling until {Until:HH:mm:ss} UTC{ReqId}",
                        preClaimed.Id,
                        rl is not null ? $" {rl.Kind}" : "",
                        rl?.ErrorCode is not null ? $" code {rl.ErrorCode}" : "",
                        mk.Provider, mk.Model,
                        effective ?? DateTime.UtcNow + LlmRateLimitCooldown,
                        rl?.RequestId is not null ? $" request_id={rl.RequestId}" : "");
                    await SafeTransitionAsync(preClaimed.Id, IssueStatus.Pending, "llm-429", bundle, cancellationToken);
                    return new Result(false, "llm-rate-limited");
                }
                _logger.LogError(ex, "Workflow dispatch for {Id} threw", preClaimed.Id);
                await ReportLifecycleAsync(preClaimed, Core.TaskEvent.RunDied, bundle, cancellationToken);
                await HandleFailureAsync(preClaimed, ex, bundle, cancellationToken);
                return new Result(false, ex.Message);
            }

            // Inspect the issue post-workflow to construct the
            // Result message (preserves the old sequential contract).
            var after = await bundle.IssueStore.GetAsync(preClaimed.Id, cancellationToken);
            var lastError = after?.GetMetadata("lastError");
            // lastError is written by RunAgentExecutor on a failed
            // run and NEVER cleared by requeues — so a value from an
            // EARLIER dispatch must not fail THIS one (observed live:
            // task-153 completed as a verified no-op, then flipped to
            // Failed over a stale error from yesterday's run). Only a
            // failure stamped during this dispatch counts.
            var lastErrorFresh =
                DateTimeOffset.TryParse(after?.GetMetadata("lastErrorAt"), out var lea)
                && lea.UtcDateTime >= startedAt.AddSeconds(-2);
            if (!string.IsNullOrEmpty(lastError) && !lastErrorFresh)
            {
                _logger.LogInformation("Issue {Id}: ignoring stale lastError from {At} (this dispatch started {Started:O})",
                    preClaimed.Id, after?.GetMetadata("lastErrorAt"), startedAt);
                await UpdateMetadataAsync(preClaimed.Id, m =>
                {
                    // metadata is upsert-merge only: JSON null is the
                    // delete idiom (GetMetadata surfaces it as empty).
                    m["lastError"] = null!;
                    m["lastErrorAt"] = null!;
                    return m;
                }, bundle, cancellationToken);
                lastError = null;
            }
            if (!string.IsNullOrEmpty(lastError))
            {
                // A recorded 429 with a completed PR is noise: the
                // agent's LLM call rate-limited mid-conversation but
                // the workflow still committed + pushed + opened the
                // PR. Never requeue those — that would redispatch
                // finished work (observed live: two tasks requeued
                // with PRs #6/#7 already open). Also never requeue a
                // task that is ALREADY terminal: a stale long-running
                // dispatch can finish minutes after the watch merged
                // its PR (observed live 2026-07-23: the 18-minute
                // rework run for task-10 finished after the merge and
                // the 429 path flipped Completed back to Pending,
                // leaving a completed sprint with a todo task).
                var reachedPr = after?.DispatchCheckpoint >= DispatchCheckpoint.PrOpened;
                var alreadyTerminal = after?.Status is IssueStatus.Completed or IssueStatus.Failed or IssueStatus.Closed;
                if (IsLlmAuthFailure(new InvalidOperationException(lastError)) && !reachedPr && !alreadyTerminal)
                {
                    var mk = runModelKey;
                    _modelCooldowns.RecordRateLimit(mk.Provider, mk.Model, LlmAuthFailureCooldown);
                    _logger.LogWarning("Issue {Id}: LLM auth failure on {Provider}/{Model}; re-queued strike-free, dispatch on that model cooling down for {Cooldown} — operator must restore provider auth",
                        preClaimed.Id, mk.Provider, mk.Model, LlmAuthFailureCooldown);
                    await SafeTransitionAsync(preClaimed.Id, IssueStatus.Pending, "llm-auth", bundle, cancellationToken);
                    return new Result(false, "llm-auth-failure");
                }
                if (IsLlmRateLimited(new InvalidOperationException(lastError)) && !reachedPr && !alreadyTerminal)
                {
                    var mk = runModelKey;
                    _modelCooldowns.RecordRateLimit(mk.Provider, mk.Model, LlmRateLimitCooldown);
                    var effective = _modelCooldowns.CoolingDownUntil(mk.Provider, mk.Model);
                    var kind = Agents.LlmRateLimitException.Classify(lastError);
                    var code = Agents.LlmRateLimitException.ExtractErrorCode(lastError);
                    _logger.LogWarning(
                        "Issue {Id}: LLM rate limit (429 {Kind}{Code}) on {Provider}/{Model}; re-queued, dispatch cooling until {Until:HH:mm:ss} UTC",
                        preClaimed.Id, kind,
                        code is not null ? $" code {code}" : "",
                        mk.Provider, mk.Model,
                        effective ?? DateTime.UtcNow + LlmRateLimitCooldown);
                    await SafeTransitionAsync(preClaimed.Id, IssueStatus.Pending, "llm-429", bundle, cancellationToken);
                    return new Result(false, "llm-rate-limited");
                }
                _logger.LogWarning("Workflow dispatch for {Id} reported failure: {Err}",
                    preClaimed.Id, lastError);
                var ex = new InvalidOperationException(lastError);
                await ReportLifecycleAsync(preClaimed, Core.TaskEvent.RunDied, bundle, cancellationToken);
                await HandleFailureAsync(preClaimed, ex, bundle, cancellationToken);
                return new Result(false, lastError);
            }
            var prNumber = after?.GetMetadata("prNumber");
            // Mid-pipeline halt detection MUST come before the
            // prNumber success return: a REWORK round already carries
            // prNumber from the previous round, so a workflow that
            // faulted silently (MAF InProcessExecution swallows
            // executor faults — the run just halts) would read as
            // "PR opened" success and the round would never actually
            // run (observed live 2026-08-01: task-18/364 "dispatch
            // completed in ~600ms" with zero executor logs, stall
            // guard re-firing strikes against rounds that never
            // happened). The checkpoint is the discriminator, and it
            // differs per dispatch kind: a FRESH dispatch succeeds at
            // PrOpened, so anything earlier is a halt; a REWORK round
            // succeeds by pushing to the EXISTING PR (checkpoint
            // PushDone — no PR-open step), so a stop before PushDone
            // is a halt. (CommitDone was too lax: a swallowed push
            // fault halts between CommitDone and PushDone — observed
            // live 2026-08-01: task-377's silent non-fast-forward
            // rejections read as "completed".)
            var halted = after?.Status == IssueStatus.InProgress
                && after.DispatchCheckpoint is not null
                && (string.IsNullOrEmpty(prNumber)
                    ? after.DispatchCheckpoint < DispatchCheckpoint.PrOpened
                    : after.DispatchCheckpoint < DispatchCheckpoint.PushDone);
            if (halted)
            {
                var msg = $"workflow halted mid-pipeline at checkpoint {after!.DispatchCheckpoint} without surfacing an error";
                _logger.LogWarning("Workflow dispatch for {Id}: {Msg}", preClaimed.Id, msg);
                await ReportLifecycleAsync(preClaimed, Core.TaskEvent.RunDied, bundle, cancellationToken);
                await HandleFailureAsync(preClaimed, new InvalidOperationException(msg), bundle, cancellationToken);
                return new Result(false, msg);
            }
            _logger.LogInformation("Workflow dispatch for {Id} completed in {Ms}ms (status={Status} prNumber={Pr})",
                preClaimed.Id, (DateTime.UtcNow - startedAt).TotalMilliseconds, after?.Status, prNumber);
            if (!string.IsNullOrEmpty(prNumber))
            {
                await ReportLifecycleAsync(after ?? preClaimed, Core.TaskEvent.PrOpened, bundle, cancellationToken);
                return new Result(true, $"PR #{prNumber} opened");
            }
            if (after?.Status == IssueStatus.Completed)
            {
                await ReportLifecycleAsync(after, Core.TaskEvent.RunCompletedNoDiff, bundle, cancellationToken);
                return new Result(true, "completed with no diff");
            }
            // (halt guard moved above — see comment there)
            return new Result(true, "workflow completed");
        }
        catch (OperationCanceledException)
        {
            await ReportLifecycleAsync(issue, Core.TaskEvent.RunDied, bundle, cancellationToken);
            await SafeTransitionAsync(issue.Id, IssueStatus.Failed, "cancelled", bundle, cancellationToken);
            return new Result(false, "cancelled");
        }
    }

    /// <summary>Phase 2 shadow authority: report an observed dispatch
    /// event to the lifecycle machine. Best-effort — never breaks a
    /// dispatch.</summary>
    private async Task ReportLifecycleAsync(
        IssueRecord task, Core.TaskEvent evt, ProjectDispatchBundle bundle, CancellationToken ct)
    {
        if (_lifecycle is null) return;
        try
        {
            // Derivation input: the machine re-reads the task itself;
            // the watch is not loaded here (null is fine — watch-side
            // states are driven by PRWatcher's own reports).
            var fresh = await bundle.IssueStore.GetAsync(task.Id, ct) ?? task;
            await _lifecycle.ReportAsync(bundle.IssueStore, fresh, evt, watch: null, hasActiveDevRun: false, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "lifecycle report {Event} failed for {TaskId}; continuing", evt, task.Id);
        }
    }

    internal void BindOptions(AgentOptions options)
    {
        _spawnerOptions = options.Spawner;
        // Provider+model map for per-model 429 cooldowns. Only names
        // are used (cooldown keys) — secret resolution stays with the
        // real chat-client factory. Invalid/absent config degrades to
        // a single "default" bucket (the old global-cooldown behavior).
        try { _llmConfig = Agents.LlmConfigAdapter.FromOptions(options.Llm); }
        catch (Exception ex)
        {
            _llmConfig = null;
            _logger.LogWarning("LLM role-model config unusable ({Err}) — per-model 429 cooldowns degrade to a single global bucket", ex.Message);
        }
    }

    /// <summary>DB model overrides (per project → global), bound at
    /// startup — the cooldown key must be the EFFECTIVE model for the
    /// task's project or a cooldown meant for one project's override
    /// skips another project's tasks (operator rule 2026-07-30).</summary>
    public Agents.RoleModelOverrides? ModelOverrides { get; set; }

    /// <summary>Single-shot triage escalation markers (phase 3),
    /// bound at startup. A task carrying a marker runs its next dev
    /// run on the role's ESCALATION model: the cooldown key is the
    /// escalated model and the run draws on the (project,
    /// escalated-role) slot pool instead of the normal role pool.</summary>
    public Agents.TaskModelEscalations? TaskEscalations { get; set; }

    /// <summary>The escalation pool rides the SAME (project, role)
    /// semaphore machinery as normal role pools — the role dimension
    /// is just prefixed. Kept as a distinct prefix (not a bare role
    /// name) so a future (project, model) bucket layer composes as a
    /// key-shape change, and so the normal pool's accounting is never
    /// touched by escalated runs.</summary>
    internal static string EscalationPoolRole(string role) => "escalated:" + role;

    /// <summary>Concurrency cap for escalated runs: 1 per
    /// (project, role) (operator decision 2026-08-23 — the bound
    /// replaces count budgets).</summary>
    internal const int EscalationSlotCap = 1;

    /// <summary>Resolve the role's ESCALATION (provider, model) for a
    /// task, when one is configured: project escalation override →
    /// global escalation override → llm.roles.&lt;AgentType&gt;.
    /// escalationModel. False when unset or unusable — explicit-only
    /// escalation, never a provider-default fallback.</summary>
    private bool TryResolveEscalationModel(string taskType, string? projectId, out (string Provider, string Model) key)
    {
        key = default;
        if (_llmConfig is null) return false;
        try
        {
            var resolved = _llmConfig.ResolveEscalationEffective(
                RoleAgentRegistry.FromTaskType(taskType), ModelOverrides, projectId);
            if (resolved is null) return false;
            key = (resolved.Value.Provider.Name, resolved.Value.Model);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolve the (provider, model) a task would run on — the cooldown
    /// key. A task carrying an unconsumed escalation marker resolves to
    /// the ESCALATED model. Falls back to ("default","default") when no
    /// LLM config is bound (tests) or the role's entry is broken, which
    /// reproduces the pre-per-model global cooldown for that bucket.
    /// </summary>
    private (string Provider, string Model) ResolveModelKey(IssueRecord task, string? projectId)
    {
        if (_llmConfig is null) return ("default", "default");
        try
        {
            // A task carrying an unconsumed escalation marker cools
            // against the ESCALATED model — a 429 on the premium
            // model must not cool the normal one, and vice versa.
            if (projectId is not null
                && TaskEscalations is not null
                && TaskEscalations.Peek(projectId, task.Id)
                && TryResolveEscalationModel(task.Type, projectId, out var escalated))
            {
                return escalated;
            }
            var (provider, model, _) = _llmConfig.ResolveEffective(
                RoleAgentRegistry.FromTaskType(task.Type), ModelOverrides, projectId);
            return (provider.Name, model);
        }
        catch (InvalidOperationException)
        {
            return ("default", "default");
        }
    }

    /// <summary>
    /// Single-shot consume of the task's escalation marker (phase 3):
    /// the key is deleted even when the run later fails (no refund —
    /// the failure re-enters triage and the agent may re-escalate).
    /// Returns the escalated (provider, model) the run must ride, or
    /// null for a normal run. Run metadata (modelEscalated, from→to)
    /// is stamped here so the workflow input (re-fetched after this)
    /// carries the escalation into RunAgentExecutor → the runner.
    /// </summary>
    private async Task<(string Provider, string Model)?> ConsumeEscalationMarkerAsync(
        IssueRecord issue, ProjectDispatchBundle bundle, CancellationToken ct)
    {
        if (TaskEscalations is null) return null;
        var marker = await TaskEscalations.ConsumeAsync(bundle.Project.Id, issue.Id, ct);
        if (marker is null) return null;
        if (!TryResolveEscalationModel(issue.Type, bundle.Project.Id, out var escalated))
        {
            _logger.LogWarning(
                "Issue {Id}: triage escalation marker consumed but no escalation model is configured for the role — running on the normal model (marker is single-shot, no refund)",
                issue.Id);
            return null;
        }
        var normal = ResolveModelKey(issue, bundle.Project.Id);
        await UpdateMetadataAsync(issue.Id, m => MergeDict(m, new Dictionary<string, object>
        {
            ["modelEscalated"] = "true",
            ["modelEscalatedFrom"] = normal.Provider + "/" + normal.Model,
            ["modelEscalatedTo"] = escalated.Provider + "/" + escalated.Model,
            ["modelEscalationNote"] = marker.Note,
        }), bundle, ct);
        _logger.LogInformation(
            "Issue {Id}: model escalation consumed — {From} → {To} (triage note: {Note})",
            issue.Id, normal.Provider + "/" + normal.Model,
            escalated.Provider + "/" + escalated.Model, marker.Note);
        return escalated;
    }

    /// <summary>Task type → slot-pool role name (coredev/clientdev/qa/reviewer).</summary>
    private string ResolveRoleName(string taskType)
        => _roleRegistry.ForType(RoleAgentRegistry.FromTaskType(taskType)).AgentName;

    /// <summary>
    /// Lazily configure a project's role pool from its persisted role
    /// caps (falling back to <see cref="DefaultProjectRoles"/>). Startup
    /// and the roles API pre-configure; this covers runtime-added
    /// projects and ad-hoc role names (e.g. "qa") so TryAcquire never
    /// throws on an unconfigured pool.
    /// </summary>
    private void EnsureSlotCap(Configuration.ProjectOptions project, string role)
    {
        if (_slots.MaxFor(project.Id, role) == 0)
            _slots.Configure(project.Id, role, DefaultProjectRoles.MaxFor(project.Roles, role));
    }

    private async Task HandleFailureAsync(IssueRecord issue, Exception ex, ProjectDispatchBundle bundle, CancellationToken cancellationToken)
    {
        var retryCount = 0;
        var prev = issue.GetMetadata("retryCount");
        if (prev is not null && int.TryParse(prev, out var r)) retryCount = r;
        var worktreePath = issue.GetMetadata("worktreePath");

        if (retryCount < _maxRetryCount)
        {
            await UpdateMetadataAsync(issue.Id, m => MergeDict(m, new Dictionary<string, object>
            {
                ["retryCount"] = retryCount + 1
            }), bundle, cancellationToken);
            await SafeTransitionAsync(issue.Id, IssueStatus.Pending, ex.Message, bundle, cancellationToken);
            _logger.LogWarning("Issue {Id} will be retried (attempt {N})", issue.Id, retryCount + 1);
        }
        else
        {
            await SafeTransitionAsync(issue.Id, IssueStatus.Failed, ex.Message, bundle, cancellationToken);
            if (!string.IsNullOrEmpty(worktreePath))
            {
                try { await bundle.Worktrees.RemoveAsync(issue.Id, cancellationToken); }
                catch (Exception wx) { _logger.LogWarning(wx, "Worktree removal failed"); }
            }
        }
    }

    private async Task RecordModelResponseMetadataAsync(string id, string? response, string? error, ProjectDispatchBundle bundle, CancellationToken ct = default)
    {
        try
        {
            var current = await bundle.IssueStore.GetAsync(id, ct);
            if (current is null) return;
            await bundle.IssueStore.TransitionAsync(id, current.Status,
                error: error ?? current.GetMetadata("lastError"),
                metadata: new Dictionary<string, object>
                {
                    ["modelResponse"] = Truncate(response ?? "", 2000),
                    ["lastError"] = error ?? current.GetMetadata("lastError") ?? "",
                },
                ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record modelResponse metadata for {Id}", id);
        }
    }

    private async Task UpdateMetadataAsync(string id, Func<Dictionary<string, object>, Dictionary<string, object>> mutate, ProjectDispatchBundle bundle, CancellationToken ct)
    {
        var current = await bundle.IssueStore.GetAsync(id, ct);
        if (current is null) return;
        using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrEmpty(current.MetadataJson) ? "{}" : current.MetadataJson);
        var dict = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
            dict[prop.Name] = System.Text.Json.JsonSerializer.Deserialize<object>(prop.Value.GetRawText())!;
        var merged = mutate(dict);
        await bundle.IssueStore.TransitionAsync(id, current.Status, current.GetMetadata("lastError"),
            metadata: merged, ct: ct);
    }

    private async Task SafeTransitionAsync(string id, IssueStatus to, string? error, ProjectDispatchBundle bundle, CancellationToken ct)
    {
        try
        {
            // Terminal rows are immutable to late dispatches: a zombie
            // run reaped after the watch already closed the loop must
            // not flip a Completed task back to Failed (observed live
            // 2026-08-18: task-653 — the watch's empty-diff supersede
            // completed it at 11:32; the still-running dispatch's
            // RunDied at 11:45 flipped it to Failed and blocked the
            // sprint's completion for hours).
            var current = await bundle.IssueStore.GetAsync(id, ct);
            if (current?.Status is IssueStatus.Completed or IssueStatus.Closed
                && to is not (IssueStatus.Completed or IssueStatus.Closed))
            {
                _logger.LogWarning(
                    "Transition {Id} {From} -> {To} refused: terminal rows are immutable to late dispatches",
                    id, current.Status, to);
                return;
            }
            await bundle.IssueStore.TransitionAsync(id, to, error, ct: ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Transition failed for {Id}", id); }
    }

    private async Task PublishTransition(IssueRecord issue, IssueStatus from, IssueStatus to, string? error, CancellationToken ct)
    {
        _logger.LogInformation("Issue {Id} transition {From} -> {To} (type={Type})", issue.Id, from, to, issue.Type);
        _events.Publish(new DashboardEvent(DateTime.UtcNow, DashboardEventKind.TaskTransition,
            issue.Id, $"{from} -> {to}",
            new Dictionary<string, object?>
            {
                ["from"] = from.ToString(),
                ["to"] = to.ToString(),
                ["type"] = issue.Type,
                ["error"] = error
            }));
    }

    private static Dictionary<string, object> MergeDict(
        Dictionary<string, object> existing,
        IReadOnlyDictionary<string, object> additions)
    {
        var merged = new Dictionary<string, object>(existing, StringComparer.Ordinal);
        foreach (var kv in additions)
            merged[kv.Key] = kv.Value;
        return merged;
    }

    internal static string BuildPrompt(IssueRecord issue, RoleAgent role, string worktreePath, string branch, string? defaultBranch)
        => $"""
            You are acting as the **{role.AgentName}** agent for the PortHorizon project.
            Working directory: {worktreePath}
            Branch: {branch} (base: {defaultBranch ?? "main"})

            ## Task
            Type: {issue.Type}
            Id: {issue.Id}
            Title: {issue.Title}

            ## Allowed tools
            {string.Join(", ", role.AllowedTools)}

            ## Rules
            - Make focused, minimal changes that fulfill the task description.
            - Run `dotnet build` and `dotnet test` on the projects you touch before committing.
            - Commit your work with message: `Task({issue.Id}): <summary>`.
            - Push the branch when done.
            - Do NOT open a PR; the orchestrator handles that.
            - Do NOT touch files outside your project subdirectory ({role.ProjectSubdir}).
            """;

    internal static string BuildPrBody(IssueRecord issue, RoleAgent role, string sha, string response)
        => $"""
            ## Summary
            Automated change for issue `{issue.Id}` (type: {issue.Type}, role: {role.AgentName}).

            ## Description
            {issue.Description}

            ## Verification
            - HEAD SHA: `{sha}`
            - ACP session result (truncated): `{Truncate(response, 400)}`

            Closes `{issue.Id}`.
            """;

    internal static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

}






