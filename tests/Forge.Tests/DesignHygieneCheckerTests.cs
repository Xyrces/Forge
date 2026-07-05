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
}