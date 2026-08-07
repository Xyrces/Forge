using Forge.AgentTools;
using Forge.Agents.Gates;
using Forge.Configuration;
using Forge.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// Run-gate framework: the plan gate's deterministic gates, the
/// pipeline's resolution order (DB override -> config -> defaults),
/// the submit_plan tool flow, and the bash tool's hard enforcement.
/// </summary>
public class RunGateTests : IDisposable
{
    private readonly string _workDir;

    public RunGateTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("gates");
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static RunGateContext Ctx(
        string plan,
        IReadOnlyList<string>? territory = null,
        bool allowRoot = false,
        string worktree = "/tmp",
        string taskText = "Implement the thing.") => new(
        TaskId: "task-1",
        RoleName: "coredev",
        TerritoryPrefixes: territory ?? Array.Empty<string>(),
        TerritoryAllowsRootFiles: allowRoot,
        WorktreePath: worktree,
        TaskText: taskText,
        Plan: plan,
        Ct: CancellationToken.None);

    private const string FullPlan = """
        ## Goal
        Add the SyncWorktreeToRefAsync primitive so rework dispatch can sync to the PR head.
        ## Files
        - `AgentTools/GitWorktreeService.cs` — add the method
        - `tests/Forge.Tests/GitWorktreeServiceTests.cs` — coverage
        ## Approach
        Fetch into a per-task ref and reset --hard onto it.
        ## Test
        Port the existing bare-origin fixture tests; run dotnet test.
        ## Done
        New tests pass; the old default-branch variant is gone.
        """;

    // ---------- PlanSchemaGate ----------

    [Fact]
    public async Task SchemaGate_ThinPlan_Revises()
    {
        var v = await new PlanSchemaGate().EvaluateAsync(Ctx("do the thing"));
        Assert.Equal(GateOutcome.Revise, v.Outcome);
    }

    [Fact]
    public async Task SchemaGate_MissingSection_Revises()
    {
        var plan = FullPlan.Replace("## Done", "## Wrapup");
        var v = await new PlanSchemaGate().EvaluateAsync(Ctx(plan));
        Assert.Equal(GateOutcome.Revise, v.Outcome);
        Assert.Contains("done", v.Feedback);
    }

    [Fact]
    public async Task SchemaGate_FullPlan_Approves()
    {
        var v = await new PlanSchemaGate().EvaluateAsync(Ctx(FullPlan));
        Assert.Equal(GateOutcome.Approve, v.Outcome);
    }

    // ---------- PlanTerritoryGate ----------

    [Fact]
    public async Task TerritoryGate_BareFilenameInProse_NotFlagged()
    {
        // Observed live 2026-07-26 (task-196): the Files section had
        // full paths, but prose in Goal mentioned bare "RunGate.cs"
        // and the extractor flagged it as nonexistent — 3 revisions
        // burned. Extraction is scoped to ## Files only.
        File.WriteAllText(Path.Combine(_workDir, "probe.tmp"), "x"); // worktree root sanity
        var plan = """
            ## Goal
            Add Description to RunGate.cs and each concrete gate (PlanSchemaGate.cs etc).
            ## Files
            - `Agents/Gates/RunGate.cs` (new)
            ## Approach
            Add the property.
            ## Test
            Build.
            ## Done
            Builds.
            """;
        var v = await new PlanTerritoryGate().EvaluateAsync(
            Ctx(plan, territory: new[] { "Agents/" }, worktree: _workDir));
        Assert.Equal(GateOutcome.Approve, v.Outcome);
    }

    [Fact]
    public async Task TerritoryGate_GlobMentionInFilesSection_NotFlagged()
    {
        // Observed live 2026-07-29 (porthorizon task-13): the Files
        // section referenced `Data/Ships/*.ship.json` (data the test
        // asserts about, not a file to edit). The regex matched the
        // ".ship.json" tail after the '*', producing a phantom
        // "ship.json" path — 3 revisions burned, run rejected.
        var plan = """
            ## Goal
            Verify ship data reaches the registry.
            ## Files
            - `PortHorizon.Tests/ECS/ShipDefinitionRegistryBootstrapTests.cs` (new)
            - `Data/Ships/*.ship.json` (reference — already covered by the Data/**/*.json glob)
            ## Approach
            Assert the registry is non-empty after DataBootstrapper.Initialize.
            ## Test
            dotnet test --filter ShipDefinitionRegistryBootstrap
            ## Done
            Test green.
            """;
        var v = await new PlanTerritoryGate().EvaluateAsync(
            Ctx(plan, territory: new[] { "PortHorizon.Tests/" }, worktree: _workDir));
        Assert.Equal(GateOutcome.Approve, v.Outcome);
    }

    [Fact]
    public async Task TerritoryGate_NoTerritoryConstraint_Approves()
    {
        var v = await new PlanTerritoryGate().EvaluateAsync(Ctx(FullPlan));
        Assert.Equal(GateOutcome.Approve, v.Outcome);
    }

    [Fact]
    public async Task TerritoryGate_OutsideTerritory_Revises()
    {
        // ClientDev plan touching backend files.
        var v = await new PlanTerritoryGate().EvaluateAsync(
            Ctx(FullPlan, territory: new[] { "Forge.UI/", "tests/" }, worktree: _workDir));
        Assert.Equal(GateOutcome.Revise, v.Outcome);
        Assert.Contains("AgentTools/GitWorktreeService.cs", v.Feedback);
    }

    [Fact]
    public async Task TerritoryGate_InsideTerritory_ExistingFile_Approves()
    {
        Directory.CreateDirectory(Path.Combine(_workDir, "Forge.UI/Components/Pages"));
        File.WriteAllText(Path.Combine(_workDir, "Forge.UI/Components/Pages/Agents.razor"), "@page \"/agents\"");
        var plan = """
            ## Goal
            Restyle the Agents page.
            ## Files
            - `Forge.UI/Components/Pages/Agents.razor`
            ## Approach
            Swap chips for the picker-bar pattern.
            ## Test
            dotnet build clean; render check.
            ## Done
            Page builds and renders.
            """;
        var v = await new PlanTerritoryGate().EvaluateAsync(
            Ctx(plan, territory: new[] { "Forge.UI/", "tests/" }, worktree: _workDir));
        Assert.Equal(GateOutcome.Approve, v.Outcome);
    }

    [Fact]
    public async Task TerritoryGate_NonexistentFile_Revises_UnlessMarkedNew()
    {
        var plan = """
            ## Goal
            Add a component.
            ## Files
            - `Forge.UI/Components/NewWidget.razor`
            ## Approach
            Create it.
            ## Test
            Build.
            ## Done
            Builds.
            """;
        var territory = new[] { "Forge.UI/" };
        var revise = await new PlanTerritoryGate().EvaluateAsync(Ctx(plan, territory: territory, worktree: _workDir));
        Assert.Equal(GateOutcome.Revise, revise.Outcome);
        Assert.Contains("(new)", revise.Feedback);

        var markedNew = plan.Replace("`Forge.UI/Components/NewWidget.razor`", "`Forge.UI/Components/NewWidget.razor` (new)");
        var approve = await new PlanTerritoryGate().EvaluateAsync(Ctx(markedNew, territory: territory, worktree: _workDir));
        Assert.Equal(GateOutcome.Approve, approve.Outcome);
    }

    [Fact]
    public async Task TerritoryGate_RootFile_OnlyWhenAllowed()
    {
        File.WriteAllText(Path.Combine(_workDir, "Program.cs"), "// root");
        var plan = """
            ## Goal
            Wire the option.
            ## Files
            - `Program.cs`
            ## Approach
            Add the binding.
            ## Test
            Build.
            ## Done
            Builds.
            """;
        var denied = await new PlanTerritoryGate().EvaluateAsync(
            Ctx(plan, territory: new[] { "Forge.UI/" }, allowRoot: false, worktree: _workDir));
        Assert.Equal(GateOutcome.Revise, denied.Outcome);

        var allowed = await new PlanTerritoryGate().EvaluateAsync(
            Ctx(plan, territory: new[] { "Core/" }, allowRoot: true, worktree: _workDir));
        Assert.Equal(GateOutcome.Approve, allowed.Outcome);
    }

    [Fact]
    public async Task TerritoryGate_BareFilenameInProse_NotViolated()
    {
        // Bare filename "RunGate.cs" in prose should not be extracted when ## Files section exists.
        var plan = @"
## Goal
We need to modify RunGate.cs to support the new flow.
## Files
- `Agents/Gates/RunGate.cs`
## Approach
Add the new method.
## Test
Build.
## Done
Builds.
";
        Directory.CreateDirectory(Path.Combine(_workDir, "Agents/Gates"));
        File.WriteAllText(Path.Combine(_workDir, "Agents/Gates/RunGate.cs"), "// stub");
        var v = await new PlanTerritoryGate().EvaluateAsync(
            Ctx(plan, territory: new[] { "Agents/" }, worktree: _workDir));
        Assert.Equal(GateOutcome.Approve, v.Outcome);
    }

    [Fact]
    public async Task TerritoryGate_BareFilenameDuplicateFullPath_NotViolated()
    {
        // Files section has both full path and bare filename; bare duplicate should be skipped.
        var plan = @"
## Goal
Add the new flow.
## Files
- `Agents/Gates/RunGate.cs`
- `RunGate.cs`
## Approach
Implement it.
## Test
Build.
## Done
Builds.
";
        Directory.CreateDirectory(Path.Combine(_workDir, "Agents/Gates"));
        File.WriteAllText(Path.Combine(_workDir, "Agents/Gates/RunGate.cs"), "// stub");
        var v = await new PlanTerritoryGate().EvaluateAsync(
            Ctx(plan, territory: new[] { "Agents/" }, worktree: _workDir));
        Assert.Equal(GateOutcome.Approve, v.Outcome);
    }

    [Fact]
    public async Task TerritoryGate_BareFilenameWithoutFullPath_StillViolated()
    {
        // Bare filename "RunGate.cs" with no full-path equivalent should still trigger violation
        // when the file doesn't exist at root.
        var plan = @"
## Goal
Add the new flow.
## Files
- `RunGate.cs`
## Approach
Implement it.
## Test
Build.
## Done
Builds.
";
        var v = await new PlanTerritoryGate().EvaluateAsync(
            Ctx(plan, territory: new[] { "Agents/" }, allowRoot: false, worktree: _workDir));
        Assert.Equal(GateOutcome.Revise, v.Outcome);
        Assert.Contains("RunGate.cs", v.Feedback);
    }

    [Fact]
    public async Task TerritoryGate_BareFilenameMatchesFullPath_OnlyDupeIsSkipped()
    {
        // Full-path file exists, bare dupe skipped, but bare non-dupe still violates.
        var plan = @"
## Goal
Add the new flow.
## Files
- `Agents/Gates/RunGate.cs`
- `RunGate.cs`
- `SomeOther.cs`
## Approach
Implement it.
## Test
Build.
## Done
Builds.
";
        Directory.CreateDirectory(Path.Combine(_workDir, "Agents/Gates"));
        File.WriteAllText(Path.Combine(_workDir, "Agents/Gates/RunGate.cs"), "// stub");
        var v = await new PlanTerritoryGate().EvaluateAsync(
            Ctx(plan, territory: new[] { "Agents/" }, allowRoot: false, worktree: _workDir));
        Assert.Equal(GateOutcome.Revise, v.Outcome);
        Assert.Contains("SomeOther.cs", v.Feedback);
        Assert.DoesNotContain("RunGate.cs", v.Feedback);
    }

    // ---------- RunGatePipeline resolution ----------

    private RunGatePipeline Pipeline(GateOptions options, MemoryStore? memory = null)
        => new(options, memory, name => name switch
        {
            PlanSchemaGate.GateName => new PlanSchemaGate(),
            PlanTerritoryGate.GateName => new PlanTerritoryGate(),
            _ => null,
        }, NullLogger.Instance);

    [Fact]
    public async Task Pipeline_Defaults_WhenNoConfigNoDb()
    {
        var names = await Pipeline(new GateOptions()).ResolveGateNamesAsync(
            RunGatePipeline.PreImplementationCheckpoint, CancellationToken.None);
        Assert.Equal(new[] { "plan-schema", "plan-territory", "plan-llm-review" }, names);
    }

    [Fact]
    public async Task Pipeline_ConfigOverride_BeatsDefaults()
    {
        var options = new GateOptions { Run = { ["preImplementation"] = new[] { "plan-schema" } } };
        var names = await Pipeline(options).ResolveGateNamesAsync(
            RunGatePipeline.PreImplementationCheckpoint, CancellationToken.None);
        Assert.Equal(new[] { "plan-schema" }, names);
    }

    [Fact]
    public async Task Pipeline_DbOverride_BeatsConfigAndDefaults()
    {
        // The memory table lives in IssueStore's schema — bootstrap it.
        await using (var bootstrap = new IssueStore(Path.Combine(_workDir, "mem.db"))) { }
        await using var memory = new MemoryStore(Path.Combine(_workDir, "mem.db"));
        await memory.RememberAsync("gates/run/preImplementation", "[\"plan-territory\"]");
        var options = new GateOptions { Run = { ["preImplementation"] = new[] { "plan-schema" } } };
        var names = await Pipeline(options, memory).ResolveGateNamesAsync(
            RunGatePipeline.PreImplementationCheckpoint, CancellationToken.None);
        Assert.Equal(new[] { "plan-territory" }, names);
    }

    [Fact]
    public async Task Pipeline_ShortCircuits_OnFirstRevise()
    {
        var options = new GateOptions { Run = { ["preImplementation"] = new[] { "plan-schema", "plan-territory" } } };
        var state = new RunGateState();
        var v = await Pipeline(options).EvaluateAsync(
            RunGatePipeline.PreImplementationCheckpoint, Ctx("too thin"), state);
        Assert.Equal(GateOutcome.Revise, v.Outcome);
        Assert.Single(state.Verdicts);   // territory gate never ran
    }

    [Fact]
    public async Task Pipeline_UnknownGate_Skipped()
    {
        var options = new GateOptions { Run = { ["preImplementation"] = new[] { "bogus-gate", "plan-schema" } } };
        var state = new RunGateState();
        var v = await Pipeline(options).EvaluateAsync(
            RunGatePipeline.PreImplementationCheckpoint, Ctx(FullPlan), state);
        Assert.Equal(GateOutcome.Approve, v.Outcome);
        Assert.Single(state.Verdicts);   // only the real gate recorded
    }

    [Fact]
    public async Task Pipeline_ThrowingGate_ApprovesWithWarning()
    {
        var pipeline = new RunGatePipeline(new GateOptions(), null,
            _ => new ThrowingGate(), NullLogger.Instance);
        var state = new RunGateState();
        var v = await pipeline.EvaluateAsync("preImplementation", Ctx(FullPlan), state);
        Assert.Equal(GateOutcome.Approve, v.Outcome);
        Assert.Contains("approved with warning", state.Verdicts[0].Feedback);
    }

    private sealed class ThrowingGate : IRunGate
    {
        public string Name => "throwing";
        public string Description => "Throwing test gate";
        public GateKind Kind => GateKind.Deterministic;
        public Task<RunGateVerdict> EvaluateAsync(RunGateContext ctx) => throw new InvalidOperationException("boom");
    }

    // ---------- PlanLlmReviewGate ----------

    private sealed class FakeChatClient : IChatClient
    {
        private readonly string _response;
        public FakeChatClient(string response) { _response = response; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response)));
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task LlmGate_ReviseVerdict_ReturnsFeedback()
    {
        var gate = new PlanLlmReviewGate(
            () => new FakeChatClient("The plan misses the wiring step.\nVERDICT: REVISE"), NullLogger.Instance);
        var v = await gate.EvaluateAsync(Ctx(FullPlan));
        Assert.Equal(GateOutcome.Revise, v.Outcome);
        Assert.Contains("wiring", v.Feedback);
    }

    [Fact]
    public async Task LlmGate_ApproveVerdict_Approves()
    {
        var gate = new PlanLlmReviewGate(
            () => new FakeChatClient("Looks sound.\nVERDICT: APPROVE"), NullLogger.Instance);
        var v = await gate.EvaluateAsync(Ctx(FullPlan));
        Assert.Equal(GateOutcome.Approve, v.Outcome);
    }

    [Fact]
    public async Task LlmGate_CriticThrows_FailsOpen()
    {
        var gate = new PlanLlmReviewGate(() => throw new HttpRequestException("429"), NullLogger.Instance);
        var v = await gate.EvaluateAsync(Ctx(FullPlan));
        Assert.Equal(GateOutcome.Approve, v.Outcome);
        Assert.Contains("approved with warning", v.Feedback);
    }

    // ---------- SubmitPlanTool ----------

    private SubmitPlanTool Tool(RunGateState state, string checkpoint = "plan-schema")
    {
        var options = new GateOptions { Run = { ["preImplementation"] = new[] { checkpoint } } };
        var pipeline = new RunGatePipeline(options, null,
            name => name == PlanSchemaGate.GateName ? new PlanSchemaGate() : null, NullLogger.Instance);
        return new SubmitPlanTool(state, pipeline, Ctx(""), NullLogger.Instance);
    }

    [Fact]
    public async Task SubmitPlan_Approved_UnlocksMutations()
    {
        var state = new RunGateState();
        var result = await Tool(state).SubmitPlan(FullPlan);
        Assert.Contains("APPROVED", result);
        Assert.True(state.PlanApproved);
    }

    [Fact]
    public async Task SubmitPlan_Revise_CountsRevisions_ThenFinalRejection()
    {
        var state = new RunGateState();
        var tool = Tool(state);
        var r1 = await tool.SubmitPlan("thin");
        Assert.Contains("revision 1 of 2", r1);
        Assert.False(state.PlanApproved);
        var r2 = await tool.SubmitPlan("still thin");
        Assert.Contains("revision 2 of 2", r2);
        var r3 = await tool.SubmitPlan("thin again");
        Assert.Contains("REJECTED (final", r3);
        Assert.True(state.PlanFailed);
        Assert.False(state.PlanApproved);
    }

    [Fact]
    public async Task SubmitPlan_FastPath_AutoApproves()
    {
        var state = new RunGateState { FastPath = true };
        var result = await Tool(state).SubmitPlan("merge main, resolve, push");
        Assert.Contains("AUTO-APPROVED", result);
        Assert.True(state.PlanApproved);
    }

    // ---------- ShellMutationClassifier + BashTool enforcement ----------

    [Theory]
    [InlineData("cat > file.cs <<'EOF'", true)]
    [InlineData("echo hi >> out.txt", true)]
    [InlineData("git commit -m 'x'", true)]
    [InlineData("git push origin branch", true)]
    [InlineData("git merge origin/main", true)]
    [InlineData("git reset --hard origin/x", true)]
    [InlineData("rm -rf bin", true)]
    [InlineData("mkdir foo", true)]
    [InlineData("sed -i s/a/b/ file.cs", true)]
    [InlineData("tee output.txt", true)]
    [InlineData("git status", false)]
    [InlineData("git log --oneline -5", false)]
    [InlineData("git diff HEAD", false)]
    [InlineData("git fetch origin", false)]
    [InlineData("dotnet build Forge.Core/Forge.Core.csproj", false)]
    [InlineData("dotnet test tests/Forge.Tests", false)]
    [InlineData("grep -rn foo Core/", false)]
    [InlineData("cat file.cs", false)]
    [InlineData("ls -la", false)]
    // Silencer idioms are read-only (observed live 2026-08-06:
    // task-382's run had `ls -la .forge/ 2>/dev/null` refused as
    // "mutating" — the fd-redirect prefix matched the raw `>` rule).
    [InlineData("ls -la .forge/ 2>/dev/null", false)]
    [InlineData("ls -la docs/ 2>/dev/null; cat status.json 2>/dev/null", false)]
    [InlineData("cat file.cs 2>&1 | grep foo", false)]
    [InlineData("dotnet test 2>&1 | tail -5", false)]
    [InlineData("grep -rn foo Core/ &>/dev/null", false)]
    [InlineData("echo hi > /dev/null", false)]
    // …but real writes still classify even with silencers attached.
    [InlineData("echo hi > out.txt 2>/dev/null", true)]
    [InlineData("cat > file.cs <<'EOF' 2>&1", true)]
    public void Classifier_DetectsMutations(string command, bool expected)
    {
        Assert.Equal(expected, ShellMutationClassifier.IsMutating(command));
    }

    [Fact]
    public void Classifier_RefusalCarriesReason()
    {
        Assert.True(ShellMutationClassifier.IsMutating("echo hi > out.txt", out var reason));
        Assert.Equal("writes a file (>, >>, or tee)", reason);
        Assert.False(ShellMutationClassifier.IsMutating("ls", out var clean));
        Assert.Null(clean);
    }

    [Fact]
    public async Task BashTool_RefusesMutation_BeforeApproval_AllowsAfter()
    {
        var allowed = false;
        var tool = new BashTool(_workDir, mutationsAllowed: () => allowed);

        var refused = await tool.Bash("git commit -m 'x'");
        Assert.Contains("REFUSED", refused);
        Assert.Contains("submit_plan", refused);

        // Reads are never gated.
        var read = await tool.Bash("echo hello");
        Assert.Contains("hello", read);

        allowed = true;
        var dir = Path.Combine(_workDir, "subdir");
        var mk = await tool.Bash("mkdir subdir");
        Assert.DoesNotContain("REFUSED", mk);
        Assert.True(Directory.Exists(dir));
    }
}
