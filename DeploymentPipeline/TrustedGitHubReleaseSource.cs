using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.Configuration;
using Forge.Core;
using Forge.Core.Completion;

namespace Forge.Deploy;

public sealed record TrustedImageReceipt(string SourceSha, string ImageDigest, bool BuildPassed, bool TestPassed,
    string Workflow, string WorkflowRunId, string Repository, string Branch, string ArtifactId, string ArtifactDigest,
    int SchemaVersion, string ValidationWorkflow, string ValidationRunId);

/// <summary>Trusted control-plane reads only. Repository content and artifact prose never authorize a release.</summary>
public sealed class TrustedGitHubReleaseSource(HttpClient api, HttpClient downloads, Func<ProjectOptions, CancellationToken, Task<string?>> token,
    Func<string?> runningRevision) : IRoutineReleaseSource, IDisposable
{
    // REST 2026-03-10 removes merge_commit_sha from PR responses. This reader
    // deliberately uses the supported 2022 contract for observed merged source;
    // PR head or current branch tip are not substitutes for a merged commit.
    // https://docs.github.com/en/rest/about-the-rest-api/breaking-changes?apiVersion=2026-03-10
    private const string MergedPullRequestApiVersion = "2022-11-28";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static bool Sha(string? value) => value is not null && Regex.IsMatch(value, "^[0-9a-f]{40}$");
    public void Dispose() { api.Dispose(); downloads.Dispose(); }

    public Task ValidateForwardAsync(ProjectOptions project, string sha, CancellationToken ct)
    {
        return ValidateForwardAsync(project, sha, runningRevision()?.Trim() ?? "", ct);
    }

    public Task ValidateForwardAsync(ProjectOptions project, string sha, string running, CancellationToken ct)
    {
        if (!Sha(running)) throw new InvalidOperationException("Running controller source revision is unavailable; routine deployment cannot prove forward ancestry.");
        return RequireAncestor(project, Repository(project), running!, sha, ct);
    }

    public static string Repository(ProjectOptions project)
    {
        if (!Uri.TryCreate(project.RepoUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "github.com" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Routine release requires an exact HTTPS github.com project repository.");
        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
        if (!Regex.IsMatch(path, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            throw new InvalidOperationException("Invalid project repository identity.");
        return path;
    }

    public Task<(string Sha, string Evidence)> ObserveMergedFrontierAsync(ProjectOptions project,
        IReadOnlyList<IssueRecord> members, CancellationToken ct)
        => ObserveFrontierAsync(project, members, null, runningRevision()?.Trim(), ct);

    public Task<(string Sha, string Evidence)> ObserveMergedFrontierAsync(ProjectOptions project,
        IReadOnlyList<IssueRecord> members, SprintPlan plan, CancellationToken ct)
        => ObserveFrontierAsync(project, members, plan, runningRevision()?.Trim(), ct);

    public Task<(string Sha, string Evidence)> ObserveMergedFrontierAsync(ProjectOptions project, IReadOnlyList<IssueRecord> members,
        SprintPlan plan, string servingRevision, CancellationToken ct) => ObserveFrontierAsync(project, members, plan, servingRevision, ct);

    private async Task<(string Sha, string Evidence)> ObserveFrontierAsync(ProjectOptions project,
        IReadOnlyList<IssueRecord> members, SprintPlan? plan, string? running, CancellationToken ct)
    {
        var repo = Repository(project);
        if (plan is not null && (plan.ProjectId != project.Id || !plan.HasValidHash
            || !plan.Members.Select(m => m.TaskId).ToHashSet(StringComparer.Ordinal).SetEquals(members.Select(m => m.Id))))
            throw new InvalidOperationException("Source qualification requires this project's exact completion plan and members.");
        var observations = new List<(string Sha, DateTimeOffset At, int Pr)>();
        var existing = new List<VerifiedExistingImplementationEvidence>();
        foreach (var member in members.Where(m => !AgentTaskTypes.IsContainer(m.Type) && m.Type != AgentTaskTypes.PrWatch))
        {
            if (plan is not null && ExistingImplementationEvidence.TryGet(member, plan, out var proof))
            {
                existing.Add(proof!);
                observations.Add((proof!.SourceSha, proof.Review.ReviewedAt, 0));
                continue;
            }
            if (member.GetMetadata(ExistingImplementationEvidence.MetadataKey) is not null && member.GetMetadata("prNumber") is null)
                throw new InvalidOperationException("Existing implementation source lacks exact-plan independent evidence.");
            // Non-code dispositions belong to the completion evaluator, not an invented source SHA.
            if (member.GetMetadata("prNumber") is not { } value) continue;
            if (!int.TryParse(value, out var number) || number <= 0) throw new InvalidOperationException("Invalid merged pull request evidence.");
            using var document = await GetJson(project, $"repos/{repo}/pulls/{number}", ct, MergedPullRequestApiVersion);
            var pr = document.RootElement;
            if (!pr.TryGetProperty("merge_commit_sha", out var mergeCommit)
                || mergeCommit.ValueKind != JsonValueKind.String || !Sha(mergeCommit.GetString()))
                throw new InvalidOperationException("GitHub merged pull request response lacks a valid full merge commit SHA under the qualified 2022-11-28 contract.");
            if (!pr.GetProperty("merged").GetBoolean() || pr.GetProperty("base").GetProperty("ref").GetString() != project.DefaultBranch ||
                !string.Equals(pr.GetProperty("base").GetProperty("repo").GetProperty("full_name").GetString(), repo, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Task pull request is not merged into the configured repository and branch.");
            observations.Add((mergeCommit.GetString()!, pr.GetProperty("merged_at").GetDateTimeOffset(), number));
        }
        if (observations.Count == 0) throw new InvalidOperationException("No merged source frontier is present; non-code completion needs an explicit no-deployment disposition.");
        var frontier = observations[0].Sha;
        foreach (var observation in observations.Skip(1))
        {
            if (frontier == observation.Sha) continue;
            using var comparison = await GetJson(project, $"repos/{repo}/compare/{frontier}...{observation.Sha}", ct);
            var relation = comparison.RootElement.GetProperty("status").GetString();
            if (relation == "ahead") frontier = observation.Sha;
            else if (relation is not ("behind" or "identical"))
                throw new InvalidOperationException("Merged source commits do not form one verified ancestry frontier.");
        }
        foreach (var observation in observations)
            await RequireAncestor(project, repo, observation.Sha, frontier, ct);
        if (!Sha(running))
            throw new InvalidOperationException("Running controller source revision is unavailable; routine deployment cannot prove forward ancestry.");
        using var branch = await GetJson(project, $"repos/{repo}/branches/{Uri.EscapeDataString(project.DefaultBranch)}", ct);
        var branchTip = branch.RootElement.GetProperty("commit").GetProperty("sha").GetString()!;
        await RequireAncestor(project, repo, frontier, branchTip, ct);

        // Prefer the minimal member frontier. A repaired controller may instead
        // require a shared descendant to avoid rolling its repair back. Only the
        // configured branch can supply that candidate; unavailable ancestry is
        // an error, never permission to select a different source.
        var selected = frontier;
        var reason = "member-frontier";
        if (!await IsAncestorAsync(project, repo, running!, frontier, ct))
        {
            await RequireAncestor(project, repo, running!, branchTip, ct);
            foreach (var observation in observations)
                await RequireAncestor(project, repo, observation.Sha, branchTip, ct);
            selected = branchTip;
            reason = "running-source-forward-recovery";
        }
        return (selected, JsonSerializer.Serialize(new { repository = repo, branch = project.DefaultBranch,
            memberFrontier = frontier, runningSource = running, selectedSource = selected, selectionReason = reason,
            merged = observations.Where(o => o.Pr > 0).Select(o => new { pullRequest = o.Pr, sourceSha = o.Sha, mergedAt = o.At }),
            verifiedExisting = existing.Select(e => new { e.TaskId, e.PlanHash, e.SourceSha, e.Verification, e.Review }) }, Json));
    }

    private async Task RequireAncestor(ProjectOptions project, string repo, string ancestor, string descendant, CancellationToken ct)
    {
        if (!await IsAncestorAsync(project, repo, ancestor, descendant, ct))
            throw new InvalidOperationException("Selected release source does not contain the required source ancestry.");
    }

    private async Task<bool> IsAncestorAsync(ProjectOptions project, string repo, string ancestor, string descendant, CancellationToken ct)
    {
        if (!Sha(ancestor) || !Sha(descendant)) throw new InvalidOperationException("Merged source evidence requires full commit identities.");
        if (ancestor == descendant) return true;
        using var comparison = await GetJson(project, $"repos/{repo}/compare/{ancestor}...{descendant}", ct);
        if (!comparison.RootElement.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("GitHub ancestry evidence is missing.");
        return status.GetString() switch
        {
            "ahead" or "identical" => true,
            "behind" or "diverged" => false,
            _ => throw new InvalidOperationException("GitHub ancestry evidence has an unrecognized status.")
        };
    }

    public async Task<TrustedImageReceipt?> FindReceiptAsync(ProjectOptions project, string sha, CancellationToken ct)
    {
        if (!Sha(sha)) throw new InvalidOperationException("Routine source must be a full commit SHA.");
        var options = project.Deployment ?? throw new InvalidOperationException("Deployment configuration missing.");
        if (options.Kind != DeploymentKind.Kubernetes || !options.RequireBuildCheck)
            throw new InvalidOperationException("Routine delivery requires the Kubernetes release protocol and mandatory CI qualification.");
        if (!Regex.IsMatch(options.RoutineWorkflow, "^[A-Za-z0-9_.-]+\\.ya?ml$")) throw new InvalidOperationException("Invalid trusted workflow path.");
        var repo = Repository(project);
        var validationRun = await FindValidationRun(project, repo, sha, ct);
        if (validationRun is null) return null;
        using var workflow = await GetJson(project, $"repos/{repo}/actions/workflows/{options.RoutineWorkflow}", ct);
        var workflowId = workflow.RootElement.GetProperty("id").GetInt64();
        if (workflow.RootElement.GetProperty("path").GetString() != ".github/workflows/" + options.RoutineWorkflow)
            throw new InvalidOperationException("Configured workflow path changed.");
        using var runs = await GetJson(project, $"repos/{repo}/actions/workflows/{workflowId}/runs?head_sha={sha}&branch={Uri.EscapeDataString(project.DefaultBranch)}&event=push&per_page=100", ct);
        foreach (var run in runs.RootElement.GetProperty("workflow_runs").EnumerateArray().OrderByDescending(r => r.GetProperty("id").GetInt64()).Take(1))
        {
            if (run.GetProperty("status").GetString() != "completed" || run.GetProperty("conclusion").GetString() != "success") continue;
            var runId = run.GetProperty("id").GetInt64();
            if (run.GetProperty("workflow_id").GetInt64() != workflowId || run.GetProperty("head_sha").GetString() != sha ||
                run.GetProperty("head_branch").GetString() != project.DefaultBranch || run.GetProperty("event").GetString() != "push" ||
                !string.Equals(run.GetProperty("repository").GetProperty("full_name").GetString(), repo, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(run.GetProperty("head_repository").GetProperty("full_name").GetString(), repo, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Workflow run provenance does not match routine release policy.");
            using var artifacts = await GetJson(project, $"repos/{repo}/actions/runs/{runId}/artifacts?per_page=100", ct);
            var matches = artifacts.RootElement.GetProperty("artifacts").EnumerateArray()
                .Where(a => a.GetProperty("name").GetString() == "forge-image-receipt" && !a.GetProperty("expired").GetBoolean()).ToArray();
            if (matches.Length == 0) continue;
            if (matches.Length != 1) throw new InvalidOperationException("CI run has ambiguous release artifacts.");
            var artifact = matches[0];
            var artifactRun = artifact.GetProperty("workflow_run");
            if (artifactRun.GetProperty("id").GetInt64() != runId || artifactRun.GetProperty("head_sha").GetString() != sha ||
                artifactRun.GetProperty("head_branch").GetString() != project.DefaultBranch ||
                artifactRun.GetProperty("repository_id").GetInt64() != run.GetProperty("repository").GetProperty("id").GetInt64() ||
                artifactRun.GetProperty("head_repository_id").GetInt64() != run.GetProperty("head_repository").GetProperty("id").GetInt64())
                throw new InvalidOperationException("Artifact belongs to another workflow run or source repository.");
            var id = artifact.GetProperty("id").GetInt64();
            var digest = artifact.GetProperty("digest").GetString();
            if (artifact.GetProperty("size_in_bytes").GetInt64() > 1048576 || digest is null || !Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$"))
                throw new InvalidOperationException("CI artifact has no bounded immutable digest.");
            var bytes = await DownloadArtifact(project, $"repos/{repo}/actions/artifacts/{id}/zip", ct);
            if ("sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != digest)
                throw new InvalidOperationException("CI artifact digest verification failed.");
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count != 1 || zip.Entries[0].FullName != "forge-image-receipt.json" || zip.Entries[0].Length > 65536)
                throw new InvalidOperationException("CI receipt archive has unexpected content.");
            await using var receiptStream = zip.Entries[0].Open();
            using var receipt = JsonDocument.Parse(await ReadBoundedStream(receiptStream, 65536, ct));
            var r = receipt.RootElement;
            var image = r.GetProperty("imageDigest").GetString() ?? "";
            if (r.GetProperty("sourceSha").GetString() != sha || r.GetProperty("workflow").GetString() != options.RoutineWorkflow ||
                r.GetProperty("workflowRunId").GetString() != runId.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                !r.GetProperty("buildPassed").GetBoolean() || !r.GetProperty("testPassed").GetBoolean() ||
                !Regex.IsMatch(image, "^" + Regex.Escape(options.RoutineImageRepository) + "@sha256:[0-9a-f]{64}$"))
                throw new InvalidOperationException("CI receipt source, image, workflow, or success evidence does not match.");
            if (!r.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != IssueStore.CurrentSchemaVersion)
                throw new InvalidOperationException("Routine release requires the current schema version; schema changes need the migration coordinator.");
            return new(sha, image, true, true, options.RoutineWorkflow, runId.ToString(), repo, project.DefaultBranch, id.ToString(), digest,
                schema.GetInt32(), options.RoutineValidationWorkflow, validationRun);
        }
        return null;
    }

    private async Task<string?> FindValidationRun(ProjectOptions project, string repo, string sha, CancellationToken ct)
    {
        var name = project.Deployment!.RoutineValidationWorkflow;
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_.-]+\\.ya?ml$")) throw new InvalidOperationException("Invalid validation workflow.");
        using var workflow = await GetJson(project, $"repos/{repo}/actions/workflows/{name}", ct);
        if (workflow.RootElement.GetProperty("path").GetString() != ".github/workflows/" + name)
            throw new InvalidOperationException("Validation workflow identity changed.");
        var id = workflow.RootElement.GetProperty("id").GetInt64();
        using var runs = await GetJson(project, $"repos/{repo}/actions/workflows/{id}/runs?head_sha={sha}&branch={Uri.EscapeDataString(project.DefaultBranch)}&event=push&per_page=100", ct);
        foreach (var run in runs.RootElement.GetProperty("workflow_runs").EnumerateArray().OrderByDescending(r => r.GetProperty("id").GetInt64()).Take(1))
        {
            if (run.GetProperty("status").GetString() != "completed" || run.GetProperty("conclusion").GetString() != "success") continue;
            if (run.GetProperty("workflow_id").GetInt64() != id || run.GetProperty("head_sha").GetString() != sha ||
                run.GetProperty("head_branch").GetString() != project.DefaultBranch || run.GetProperty("event").GetString() != "push" ||
                !string.Equals(run.GetProperty("repository").GetProperty("full_name").GetString(), repo, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(run.GetProperty("head_repository").GetProperty("full_name").GetString(), repo, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Validation run provenance mismatch.");
            return run.GetProperty("id").GetInt64().ToString();
        }
        return null;
    }

    private async Task<JsonDocument> GetJson(ProjectOptions project, string path, CancellationToken ct, string apiVersion = "2026-03-10")
    {
        using var response = await SendApi(project, path, ct, apiVersion);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await ReadBounded(response, ct));
    }

    private async Task<HttpResponseMessage> SendApi(ProjectOptions project, string path, CancellationToken ct, string apiVersion = "2026-03-10")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.github.com/" + path));
        request.Headers.UserAgent.ParseAdd("Forge-Routine-Deployment");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", apiVersion);
        var value = await token(project, ct);
        if (!string.IsNullOrEmpty(value)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value);
        return await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<byte[]> DownloadArtifact(ProjectOptions project, string path, CancellationToken ct)
    {
        using var response = await SendApi(project, path, ct);
        if (response.StatusCode != HttpStatusCode.Found) { response.EnsureSuccessStatusCode(); return await ReadBounded(response, ct); }
        var location = response.Headers.Location;
        if (location is null || location.Scheme != "https" || !location.IsDefaultPort || !string.IsNullOrEmpty(location.UserInfo) ||
            !(location.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase) ||
              location.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Artifact redirect is not an approved public GitHub artifact host.");
        // Separate client, no bearer credentials and no automatic redirect following.
        using var download = await downloads.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, ct);
        download.EnsureSuccessStatusCode();
        return await ReadBounded(download, ct);
    }

    private static async Task<byte[]> ReadBounded(HttpResponseMessage response, CancellationToken ct)
    {
        const int max = 1048576;
        if (response.Content.Headers.ContentLength > max) throw new InvalidOperationException("GitHub response exceeds release evidence bound.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        return await ReadBoundedStream(source, max, ct);
    }

    private static async Task<byte[]> ReadBoundedStream(Stream source, int max, CancellationToken ct)
    {
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (target.Length + count > max) throw new InvalidOperationException("GitHub response exceeds release evidence bound.");
            target.Write(buffer, 0, count);
        }
        return target.ToArray();
    }
}
