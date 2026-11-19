using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.AgentTools;
using Forge.Core;
using Forge.Core.Completion;
using Microsoft.Extensions.Logging;
using Octokit;

namespace Forge.Orchestrator.Workers;

/// <summary>Adopts a clean upstream integration only after isolated, zero-model exact-head verification.</summary>
public sealed class IntegratedHeadVerifier(IIssueStore issues, GitWorktreeService worktrees, GitHubService github,
    KubernetesAgentRunner workers, string projectId, IReadOnlyList<string>? verifyCommands, ILogger<IntegratedHeadVerifier> logger)
{
    private sealed record Pending(string Head, string Dispatch, string Path, string Original, string Commands);
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private const string PendingKey = "reviewIntegrationVerificationPending";

    public async Task<bool> EnsureAsync(IssueRecord task, string? expectedHead, CancellationToken ct)
    {
        if (task.GetMetadata("trustedPublication") is null) return true; // Preserve legacy callers without inventing evidence.
        TrustedPublicationEvidence? initial;
        try { initial = JsonSerializer.Deserialize<TrustedPublicationEvidence>(task.GetMetadata("trustedPublication")!); }
        catch (JsonException ex) { logger.LogWarning(ex, "Unreadable original publication for {Task}", task.Id); return false; }
        if (initial?.TaskId == task.Id && initial.Verification is not null && expectedHead == initial.PublishedHeadSha
            && task.GetMetadata("branchSha") == expectedHead) return true;
        var gate = Gates[SHA256.HashData(Encoding.UTF8.GetBytes(projectId + "/" + task.Id))[0] % Gates.Length];
        if (!await gate.WaitAsync(0, ct)) return false;
        try
        {
            task = await issues.GetAsync(task.Id, ct) ?? throw new InvalidOperationException("Task disappeared.");
            var original = JsonSerializer.Deserialize<TrustedPublicationEvidence>(task.GetMetadata("trustedPublication") ?? "null")
                ?? throw new InvalidOperationException("Original publication is unreadable.");
            if (original.TaskId != task.Id || original.Verification is null
                || task.GetMetadata("branchSha") != original.PublishedHeadSha
                || !int.TryParse(task.GetMetadata("prNumber"), out var number))
                throw new InvalidOperationException("Task publication binding is incomplete.");
            if (expectedHead == original.PublishedHeadSha) return true;
            var pr = await github.GetPullRequestAsync(number, ct);
            var head = pr.Head?.Sha ?? throw new InvalidOperationException("PR head is unavailable.");
            Validate(pr, task, expectedHead);
            if (head == original.PublishedHeadSha) return true;
            var resolvedCommands = verifyCommands ?? await worktrees.DefaultVerificationCommandsAtHeadAsync(head, ct);
            var commands = WorkerVerification.CommandsHash(resolvedCommands);
            if (resolvedCommands.Count == 0 || original.Verification.CommandsSha != commands)
                throw new InvalidOperationException("Current verification profile differs from the original publication; independent reconciliation is required.");
            if (TaskDeliveryEvidence.Integration(task, head) is { } existing
                && existing.ProjectId == projectId && string.Equals(existing.Repository, github.Owner + "/" + github.Repo, StringComparison.OrdinalIgnoreCase)
                && existing.Verification.CommandsSha == commands
                && issues is IssueStore authorityStore
                && (await new MemoryStore(authorityStore.Db).GetAsync(TaskDeliveryEvidence.IntegrationAuthorityKey(projectId, task.Id, head), ct))?.Body == JsonSerializer.Serialize(existing))
            {
                var leftover = JsonSerializer.Deserialize<Pending>(task.GetMetadata(PendingKey) ?? "null");
                if (leftover is not null)
                {
                    var prior = await workers.InspectReviewWorkerAsync(AgentType.QA, projectId, task.Id, leftover.Dispatch, false, ct);
                    if (prior is { Terminal: false }) return false;
                    await RetireAsync(task.Id, leftover, ct);
                    await PatchAsync(task.Id, new() { [PendingKey] = null! }, ct);
                }
                return true;
            }
            var lineage = await worktrees.VerifyCleanIntegrationAsync(original.PublishedHeadSha, head, original.Verification.TreeSha, ct, pr.Merged ? pr.MergeCommitSha : null);
            var pending = JsonSerializer.Deserialize<Pending>(task.GetMetadata(PendingKey) ?? "null");
            if (pending is not null && (pending.Head != head || pending.Original != original.PublishedHeadSha || pending.Commands != commands))
            {
                var prior = await workers.InspectReviewWorkerAsync(AgentType.QA, projectId, task.Id, pending.Dispatch, false, ct);
                if (prior is { Terminal: false }) return false;
                await RetireAsync(task.Id, pending, ct);
                pending = null;
            }
            if (pending is null)
            {
                var dispatch = "integration-verify-" + Guid.NewGuid().ToString("N");
                pending = new(head, dispatch, worktrees.ReviewSnapshotPath(task.Id, dispatch), original.PublishedHeadSha, commands);
                await PatchAsync(task.Id, new() { [PendingKey] = JsonSerializer.Serialize(pending) }, ct);
            }
            if (!worktrees.IsReviewSnapshotPath(pending.Path)
                || pending.Path != worktrees.ReviewSnapshotPath(task.Id, pending.Dispatch))
                throw new InvalidOperationException("Integration snapshot binding is invalid.");
            var priorWorker = await workers.InspectReviewWorkerAsync(AgentType.QA, projectId, task.Id, pending.Dispatch, false, ct);
            if (priorWorker is null && !Directory.Exists(pending.Path))
                await worktrees.CreateReviewSnapshotAsync(task.Id, pending.Dispatch, head, ct);
            if (priorWorker is null && await worktrees.GetHeadShaAsync(pending.Path, ct) != head)
                throw new InvalidOperationException("Integration snapshot does not match its durable assignment.");
            var context = new Dictionary<string, object>
            { ["projectId"] = projectId, ["issueId"] = task.Id, ["dispatchId"] = pending.Dispatch,
              ["worktreePath"] = pending.Path, ["sourceSha"] = head };
            var result = await workers.StartOrObserveVerificationAsync(context, ct);
            if (result is null) return false;
            var receipt = result.Verification ?? throw new InvalidOperationException("Integration worker returned no verification receipt.");
            if (receipt.CommandsSha != commands || receipt.TreeSha != lineage.Tree
                || !await WorkerVerification.MatchesAsync(receipt, pending.Path, resolvedCommands, ct, worktrees.WorkspaceRoot))
                throw new InvalidOperationException("Integration verification content or commands changed.");
            var current = await github.GetPullRequestAsync(number, ct);
            Validate(current, task, head);
            if (issues is not IssueStore store) throw new InvalidOperationException("Integration evidence requires canonical issue admission.");
            await store.AdmitIntegrationVerificationAsync(task.Id, new(task.Id, projectId, github.Owner + "/" + github.Repo,
                number, original.PublishedHeadSha, head, original.Verification.AttemptId, lineage.BaseParent, receipt), ct);
            await RetireAsync(task.Id, pending, ct);
            await PatchAsync(task.Id, new() { [PendingKey] = null! }, ct);
            return true;
        }
        catch (OperationCanceledException) { throw; } // Durable assignment remains adoptable.
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Integrated-head verification pending for {Project}/{Task}", projectId, task.Id);
            await PatchAsync(task.Id, new() { ["reviewIntegrationVerificationError"] = ex.Message }, ct);
            return false; // Same durable assignment is retained; no unbounded new Jobs.
        }
        finally { gate.Release(); }
    }

    private void Validate(PullRequest pr, IssueRecord task, string? expected)
    {
        var repository = github.Owner + "/" + github.Repo;
        if (pr.Head is null || pr.Base is null || pr.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) != task.GetMetadata("prNumber")
            || !string.Equals(pr.Head.Repository?.FullName, repository, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(pr.Base.Repository?.FullName, repository, StringComparison.OrdinalIgnoreCase)
            || pr.Head.Ref != task.GetMetadata("branch") || pr.Base.Ref != worktrees.DefaultBranch
            || expected is not null && pr.Head.Sha != expected
            || pr.State.Value != ItemState.Open && !pr.Merged)
            throw new InvalidOperationException("Integration PR does not match the owning repository, branch or expected revision.");
        if (pr.Merged)
        {
            var merge = JsonSerializer.Deserialize<ObservedMergeEvidence>(task.GetMetadata("observedMerge") ?? "null");
            if (merge?.TaskId != task.Id || merge.PullRequestNumber != pr.Number || merge.HeadSha != pr.Head.Sha || merge.MergeSha != pr.MergeCommitSha)
                throw new InvalidOperationException("Merged integration does not match the task's recorded merge evidence.");
        }
    }

    private async Task RetireAsync(string taskId, Pending pending, CancellationToken ct)
    {
        if (!worktrees.IsReviewSnapshotPath(pending.Path) || pending.Path != worktrees.ReviewSnapshotPath(taskId, pending.Dispatch))
            throw new InvalidOperationException("Refusing retirement of an unrelated integration snapshot.");
        var task = await issues.GetAsync(taskId, ct) ?? throw new InvalidOperationException("Task disappeared during retirement.");
        var saved = task.GetMetadata("reviewIntegrationVerificationRetirement");
        var retirement = saved is null ? worktrees.PrepareReviewSnapshotRetirement(pending.Path)
            : JsonSerializer.Deserialize<ReviewSnapshotRetirement>(saved)
                ?? throw new InvalidOperationException("Integration snapshot retirement is invalid.");
        if (saved is null && retirement is not null)
            await PatchAsync(taskId, new() { ["reviewIntegrationVerificationRetirement"] = JsonSerializer.Serialize(retirement) }, ct);
        await worktrees.RemoveReviewSnapshotAsync(pending.Path, ct, retirement);
        await PatchAsync(taskId, new() { ["reviewIntegrationVerificationRetirement"] = null! }, ct);
    }

    private Task PatchAsync(string id, Dictionary<string, object> fields, CancellationToken ct) => issues is IssueStore concrete
        ? concrete.PatchReviewMetadataAsync(id, fields, null, ct)
        : throw new InvalidOperationException("Integration verification requires canonical metadata admission.");
}
