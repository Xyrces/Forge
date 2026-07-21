using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.AgentTools;
using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// Live-LLM test: drives MafAgentRunner end-to-end against the kilo gateway
/// to verify the agent can issue tool_calls, have them
/// executed, and produce a final plain-text response. Used to
/// diagnose empty-modelResponse bugs in production dispatch.
/// </summary>
public class MafAgentRunnerLiveTests
{
    [Fact]
    public async Task RunAsync_LiveLLM_BashToolLoop_ProducesFinalText()
    {
        var apiKey = Environment.GetEnvironmentVariable("FORGE_LIVE_LLM_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            // Skip if no key configured.
            return;
        }

        var provider = new ProviderConfig(
            Name: LlmProviders.KiloGateway,
            BaseUrl: "https://api.kilo.ai/api/gateway",
            ApiKey: apiKey,
            OrgId: null,
            DefaultModel: "minimax/minimax-m3");

        var factory = new OpenAICompatibleChatClientFactory();
        var runner = new MafAgentRunner(
            chatClientFactory: factory,
            config: new LlmConfig(provider),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: Path.Combine(Path.GetTempPath(), $"ph-live-md-{Guid.NewGuid():N}"));

        var worktree = Path.Combine(Path.GetTempPath(), $"ph-live-wt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, "PROBE.txt"), "live-marker-12345");

        try
        {
            var result = await runner.RunAsync(
                AgentType.CoreDev,
                "Use the bash tool to read PROBE.txt in the current directory, then report the contents back as plain text.",
                sessionId: null,
                context: new Dictionary<string, object> { ["worktreePath"] = worktree },
                ct: default);

            // After the loop, the agent MUST have produced plain text
            // containing the marker (or at minimum, non-empty text).
            // If text is empty the orchestrator will mark the task
            // "no changes (agent made 0 edits)" and never commit.
            Assert.False(string.IsNullOrWhiteSpace(result.Text),
                $"Agent produced empty text. SessionId={result.SessionId}. " +
                "This is the silent-agent bug.");
            Assert.Contains("live-marker-12345", result.Text);
        }
        finally
        {
            try { Directory.Delete(worktree, recursive: true); } catch { }
            factory.Dispose();
        }
    }

    /// <summary>
    /// Reproduce the silent-agent bug from production: use the SAME
    /// prompt the orchestrator sends (task description + worktree
    /// context) and verify the LLM produces a non-empty response.
    /// </summary>
    [Fact]
    public async Task RunAsync_LiveLLM_RealTaskPrompt_ProducesFinalText()
    {
        var apiKey = Environment.GetEnvironmentVariable("FORGE_LIVE_LLM_KEY");
        if (string.IsNullOrEmpty(apiKey)) return;

        var provider = new ProviderConfig(
            Name: LlmProviders.KiloGateway,
            BaseUrl: "https://api.kilo.ai/api/gateway",
            ApiKey: apiKey,
            OrgId: null,
            DefaultModel: "minimax/minimax-m3");

        var factory = new OpenAICompatibleChatClientFactory();
        var runner = new MafAgentRunner(
            chatClientFactory: factory,
            config: new LlmConfig(provider),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: Path.Combine(Path.GetTempPath(), $"ph-live-md-{Guid.NewGuid():N}"));

        // Use the actual prompt shape from RunAgentExecutor.BuildPrompt.
        // Working directory points at a temp dir with a sample .cs file
        // so the LLM has a real surface to act on.
        var worktree = Path.Combine(Path.GetTempPath(), $"ph-real-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, "Program.cs"),
            "// existing program\nclass Program { static void Main() {}\n}\n");

        try
        {
            var prompt = $"""
                You are the CoreDev agent.

                Issue: task-test-001
                Title: Add a class-level XML doc comment to Program.cs
                Type: task
                Priority: 2

                Add an XML doc comment to the Program class in Program.cs. The comment should describe what the class does.

                Working directory: {worktree}
                Branch: agent/task-test-001 (base: main)
                """;

            var result = await runner.RunAsync(
                AgentType.CoreDev,
                prompt,
                sessionId: null,
                context: new Dictionary<string, object> { ["worktreePath"] = worktree },
                ct: default);

            // Diagnostic: surface what came back so we can see it in the test log
            // even when the assert passes.
            Console.WriteLine($"=== AGENT TEXT ({result.Text?.Length ?? 0} chars) ===");
            Console.WriteLine(result.Text ?? "<null>");
            Console.WriteLine($"=== SESSION ID ===");
            Console.WriteLine(result.SessionId ?? "<null>");

            Assert.False(string.IsNullOrWhiteSpace(result.Text),
                "Real-task prompt produced empty text. This is the production bug.");
        }
        finally
        {
            try { Directory.Delete(worktree, recursive: true); } catch { }
            factory.Dispose();
        }
    }

    /// <summary>
    /// Reproduce the production agent silence: long prompt with task
    /// that requires multiple bash iterations. This is the closest
    /// test to what the orchestrator does in production.
    /// </summary>
    [Fact]
    public async Task RunAsync_LiveLLM_MultiStepTask_ProducesFinalText()
    {
        var apiKey = Environment.GetEnvironmentVariable("FORGE_LIVE_LLM_KEY");
        if (string.IsNullOrEmpty(apiKey)) return;

        var provider = new ProviderConfig(
            Name: LlmProviders.KiloGateway,
            BaseUrl: "https://api.kilo.ai/api/gateway",
            ApiKey: apiKey,
            OrgId: null,
            DefaultModel: "minimax/minimax-m3");

        var factory = new OpenAICompatibleChatClientFactory();
        var runner = new MafAgentRunner(
            chatClientFactory: factory,
            config: new LlmConfig(provider),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: Path.Combine(Path.GetTempPath(), $"ph-live-md-{Guid.NewGuid():N}"));

        // Create a small repo-like workspace for the agent.
        var worktree = Path.Combine(Path.GetTempPath(), $"ph-multi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, "lib.cs"),
            "namespace MyLib { public class Helper { public static int Add(int a, int b) => a + b; } }\n");
        File.WriteAllText(Path.Combine(worktree, "tests.cs"),
            "// tests placeholder\n");

        try
        {
            // Same prompt shape the orchestrator sends.
            var prompt = $"""
                You are the CoreDev agent.

                Issue: task-multi-001
                Title: Add an XML doc comment to lib.cs
                Type: task
                Priority: 2

                Working directory: {worktree}
                Branch: agent/task-multi-001 (base: main)

                Add an XML doc comment to the `Helper.Add` method in lib.cs describing what the method does (it adds two integers). Save the file. Confirm the file was edited.
                """;

            var result = await runner.RunAsync(
                AgentType.CoreDev,
                prompt,
                sessionId: null,
                context: new Dictionary<string, object> { ["worktreePath"] = worktree },
                ct: default);

            Console.WriteLine($"=== MULTI-STEP TEXT ({result.Text?.Length ?? 0} chars) ===");
            Console.WriteLine(result.Text ?? "<null>");

            Assert.False(string.IsNullOrWhiteSpace(result.Text),
                "Multi-step task produced empty text -- silent-agent bug reproduces.");
        }
        finally
        {
            try { Directory.Delete(worktree, recursive: true); } catch { }
            factory.Dispose();
        }
    }
}
