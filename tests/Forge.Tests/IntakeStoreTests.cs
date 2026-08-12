using Forge.Core;
using Xunit;

namespace Forge.Tests;

public class IntakeStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly IssueStore _issues;
    private readonly IntakeStore _intake;

    public IntakeStoreTests()
    {
        _dbPath = TempRoot.Instance.NewDbPath("intake");
        _issues = new IssueStore(_dbPath);
        _intake = new IntakeStore(_issues);
    }

    public void Dispose()
    {
        try { _issues.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task CreateAsync_StoresSession()
    {
        var session = await _intake.CreateAsync("PortHorizon", "P1.4 intake session", default);
        Assert.False(string.IsNullOrEmpty(session.Id));
        Assert.StartsWith("intake-", session.Id);
        Assert.Equal("PortHorizon", session.ProjectId);
        Assert.Equal("P1.4 intake session", session.Title);
        Assert.Empty(session.Messages);
    }

    [Fact]
    public async Task CreateAsync_EmptyTitle_DefaultsToNewIntake()
    {
        var session = await _intake.CreateAsync("proj", null, default);
        Assert.Equal("New intake", session.Title);

        var session2 = await _intake.CreateAsync("proj", "  ", default);
        Assert.Equal("New intake", session2.Title);
    }

    [Fact]
    public async Task CreateAsync_ProjectIdRequired()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _intake.CreateAsync("", null, default));
    }

    [Fact]
    public async Task GetAsync_RoundTripsMessages()
    {
        var session = await _intake.CreateAsync("proj", "t", default);
        var user = await _intake.AppendMessageAsync(session.Id,
            new NewIntakeMessage(IntakeMessageRole.User, "hello"), default);
        var assistant = await _intake.AppendMessageAsync(session.Id,
            new NewIntakeMessage(IntakeMessageRole.Assistant, "hi there"), default);
        var system = await _intake.AppendMessageAsync(session.Id,
            new NewIntakeMessage(IntakeMessageRole.System, "Proposed epic: epic-1", ProposedEpicId: "epic-1", ProposedEpicTitle: "Demo epic"), default);

        var fetched = await _intake.GetAsync(session.Id, default);

        Assert.NotNull(fetched);
        Assert.Equal(3, fetched!.Messages.Count);
        Assert.Equal(IntakeMessageRole.User, fetched.Messages[0].Role);
        Assert.Equal("hello", fetched.Messages[0].Content);
        Assert.Equal(IntakeMessageRole.Assistant, fetched.Messages[1].Role);
        Assert.Equal(IntakeMessageRole.System, fetched.Messages[2].Role);
        Assert.Equal("epic-1", fetched.Messages[2].ProposedEpicId);
        Assert.Equal("Demo epic", fetched.Messages[2].ProposedEpicTitle);
        // updatedAt advances on each append. Compare the two STORED
        // timestamps (both parsed at millisecond precision) — the
        // in-memory session.CreatedAt keeps full tick precision, which
        // races the ms-truncated DB round-trip and flakes.
        Assert.True(fetched.UpdatedAt >= fetched.CreatedAt);
    }

    [Fact]
    public async Task GetAsync_MissingSession_ReturnsNull()
    {
        var fetched = await _intake.GetAsync("intake-does-not-exist", default);
        Assert.Null(fetched);
    }

    [Fact]
    public async Task ListAsync_ReturnsAllSessionsByUpdatedAtDesc()
    {
        var s1 = await _intake.CreateAsync("p1", "first", default);
        await Task.Delay(20); // ensure updated_at difference
        var s2 = await _intake.CreateAsync("p2", "second", default);
        await Task.Delay(20);
        await _intake.AppendMessageAsync(s1.Id, new NewIntakeMessage(IntakeMessageRole.User, "bump"), default); // s1 now most recent

        var all = await _intake.ListAsync(default);

        Assert.Equal(2, all.Count);
        Assert.Equal(s1.Id, all[0].Id); // most recently updated first
        Assert.Equal(s2.Id, all[1].Id);
        Assert.Single(all[0].Messages); // includes the "bump"
        Assert.Empty(all[1].Messages);
    }

    [Fact]
    public async Task AppendMessageAsync_SessionNotFound_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _intake.AppendMessageAsync("intake-nope",
                new NewIntakeMessage(IntakeMessageRole.User, "x"), default));
    }

    [Fact]
    public async Task AppendMessageAsync_Questions_RoundTrip()
    {
        var s = await _intake.CreateAsync("p", "t", default);
        await _intake.AppendMessageAsync(s.Id,
            new NewIntakeMessage(IntakeMessageRole.Assistant, "Q?", Questions: new[]
            {
                new IntakeQuestion("Which transport?", new[] { "Kafka", "Azure Service Bus" }),
                new IntakeQuestion("Free-form question?", Array.Empty<string>()),
            }), default);

        var loaded = await _intake.GetAsync(s.Id, default);
        var msg = loaded!.Messages.Single();
        Assert.NotNull(msg.Questions);
        Assert.Equal(2, msg.Questions!.Count);
        Assert.Equal("Which transport?", msg.Questions[0].Question);
        Assert.Equal(new[] { "Kafka", "Azure Service Bus" }, msg.Questions[0].Options);
        Assert.Empty(msg.Questions[1].Options);
    }

    [Fact]
    public async Task AppendMessageAsync_NoQuestions_NullRoundTrip()
    {
        var s = await _intake.CreateAsync("p", "t", default);
        await _intake.AppendMessageAsync(s.Id,
            new NewIntakeMessage(IntakeMessageRole.Assistant, "plain"), default);

        var loaded = await _intake.GetAsync(s.Id, default);
        Assert.Null(loaded!.Messages.Single().Questions);
    }

    [Fact]
    public async Task AppendMessageAsync_EmptyContent_Throws()
    {
        var s = await _intake.CreateAsync("p", "t", default);
        await Assert.ThrowsAsync<ArgumentException>(
            () => _intake.AppendMessageAsync(s.Id,
                new NewIntakeMessage(IntakeMessageRole.User, ""), default));
    }

    [Fact]
    public async Task SetMessagesAsync_ReplacesAllMessages()
    {
        var s = await _intake.CreateAsync("p", "t", default);
        await _intake.AppendMessageAsync(s.Id, new NewIntakeMessage(IntakeMessageRole.User, "old1"), default);
        await _intake.AppendMessageAsync(s.Id, new NewIntakeMessage(IntakeMessageRole.User, "old2"), default);

        await _intake.SetMessagesAsync(s.Id, new[]
        {
            new NewIntakeMessage(IntakeMessageRole.User, "fresh1"),
            new NewIntakeMessage(IntakeMessageRole.Assistant, "fresh2"),
        }, default);

        var fetched = await _intake.GetAsync(s.Id, default);
        Assert.Equal(2, fetched!.Messages.Count);
        Assert.Equal("fresh1", fetched.Messages[0].Content);
        Assert.Equal("fresh2", fetched.Messages[1].Content);
        Assert.Equal(IntakeMessageRole.Assistant, fetched.Messages[1].Role);
    }
}

public class NullIntakeStoreTests
{
    [Fact]
    public async Task ListAsync_ReturnsEmpty()
    {
        var s = new NullIntakeStore();
        var all = await s.ListAsync(default);
        Assert.Empty(all);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull()
    {
        var s = new NullIntakeStore();
        var fetched = await s.GetAsync("anything", default);
        Assert.Null(fetched);
    }

    [Fact]
    public async Task CreateAsync_Throws()
    {
        var s = new NullIntakeStore();
        await Assert.ThrowsAsync<NotSupportedException>(
            () => s.CreateAsync("proj", null, default));
    }
}
