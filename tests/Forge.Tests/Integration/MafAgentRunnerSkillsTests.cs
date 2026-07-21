using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Core;
using Xunit;

namespace Forge.Tests.Integration;

/// <summary>
/// P1 tests: <see cref="MafAgentRunner"/> loads skills from
/// <see cref="ISkillSource"/> and includes them in the MAF agent's
/// <c>instructions:</c> parameter. These tests use
/// <see cref="InMemorySkillSource"/> (no SQLite) and a custom
/// <see cref="CapturingChatClient"/> that records the ChatOptions
/// passed to the underlying IChatClient so we can assert what the
/// LLM actually saw.
/// </summary>
public class MafAgentRunnerSkillsTests
{
    [Fact]
    public async Task RunAsync_NoSkillSource_BehaviorUnchanged()
    {
        // No skills source => the agent instructions match the legacy
        // path (just the role .md description). Backward-compatible.
        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var factory = new ScriptingFactory(scripted);
        var runner = new MafAgentRunner(
            chatClientFactory: factory,
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: null,
            rolePromptsRoot: Path.Combine(Path.GetTempPath(), $"ph-no-md-{Guid.NewGuid():N}"));

        var result = await runner.RunAsync(AgentType.CoreDev, "do thing", sessionId: null, ct: default);

        Assert.Equal("ok", result.Text);
        var instructions = factory.LastInstructions;
        Assert.NotNull(instructions);
        // Without a skills source, instructions contain only the role fallback.
        Assert.Contains("coredev", instructions!, StringComparison.Ordinal);
        Assert.DoesNotContain("## Project skills", instructions!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithSkills_AppendsSkillContentToInstructions()
    {
        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var factory = new ScriptingFactory(scripted);
        var skills = new InMemorySkillSource(new Dictionary<AgentType, IReadOnlyList<SkillContent>>
        {
            [AgentType.CoreDev] = new[]
            {
                new SkillContent("ecs-style", "How we write ECS code", "Use components over inheritance."),
            },
        });
        var runner = new MafAgentRunner(
            chatClientFactory: factory,
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: skills,
            rolePromptsRoot: Path.Combine(Path.GetTempPath(), $"ph-no-md-{Guid.NewGuid():N}"));

        await runner.RunAsync(AgentType.CoreDev, "do thing", sessionId: null, ct: default);

        var instructions = factory.LastInstructions;
        Assert.NotNull(instructions);
        Assert.Contains("## Project skills", instructions!, StringComparison.Ordinal);
        Assert.Contains("ecs-style", instructions!, StringComparison.Ordinal);
        Assert.Contains("How we write ECS code", instructions!, StringComparison.Ordinal);
        Assert.Contains("Use components over inheritance.", instructions!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithSkills_DoesNotPolluteUserPrompt()
    {
        // P1 bug fix: previously, instructions were prepended to the
        // user prompt. The agent should see them only via the system
        // instructions, not as a user message prefix.
        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var factory = new ScriptingFactory(scripted);
        var skills = new InMemorySkillSource(new Dictionary<AgentType, IReadOnlyList<SkillContent>>
        {
            [AgentType.CoreDev] = new[]
            {
                new SkillContent("test-skill", null, "SECRET-MARKER-12345"),
            },
        });
        var runner = new MafAgentRunner(
            chatClientFactory: factory,
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: skills,
            rolePromptsRoot: Path.Combine(Path.GetTempPath(), $"ph-no-md-{Guid.NewGuid():N}"));

        await runner.RunAsync(AgentType.CoreDev, "do thing", sessionId: null, ct: default);

        var prompt = factory.LastPrompt;
        Assert.NotNull(prompt);
        Assert.Equal("do thing", prompt);
        Assert.DoesNotContain("SECRET-MARKER-12345", prompt!);
    }

    [Fact]
    public async Task RunAsync_SkillLoadingFails_DispatchStillCompletes()
    {
        // The runner must not let a skill-store outage kill a dispatch.
        // We simulate by passing a source that always throws.
        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var factory = new ScriptingFactory(scripted);
        var failing = new ThrowingSkillSource(new InvalidOperationException("db locked"));
        var runner = new MafAgentRunner(
            chatClientFactory: factory,
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: failing,
            rolePromptsRoot: Path.Combine(Path.GetTempPath(), $"ph-no-md-{Guid.NewGuid():N}"));

        var result = await runner.RunAsync(AgentType.CoreDev, "do thing", sessionId: null, ct: default);

        Assert.Equal("ok", result.Text);
        var instructions = factory.LastInstructions;
        Assert.NotNull(instructions);
        // No skills appended because the source threw; the dispatch still
        // completes with the role instructions only.
        Assert.DoesNotContain("## Project skills", instructions!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PerRoleSkillIsolation()
    {
        // Skills attached to CoreDev must not bleed into Reviewer.
        var scripted = new ScriptedChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok-core")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok-reviewer")));
        var factory = new ScriptingFactory(scripted);
        var skills = new InMemorySkillSource(new Dictionary<AgentType, IReadOnlyList<SkillContent>>
        {
            [AgentType.CoreDev]   = new[] { new SkillContent("core-only", null, "C") },
            [AgentType.Reviewer]  = new[] { new SkillContent("reviewer-only", null, "R") },
        });
        var runner = new MafAgentRunner(
            chatClientFactory: factory,
            config: new LlmConfig(new ProviderConfig(LlmProviders.Stub, "", null, null, "stub-model")),
            roles: new RoleAgentRegistry(),
            logger: NullLogger<MafAgentRunner>.Instance,
            skills: skills,
            rolePromptsRoot: Path.Combine(Path.GetTempPath(), $"ph-no-md-{Guid.NewGuid():N}"));

        await runner.RunAsync(AgentType.CoreDev, "x", sessionId: null, ct: default);
        var coreInstructions = factory.LastInstructions!;
        Assert.Contains("core-only", coreInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewer-only", coreInstructions, StringComparison.Ordinal);

        await runner.RunAsync(AgentType.Reviewer, "x", sessionId: null, ct: default);
        var reviewerInstructions = factory.LastInstructions!;
        Assert.Contains("reviewer-only", reviewerInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("core-only", reviewerInstructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Wraps a pre-built <see cref="ScriptedChatClient"/> so the test can
    /// record the <c>instructions</c> + <c>messages</c> passed to it.
    /// Without this, we'd need a custom IChatClient to intercept the
    /// chat options.
    /// </summary>
    private sealed class ScriptingFactory : IChatClientFactory
    {
        private readonly ScriptedChatClient _inner;
        public ScriptingFactory(ScriptedChatClient inner) { _inner = inner; }
        public string? LastInstructions { get; private set; }
        public string? LastPrompt { get; private set; }

        public IChatClient Create(LlmConfig config, AgentType role) => new CapturingClient(_inner, this);

        private sealed class CapturingClient : IChatClient
        {
            private readonly ScriptedChatClient _inner;
            private readonly ScriptingFactory _owner;
            public CapturingClient(ScriptedChatClient inner, ScriptingFactory owner)
            { _inner = inner; _owner = owner; }
            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                _owner.LastInstructions = options?.Instructions;
                _owner.LastPrompt = string.Concat(messages.Select(m => m.Text));
                return await _inner.GetResponseAsync(messages, options, cancellationToken);
            }
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                _owner.LastInstructions = options?.Instructions;
                _owner.LastPrompt = string.Concat(messages.Select(m => m.Text));
                await foreach (var u in _inner.GetStreamingResponseAsync(messages, options, cancellationToken))
                    yield return u;
            }
            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() => _inner.Dispose();
        }
    }

    private sealed class ThrowingSkillSource : ISkillSource
    {
        private readonly Exception _ex;
        public ThrowingSkillSource(Exception ex) { _ex = ex; }
        public Task<IReadOnlyList<SkillContent>> LoadForRoleAsync(AgentType role, CancellationToken ct = default)
            => throw _ex;
    }
}
