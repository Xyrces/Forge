using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Codebase;
using Forge.Core;
using Forge.Orchestrator;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// One test per hygiene rule. The rules are deterministic, so
/// these tests can run without an LLM. A failure here means the
/// rule is broken; a missing test means a new rule has been added
/// without coverage.
/// </summary>
public class DesignHygieneCheckerTests : IDisposable
{
    private readonly string _workDir;
    private readonly IssueStore _issues;
    private readonly SpecStore _specs;
    private readonly CodebaseGraphCacheStore _graphCache;
    private readonly DotnetCodebaseGraphBuilder _graphBuilder;

    public DesignHygieneCheckerTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"ph-hygiene-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        InitRepo(_workDir);
        _issues = new IssueStore(Path.Combine(_workDir, "issues.db"));
        _specs = new SpecStore(_issues);
        _graphCache = new CodebaseGraphCacheStore(_issues);
        _graphBuilder = new DotnetCodebaseGraphBuilder();
    }

    public void Dispose()
    {
        _issues.Dispose();
        _specs.Dispose();
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static void InitRepo(string dir)
    {
        Run("git", "init -q -b main", dir);
        Run("git", "config user.email t@t", dir);
        Run("git", "config user.name T", dir);
        // Set up a PortHorizon.Core project + file so the codebase
        // graph actually has modules the hygiene checker can validate
        // against. Without this, the healthy-spec test fails on
        // touches_undefined_module.
        var coreDir = Path.Combine(dir, "PortHorizon.Core");
        Directory.CreateDirectory(coreDir);
        File.WriteAllText(Path.Combine(coreDir, "PortHorizon.Core.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(coreDir, "Program.cs"), "class Program { }");
        File.WriteAllText(Path.Combine(dir, "README.md"), "x");
        Run("git", "add .", dir);
        Run("git", "commit -q -m init", dir);
    }

    private static void Run(string exe, string args, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, Arguments = args, WorkingDirectory = cwd,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    private DesignHygieneChecker NewChecker() => new(_specs, _graphCache, _graphBuilder, _workDir);

    private async Task<SpecRecord> CreateSpecAsync(string body, SpecStatus status = SpecStatus.ReadyForDesign)
    {
        var spec = await _specs.CreateAsync(new NewSpec("P", "Test", body));
        if (status != SpecStatus.Draft)
        {
            await _specs.SetStatusAsync(spec.Id, status);
        }
        return (await _specs.GetAsync(spec.Id))!;
    }

    private const string HealthyBody = """
        ## Summary
        The spec summary.

        ## Acceptance criteria
        - [ ] do the thing
        - [ ] verify the thing

        ## Touches
        - PortHorizon.Core

        ## Dependencies
        - none

        ## Out of scope
        nothing
        """;

    [Fact]
    public async Task HealthySpec_PassesAllRules()
    {
        var spec = await CreateSpecAsync(HealthyBody);
        var report = await NewChecker().CheckAsync(spec);
        Assert.True(report.Passed);
        Assert.Empty(report.Findings.Where(f => f.Severity == HygieneSeverity.Error));
    }

    [Fact]
    public async Task MissingAcceptanceCriteria_ReportsError()
    {
        var body = """
            ## Summary
            nothing
            ## Touches
            - PortHorizon.Core
            """;
        var spec = await CreateSpecAsync(body);
        var report = await NewChecker().CheckAsync(spec);
        var f = report.Findings.Single(x => x.Rule == "missing_acceptance_criteria");
        Assert.Equal(HygieneSeverity.Error, f.Severity);
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task AcceptanceCriteriaSectionButEmpty_ReportsError()
    {
        var body = """
            ## Summary
            nothing
            ## Acceptance criteria

            ## Touches
            - PortHorizon.Core
            """;
        var spec = await CreateSpecAsync(body);
        var report = await NewChecker().CheckAsync(spec);
        var f = report.Findings.Single(x => x.Rule == "missing_acceptance_criteria");
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task BrokenDepChain_ReportsError()
    {
        var body = """
            ## Summary
            test
            ## Acceptance criteria
            - [ ] do the thing
            ## Touches
            - PortHorizon.Core
            ## Dependencies
            - blocks spec-does-not-exist
            """;
        var spec = await CreateSpecAsync(body);
        var report = await NewChecker().CheckAsync(spec);
        var f = report.Findings.Single(x => x.Rule == "broken_dep_chain");
        Assert.False(report.Passed);
        Assert.Contains("spec-does-not-exist", f.Message);
    }

    [Fact]
    public async Task CircularDep_ReportsError()
    {
        // Build a 2-cycle: A depends on B, B depends on A.
        var specB = await CreateSpecAsync(HealthyBody, SpecStatus.Draft);
        var bodyA = HealthyBody + "\n## Dependencies\n- blocks " + specB.Id;
        var specA = await CreateSpecAsync(bodyA, SpecStatus.Draft);
        var bodyB = HealthyBody + "\n## Dependencies\n- blocks " + specA.Id;
        await _specs.SetStatusAsync(specB.Id, SpecStatus.Draft);
        await _specs.UpdateBodyAsync(specB.Id, new UpdateSpecBody(bodyB));
        var a = (await _specs.GetAsync(specA.Id))!;
        var report = await NewChecker().CheckAsync(a);
        var f = report.Findings.SingleOrDefault(x => x.Rule == "circular_dep");
        Assert.NotNull(f);
        Assert.Equal(HygieneSeverity.Error, f!.Severity);
    }

    [Fact]
    public async Task TouchesUndefinedModule_ReportsError()
    {
        var body = HealthyBody.Replace("- PortHorizon.Core", "- PortHorizon.NotARealModule");
        var spec = await CreateSpecAsync(body);
        var report = await NewChecker().CheckAsync(spec);
        var f = report.Findings.Single(x => x.Rule == "touches_undefined_module");
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task NoTouches_ReportsWarning_Passes()
    {
        var body = """
            ## Summary
            spec summary
            ## Acceptance criteria
            - [ ] do the thing
            """;
        var spec = await CreateSpecAsync(body);
        var report = await NewChecker().CheckAsync(spec);
        var f = report.Findings.Single(x => x.Rule == "no_touches");
        Assert.Equal(HygieneSeverity.Warning, f.Severity);
        Assert.True(report.Passed);  // warning doesn't fail
    }

    [Fact]
    public async Task DuplicateEpicInActiveSprint_ReportsError()
    {
        var dup = await CreateSpecAsync(HealthyBody, SpecStatus.Draft);
        await _specs.SetStatusAsync(dup.Id, SpecStatus.Approved);
        var spec = await CreateSpecAsync(HealthyBody);  // same title "Test"
        var report = await NewChecker().CheckAsync(spec);
        var f = report.Findings.Single(x => x.Rule == "duplicate_epic_in_active_sprint");
        Assert.Equal(HygieneSeverity.Error, f.Severity);
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task BodyTooLong_ReportsWarning_Passes()
    {
        var big = new string('x', 50_001);
        var spec = await CreateSpecAsync(HealthyBody + big);
        var report = await NewChecker().CheckAsync(spec);
        var f = report.Findings.Single(x => x.Rule == "body_too_long");
        Assert.Equal(HygieneSeverity.Warning, f.Severity);
    }

    [Fact]
    public async Task StaleOpenQuestions_ReportsWarning_WhenMoreThan3()
    {
        var body = """
            ## Summary
            s
            ## Acceptance criteria
            - [ ] a
            ## Open questions
            - q1
            - q2
            - q3
            - q4
            """;
        var spec = await CreateSpecAsync(body);
        var report = await NewChecker().CheckAsync(spec);
        var f = report.Findings.Single(x => x.Rule == "stale_open_questions");
        Assert.Equal(HygieneSeverity.Warning, f.Severity);
    }

    [Fact]
    public async Task StatusMismatch_ReportsError_ForShippedSpec()
    {
        // Spec is in Shipped; Designer shouldn't process it.
        var spec = await CreateSpecAsync(HealthyBody, SpecStatus.Draft);
        // Walk the chain: Draft -> ReadyForDesign -> Designed -> Grooming -> Groomed -> Shipped
        // (some transitions require valid intermediates; we walk the chain)
        try
        {
            await _specs.SetStatusAsync(spec.Id, SpecStatus.ReadyForDesign);
            await _specs.SetStatusAsync(spec.Id, SpecStatus.Designed);
            await _specs.SetStatusAsync(spec.Id, SpecStatus.Grooming);
            await _specs.SetStatusAsync(spec.Id, SpecStatus.Groomed);
            await _specs.SetStatusAsync(spec.Id, SpecStatus.Shipped);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Walk failed: {ex.GetType().Name}: {ex.Message}", ex);
        }

        // Re-fetch: the local `spec` is stale; the walk advanced its
        // status. The hygiene checker should still see Shipped.
        var fresh = (await _specs.GetAsync(spec.Id))!;
        Assert.Equal(SpecStatus.Shipped, fresh.Status);

        var report = await NewChecker().CheckAsync(fresh);
        var f = report.Findings.FirstOrDefault(x => x.Rule == "status_mismatch");
        Assert.NotNull(f);
        Assert.Equal(HygieneSeverity.Error, f!.Severity);
    }

    [Fact]
    public async Task HygieneReport_RoundTripsAsJson()
    {
        var spec = await CreateSpecAsync(HealthyBody);
        var report = await NewChecker().CheckAsync(spec);
        var json = report.ToJson();
        var round = JsonSerializer.Deserialize<HygieneReport>(json, DesignerHygieneJsonContext.Default.HygieneReport);
        Assert.NotNull(round);
        Assert.Equal(report.Passed, round!.Passed);
        Assert.Equal(report.Findings.Count, round.Findings.Count);
    }

    [Fact]
    public async Task ValidDepChain_DoesNotReportBroken()
    {
        // Create specB as a real spec; specA depends on specB (a valid link).
        var specB = await CreateSpecAsync(HealthyBody, SpecStatus.Draft);
        var bodyA = HealthyBody + "\n## Dependencies\n- blocks " + specB.Id;
        var specA = await CreateSpecAsync(bodyA, SpecStatus.Draft);
        var report = await NewChecker().CheckAsync(specA);
        Assert.DoesNotContain(report.Findings, f => f.Rule == "broken_dep_chain");
    }

    // P5.4 — artifact marker hygiene.

    [Fact]
    public async Task EmptyArtifactBlock_ReportsWarning()
    {
        // HealthyBody + an empty artifact marker. The marker
        // exists but the body is empty, so the post-processor
        // would store no design_artifact row and the next agent
        // would see "[read_artifact empty-N]" with nothing to
        // fetch. Warn (not error) so the operator can clean up.
        var body = HealthyBody + "\n\n<!-- artifact:wireframe:Empty -->\n";
        var spec = await CreateSpecAsync(body);
        var report = await NewChecker().CheckAsync(spec);
        var f = report.Findings.SingleOrDefault(x => x.Rule == "empty_artifact_block");
        Assert.NotNull(f);
        Assert.Equal(HygieneSeverity.Warning, f!.Severity);
        Assert.Contains("Empty", f.Message);
        Assert.True(report.Passed);  // warnings don't fail the report
    }

    [Fact]
    public async Task TwoEmptyArtifactBlocksBackToBack_ReportsBoth()
    {
        var body = HealthyBody + "\n\n<!-- artifact:wireframe:One -->\n\n<!-- artifact:mockup:Two -->\n";
        var spec = await CreateSpecAsync(body);
        var report = await NewChecker().CheckAsync(spec);
        var findings = report.Findings.Where(x => x.Rule == "empty_artifact_block").ToList();
        Assert.Equal(2, findings.Count);
    }

    [Fact]
    public async Task NonEmptyArtifactBlock_DoesNotReport()
    {
        var body = HealthyBody + "\n\n<!-- artifact:wireframe:Login -->\n<svg>...</svg>\n";
        var spec = await CreateSpecAsync(body);
        var report = await NewChecker().CheckAsync(spec);
        Assert.DoesNotContain(report.Findings, f => f.Rule == "empty_artifact_block");
    }

    [Fact]
    public async Task UnknownArtifactKind_ReportsWarning()
    {
        var body = HealthyBody + "\n\n<!-- artifact:weird-kind:Title -->\ncontent\n";
        var spec = await CreateSpecAsync(body);
        var report = await NewChecker().CheckAsync(spec);
        var f = report.Findings.SingleOrDefault(x => x.Rule == "unknown_artifact_kind");
        Assert.NotNull(f);
        Assert.Equal(HygieneSeverity.Warning, f!.Severity);
        Assert.Contains("weird-kind", f.Message);
    }

    [Fact]
    public async Task KnownArtifactKinds_DoNotReportUnknown()
    {
        var body = HealthyBody + """
            <!-- artifact:wireframe:W1 -->
            content
            <!-- artifact:mockup:M1 -->
            content
            <!-- artifact:component-spec:C1 -->
            content
            <!-- artifact:visual-rule:V1 -->
            content
            """;
        var spec = await CreateSpecAsync(body);
        var report = await NewChecker().CheckAsync(spec);
        Assert.DoesNotContain(report.Findings, f => f.Rule == "unknown_artifact_kind");
    }

    [Fact]
    public async Task MixedKnownAndUnknownKinds_OnlyUnknownFlagged()
    {
        var body = HealthyBody + """
            <!-- artifact:wireframe:W1 -->
            content
            <!-- artifact:made-up-kind:M1 -->
            content
            """;
        var spec = await CreateSpecAsync(body);
        var report = await NewChecker().CheckAsync(spec);
        var findings = report.Findings.Where(x => x.Rule == "unknown_artifact_kind").ToList();
        Assert.Single(findings);
        Assert.Contains("made-up-kind", findings[0].Message);
    }

    [Fact]
    public async Task NamespaceTouch_InsideKnownModule_Passes()
    {
        // Graph modules are csproj-granular ('PortHorizon.Core'); a
        // touches entry naming a namespace inside it is more specific,
        // not undefined (live 2026-07-29: epic-2's spec touched
        // 'PortHorizon.Core.Construction' and was hygiene-failed).
        var body = HealthyBody.Replace("- PortHorizon.Core", "- PortHorizon.Core.Construction");
        var spec = await CreateSpecAsync(body);
        var report = await NewChecker().CheckAsync(spec);
        Assert.DoesNotContain(report.Findings, f => f.Rule == "touches_undefined_module");
    }

    [Fact]
    public async Task TouchesCheckedAgainstSpecProjectRoot_NotPrimaryRoot()
    {
        // Regression for the live 2026-07-29 epic-2 design failure: a
        // porthorizon spec touching PortHorizon.Core.* modules was
        // hygiene-failed because the graph was built from the PRIMARY
        // (forge) workspace. The checker must resolve the graph root
        // per spec project.
        var primaryRoot = Path.Combine(Path.GetTempPath(), $"ph-hygiene-primary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(primaryRoot);
        InitRepo(primaryRoot);   // git repo, but no PortHorizon.Core project
        try
        {
            File.Delete(Path.Combine(primaryRoot, "PortHorizon.Core", "PortHorizon.Core.csproj"));
            File.Delete(Path.Combine(primaryRoot, "PortHorizon.Core", "Program.cs"));
            Directory.Delete(Path.Combine(primaryRoot, "PortHorizon.Core"));

            var checker = new DesignHygieneChecker(
                _specs, _graphCache, _graphBuilder, primaryRoot,
                projectRootLookup: id => id == "porthorizon" ? _workDir : null);
            var spec = await _specs.CreateAsync(new NewSpec("porthorizon", "Test", HealthyBody));
            await _specs.SetStatusAsync(spec.Id, SpecStatus.ReadyForDesign);
            spec = (await _specs.GetAsync(spec.Id))!;

            var report = await checker.CheckAsync(spec);

            Assert.DoesNotContain(report.Findings, f => f.Rule == "touches_undefined_module");
        }
        finally
        {
            try { Directory.Delete(primaryRoot, recursive: true); } catch { }
        }
    }
}