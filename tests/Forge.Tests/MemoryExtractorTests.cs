using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Forge.Agents;
using Forge.Core;
using Forge.Orchestrator;
using Xunit;

namespace Forge.Tests;

/// <summary>
/// P5.5: auto-extract project memory from a model's response.
///
/// <para>
/// Two surfaces under test:
/// </para>
/// <list type="bullet">
///   <item><see cref="MemoryExtractor.ParseBlock"/> — the LLM
///         can be non-deterministic in its formatting; the parser
///         must be tolerant of extra whitespace, missing outer
///         wrappers, multi-block responses, and HTML-escaped
///         content.</item>
///   <item><see cref="MemoryExtractor.ExtractAsync"/> — calls
///         a (scripted) <see cref="IChatClient"/>, persists the
///         results to <see cref="MemoryStore"/>, and returns a
///         usable <see cref="ExtractionResult"/>.</item>
///   <item><see cref="MemoryExtractionStore"/> — audit log
///         round-trips through the v13 migration.</item>
/// </list>
/// </summary>
public class MemoryExtractorTests : IDisposable
{
    private readonly string _workDir;
    private readonly MemoryStore _memory;
    private readonly MemoryExtractionStore _extractions;
    private readonly StubbedChatClientFactory _factory = new();

    public MemoryExtractorTests()
    {
        _workDir = TempRoot.Instance.NewDirectory("memextractor");
        Directory.CreateDirectory(_workDir);
        // Force the v13 migration by constructing an IssueStore
        // against the same DB. The MemoryStore doesn't own
        // migrations, so we trigger them externally.
        var dbPath = Path.Combine(_workDir, "memory.db");
        _ = new IssueStore(dbPath);
        _memory = new MemoryStore(dbPath);
        _extractions = new MemoryExtractionStore(dbPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best-effort */ }
    }

    private MemoryExtractor NewExtractor()
        => new(_factory, new LlmConfig(new ProviderConfig("test", "", null, null, "test-model")), _memory,
            NullLogger<MemoryExtractor>.Instance);

    private ScriptedChatClient Script(params string[] replies)
    {
        var client = new ScriptedChatClient();
        foreach (var r in replies)
        {
            client.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, r)));
        }
        return client;
    }

    // -------- ParseBlock --------

    [Fact]
    public void ParseBlock_EmptyInput_ReturnsEmpty()
    {
        var items = MemoryExtractor.ParseBlock("");
        Assert.Empty(items);
    }

    [Fact]
    public void ParseBlock_NoMemoryTags_ReturnsEmpty()
    {
        // The model went off-script and just said "no insights".
        var items = MemoryExtractor.ParseBlock("No durable insights to record.");
        Assert.Empty(items);
    }

    [Fact]
    public void ParseBlock_SingleEntry()
    {
        var text = """
            <memory>
            <memory><key>forge/uses-maf-2026</key><value>Forge agents run on Microsoft Agent Framework 1.x</value></memory>
            </memory>
            """;
        var items = MemoryExtractor.ParseBlock(text);
        var item = Assert.Single(items);
        Assert.Equal("forge/uses-maf-2026", item.Key);
        Assert.Equal("Forge agents run on Microsoft Agent Framework 1.x", item.Value);
    }

    [Fact]
    public void ParseBlock_MultipleEntries()
    {
        var text = """
            <memory>
            <memory><key>a</key><value>alpha</value></memory>
            <memory><key>b</key><value>beta</value></memory>
            <memory><key>c</key><value>gamma</value></memory>
            </memory>
            """;
        var items = MemoryExtractor.ParseBlock(text);
        Assert.Equal(3, items.Count);
        Assert.Equal("alpha", items[0].Value);
        Assert.Equal("beta", items[1].Value);
        Assert.Equal("gamma", items[2].Value);
    }

    [Fact]
    public void ParseBlock_ToleratesExtraWhitespace()
    {
        var text = "<memory>\n\n   <memory><key>  k  </key><value>  v  </value></memory>\n\n</memory>";
        var items = MemoryExtractor.ParseBlock(text);
        var item = Assert.Single(items);
        Assert.Equal("k", item.Key);
        Assert.Equal("v", item.Value);
    }

    [Fact]
    public void ParseBlock_HandlesHtmlEscapedValues()
    {
        var text = """
            <memory>
            <memory><key>html-entities</key><value>Uses &lt;memory&gt; tags &amp; &quot;quotes&quot;</value></memory>
            </memory>
            """;
        var items = MemoryExtractor.ParseBlock(text);
        var item = Assert.Single(items);
        Assert.Contains("<memory>", item.Value);
        Assert.Contains("&", item.Value);
        Assert.Contains("\"quotes\"", item.Value);
    }

    [Fact]
    public void ParseBlock_SkipsEmptyKeyOrValue()
    {
        var text = """
            <memory>
            <memory><key>good</key><value>value</value></memory>
            <memory><key></key><value>value</value></memory>
            <memory><key>key</key><value></value></memory>
            </memory>
            """;
        var items = MemoryExtractor.ParseBlock(text);
        var item = Assert.Single(items);
        Assert.Equal("good", item.Key);
    }

    [Fact]
    public void ParseBlock_NoOuterWrapper_StillFindsEntries()
    {
        // Model forgot the outer <memory>...</memory>.
        var text = """
            <memory><key>bare</key><value>bare</value></memory>
            """;
        var items = MemoryExtractor.ParseBlock(text);
        var item = Assert.Single(items);
        Assert.Equal("bare", item.Key);
    }

    [Fact]
    public void ParseBlock_TruncatesVeryLongValues()
    {
        var huge = new string('x', 3000);
        var text = $"<memory><memory><key>k</key><value>{huge}</value></memory></memory>";
        var items = MemoryExtractor.ParseBlock(text);
        var item = Assert.Single(items);
        Assert.True(item.Value.Length <= 2000);
    }

    // -------- ExtractAsync --------

    [Fact]
    public async Task ExtractAsync_EmptyModelText_NoCallNoPersist()
    {
        var result = await NewExtractor().ExtractAsync("issue-1", null);
        Assert.Equal(0, result.ExtractedCount);
        Assert.Empty(result.PersistedKeys);
        Assert.Null(result.Error);
        var all = await _memory.RecallAsync();
        Assert.Empty(all);
    }

    [Fact]
    public async Task ExtractAsync_ParsesAndPersists()
    {
        var client = Script("""
            <memory>
            <memory><key>lesson-a</key><value>Always run dotnet build after schema migrations</value></memory>
            <memory><key>lesson-b</key><value>Headroom + token mode works for short sessions</value></memory>
            </memory>
            """);
        // Stuff the scripted client into the factory by hooking
        // the returned IChatClient. The factory ignores LlmConfig
        // + role and returns a fresh ScriptedChatClient, so we
        // need to grab the first one it returns.
        var _ = (IChatClient)client;
        // Re-create: the factory creates a new client per call.
        // We override by re-enqueueing on the same instance the
        // factory returns. StubbedChatClientFactory always
        // returns a *fresh* ScriptedChatClient; we can swap
        // our own factory impl by using a delegating factory.
        var scripted = Script("""
            <memory>
            <memory><key>lesson-a</key><value>Always run dotnet build after schema migrations</value></memory>
            <memory><key>lesson-b</key><value>Headroom + token mode works for short sessions</value></memory>
            </memory>
            """);
        var capturedFactory = new CapturingFactory(scripted);
        var extractor = new MemoryExtractor(capturedFactory, new LlmConfig(new ProviderConfig("test", "", null, null, "test-model")), _memory,
            NullLogger<MemoryExtractor>.Instance);

        var result = await extractor.ExtractAsync("issue-42",
            "long model response about schema work and Headroom", default);

        Assert.Equal(2, result.ExtractedCount);
        Assert.Equal(2, result.PersistedKeys.Count);
        Assert.All(result.PersistedKeys, k => Assert.StartsWith("extraction/issue-42/", k));
        Assert.Null(result.Error);
        // Persistence round-trip.
        var stored = await _memory.RecallAsync("extraction/issue-42/");
        Assert.Equal(2, stored.Count);
    }

    [Fact]
    public async Task ExtractAsync_NoMemoryBlock_NoPersist()
    {
        var scripted = Script("The model didn't follow the format.");
        var factory = new CapturingFactory(scripted);
        var extractor = new MemoryExtractor(factory, new LlmConfig(new ProviderConfig("test", "", null, null, "test-model")), _memory,
            NullLogger<MemoryExtractor>.Instance);
        var result = await extractor.ExtractAsync("issue-7", "source text", default);
        Assert.Equal(0, result.ExtractedCount);
        Assert.Empty(result.PersistedKeys);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ExtractAsync_LlmThrows_ReturnsErrorEnvelope()
    {
        var throwing = new ThrowingChatClient();
        var factory = new CapturingFactory(throwing);
        var extractor = new MemoryExtractor(factory, new LlmConfig(new ProviderConfig("test", "", null, null, "test-model")), _memory,
            NullLogger<MemoryExtractor>.Instance);
        var result = await extractor.ExtractAsync("issue-8", "source text", default);
        Assert.Equal(0, result.ExtractedCount);
        Assert.NotNull(result.Error);
        Assert.Contains("InvalidOperationException", result.Error);
    }

    [Fact]
    public async Task ExtractAsync_NamespacesKeys()
    {
        var scripted = Script("""
            <memory>
            <memory><key>No-Spaces Allowed!</key><value>v</value></memory>
            </memory>
            """);
        var factory = new CapturingFactory(scripted);
        var extractor = new MemoryExtractor(factory, new LlmConfig(new ProviderConfig("test", "", null, null, "test-model")), _memory,
            NullLogger<MemoryExtractor>.Instance);
        var result = await extractor.ExtractAsync("ABC", "x", default);
        Assert.Single(result.PersistedKeys);
        var key = result.PersistedKeys[0];
        // issueId is preserved as-is; only the *key part* is sanitized.
        Assert.StartsWith("extraction/ABC/", key);
        // Sanitize lowercases + collapses non-alnum to dash.
        Assert.DoesNotContain(" ", key);
        Assert.DoesNotContain("!", key);
        Assert.EndsWith("/no-spaces-allowed", key);
    }

    // -------- MemoryExtractionStore audit log --------

    [Fact]
    public async Task RecordAsync_AndListForTask_RoundTrip()
    {
        var result = new ExtractionResult(
            IssueId: "task-1", SourceChars: 1234, ExtractedCount: 2,
            PersistedKeys: new[] { "extraction/task-1/a", "extraction/task-1/b" },
            Error: null);
        await _extractions.RecordAsync(result);
        var list = await _extractions.ListForTaskAsync("task-1");
        var row = Assert.Single(list);
        Assert.Equal("task-1", row.TaskId);
        Assert.Equal(1234, row.SourceChars);
        Assert.Equal(2, row.ExtractedCount);
        Assert.Equal(2, row.PersistedKeys.Count);
        Assert.Null(row.Error);
    }

    [Fact]
    public async Task RecordAsync_WithError_StoresErrorAndZeroKeys()
    {
        var result = new ExtractionResult(
            IssueId: "task-2", SourceChars: 500, ExtractedCount: 0,
            PersistedKeys: Array.Empty<string>(),
            Error: "TimeoutException: timed out");
        await _extractions.RecordAsync(result);
        var list = await _extractions.ListForTaskAsync("task-2");
        var row = Assert.Single(list);
        Assert.Equal(0, row.ExtractedCount);
        Assert.Empty(row.PersistedKeys);
        Assert.Equal("TimeoutException: timed out", row.Error);
    }

    [Fact]
    public async Task ListForTask_OtherTasks_DoesNotLeak()
    {
        await _extractions.RecordAsync(new ExtractionResult(
            "task-a", 100, 1, new[] { "k" }, null));
        await _extractions.RecordAsync(new ExtractionResult(
            "task-b", 200, 2, new[] { "x", "y" }, null));
        var listA = await _extractions.ListForTaskAsync("task-a");
        Assert.Single(listA);
        var listB = await _extractions.ListForTaskAsync("task-b");
        Assert.Single(listB);
    }
}

// -------- Test doubles --------

/// <summary>
/// IChatClientFactory that always returns a pre-canned
/// <see cref="IChatClient"/>. Used so we can assert on the
/// exact response the model "produced" without depending on
/// <see cref="StubbedChatClientFactory"/>'s enqueue-per-call
/// semantics.
/// </summary>
internal sealed class CapturingFactory : IChatClientFactory
{
    private readonly IChatClient _client;
    public CapturingFactory(IChatClient client) { _client = client; }
    public IChatClient Create(LlmConfig config, AgentType role, string? projectId = null) => _client;
}

internal sealed class ThrowingChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("simulated LLM failure");
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("simulated LLM failure");
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
