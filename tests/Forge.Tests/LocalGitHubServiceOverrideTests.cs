using System.Reflection;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Reflection-based guard: every virtual (overridable) method on
/// <see cref="GitHubService"/> must be overridden in
/// <see cref="LocalGitHubService"/>. If a new virtual is added to
/// GitHubService without a corresponding override in
/// LocalGitHubService, the e2e harness will silently call the real
/// Octokit-backed base implementation (which throws on the fake
/// "local/e2e" repo), halting the pipeline mid-dispatch.
/// This class of break was introduced in commit d25b0b2
/// (GetOpenPullRequestForBranchAsync) and went undetected for 5 CI
/// runs.
/// </summary>
public class LocalGitHubServiceOverrideTests
{
    [Fact]
    public void AllVirtualMethodsOnGitHubService_AreOverriddenInLocalGitHubService()
    {
        var baseType = typeof(GitHubService);
        var derivedType = typeof(LocalGitHubService);

        var virtualMethods = baseType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.IsVirtual && !m.IsFinal && m.Name != "Finalize" && m.Name != "MemberwiseClone")
            .ToList();

        var missing = new List<string>();

        foreach (var vm in virtualMethods)
        {
            var derivedMethod = derivedType.GetMethod(
                vm.Name,
                vm.GetParameters().Select(p => p.ParameterType).ToArray());

            if (derivedMethod is null || !derivedMethod.IsVirtual || derivedMethod.DeclaringType != derivedType)
            {
                missing.Add(vm.Name);
            }
        }

        if (missing.Count > 0)
        {
            Assert.Fail($"LocalGitHubService is missing override(s) for: {string.Join(", ", missing)}. " +
                        "Every virtual method on GitHubService must be overridden in LocalGitHubService " +
                        "so the e2e harness doesn't accidentally call the real Octokit-backed implementation.");
        }
    }

    [Fact]
    public void AllVirtualMethods_ListIsCorrect()
    {
        // Document the expected set so the test itself is auditable.
        var baseType = typeof(GitHubService);
        var virtualMethods = baseType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.IsVirtual && !m.IsFinal && m.Name != "Finalize" && m.Name != "MemberwiseClone")
            .Select(m => m.Name)
            .OrderBy(n => n)
            .ToList();

        var expected = new[]
        {
            "CreateBranchAsync",
            "CreateIssueCommentAsync",
            "CreatePullRequestAsync",
            "DeleteBranchAsync",
            "GetBranchHeadShaAsync",
            "GetCommitStatusAsync",
            "GetCompareDiffAsync",
            "GetFailedCheckRunSummariesAsync",
            "GetOpenPullRequestForBranchAsync",
            "GetPullRequestAsync",
            "GetPullRequestDiffAsync",
            "GetReviewsAsync",
            "MergePullRequestAsync",
            "SubmitReviewAsync",
        };

        Assert.Equal(expected, virtualMethods);
    }
}
