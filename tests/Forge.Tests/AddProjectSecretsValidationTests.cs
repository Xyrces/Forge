using Forge.Dashboard;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Add Project registration-time secrets (operator ask 2026-08-09):
/// kinds follow the SecretsEndpoints rules (FORGE_SECRET_&lt;KIND&gt;
/// env-var suffix); github_token stays on its dedicated field.
/// </summary>
public class AddProjectSecretsValidationTests
{
    [Fact]
    public void NullOrEmptySecrets_AreValid()
    {
        Assert.Null(ProjectsEndpoints.ValidateSecrets(null));
        Assert.Null(ProjectsEndpoints.ValidateSecrets(new Dictionary<string, string>()));
    }

    [Fact]
    public void KnownAndCustomKinds_AreValid()
    {
        var secrets = new Dictionary<string, string>
        {
            ["kimi_api_key"] = "sk-…",
            ["npm_token"] = "npm_…",
        };
        Assert.Null(ProjectsEndpoints.ValidateSecrets(secrets));
    }

    [Fact]
    public void GitHubToken_KindIsRejected_HasDedicatedField()
    {
        var secrets = new Dictionary<string, string> { ["github_token"] = "x" };
        Assert.Contains("Git token field", ProjectsEndpoints.ValidateSecrets(secrets));
    }

    [Fact]
    public void BadKinds_AreRejected()
    {
        Assert.NotNull(ProjectsEndpoints.ValidateSecrets(new Dictionary<string, string> { ["UPPER"] = "x" }));
        Assert.NotNull(ProjectsEndpoints.ValidateSecrets(new Dictionary<string, string> { ["has space"] = "x" }));
        Assert.NotNull(ProjectsEndpoints.ValidateSecrets(new Dictionary<string, string> { ["-leading-dash"] = "x" }));
        Assert.NotNull(ProjectsEndpoints.ValidateSecrets(new Dictionary<string, string> { [""] = "x" }));
    }

    [Fact]
    public void EmptyOrOversizedValues_AreRejected()
    {
        Assert.NotNull(ProjectsEndpoints.ValidateSecrets(new Dictionary<string, string> { ["ok_kind"] = "" }));
        Assert.NotNull(ProjectsEndpoints.ValidateSecrets(new Dictionary<string, string> { ["ok_kind"] = new string('x', 8193) }));
        Assert.Null(ProjectsEndpoints.ValidateSecrets(new Dictionary<string, string> { ["ok_kind"] = new string('x', 8192) }));
    }
}
