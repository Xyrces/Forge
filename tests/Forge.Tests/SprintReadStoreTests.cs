using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Forge.Core;
using Forge.Core.Db;
using Forge.Dashboard.Features.Sprints;
using Xunit;

namespace Forge.Tests;

public sealed class SprintReadStoreTests : IDisposable
{
    private readonly IssueStore _issues = new(TempRoot.Instance.NewDbPath("sprint-reads"));
    private SprintStore Sprints => new(_issues);
    public void Dispose() => _issues.Dispose();

    [Fact]
    public async Task HistoryAndBoardQueryCountsDoNotGrowWithHistoricalSprints()
    {
        var active = await Sprints.CreateAsync(new("Active", "goal", DateTime.UtcNow, DateTime.UtcNow));
        var issue = await _issues.CreateAsync(new NewIssue("dev", "Current work"));
        await Sprints.AddIssueAsync(active.Id, issue.Id);
        var counted = new CountingFactory(_issues.Db);
        var reader = new SprintReadStore(counted);
        await reader.HistoryAsync("forge");
        Assert.Equal(3, counted.Commands.Count);
        counted.Commands.Clear(); await reader.BoardAsync("forge");
        Assert.Equal(4, counted.Commands.Count);
        for (var i = 0; i < 500; i++)
            await Sprints.CreateAsync(new("Old " + i, "A large goal need not be exported", DateTime.UtcNow.AddDays(-i), DateTime.UtcNow, SprintStatus.Completed));
        counted.Commands.Clear();
        var history = await reader.HistoryAsync("forge");
        Assert.Equal(3, counted.Commands.Count); Assert.Equal(501, history.Total);
        Assert.Equal(20, history.History.Count); Assert.NotNull(history.NextCursor);
        Assert.Equal(active.Id, history.Active!.Id);
        Assert.All(history.History, sprint => Assert.Empty(sprint.Goal));
        counted.Commands.Clear();
        var board = await reader.BoardAsync("forge");
        Assert.Equal(4, counted.Commands.Count); Assert.Single(board.Members);
        Assert.All(counted.Commands, sql => Assert.DoesNotContain("skill", sql));
    }

    [Fact]
    public async Task LargeSprintPagesStayBoundedAndRetainWholeSprintTotals()
    {
        var sprint = await Sprints.CreateAsync(new("Large", "goal", DateTime.UtcNow, DateTime.UtcNow));
        await SeedMembers(sprint.Id, 2500);
        var reader = new SprintReadStore(_issues.Db);
        var ids = new HashSet<string>(); string? cursor = null;
        do
        {
            var page = await reader.BoardAsync("forge", sprint.Id, 200, cursor);
            Assert.Equal(2500, page.Total); Assert.Equal(1250, page.Done);
            Assert.InRange(page.Members.Count, 1, 200);
            foreach (var row in page.Members) Assert.True(ids.Add(row.Issue.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(2500, ids.Count);
        var first = await reader.BoardAsync("forge", sprint.Id, int.MaxValue);
        Assert.Equal(200, first.Members.Count);
        await Assert.ThrowsAsync<ArgumentException>(() => reader.BoardAsync("another", sprint.Id, 200, first.NextCursor));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.BoardAsync("forge", sprint.Id, 200, "bad cursor"));
    }

    [Fact]
    public async Task MembershipFiltersBlockerPreviewAndQueueMatchDispatchOrdering()
    {
        var sprint = await Sprints.CreateAsync(new("Current", "goal", DateTime.UtcNow, DateTime.UtcNow));
        var ordinary = await _issues.CreateAsync(new NewIssue("dev", "Ordinary", Priority: 1));
        var booster = await _issues.CreateAsync(new NewIssue("dev", "Boosted", Priority: 3));
        var blocked = await _issues.CreateAsync(new NewIssue("dev", "Blocked"));
        var outside = await _issues.CreateAsync(new NewIssue("dev", "Outside this sprint"));
        await _issues.AddDependencyAsync(booster.Id, outside.Id, IssueDepKind.Blocks);
        foreach (var item in new[] { ordinary, booster, blocked }) await Sprints.AddIssueAsync(sprint.Id, item.Id);
        foreach (var type in new[] { "epic", "Story", "pr-watch" })
        {
            var item = await _issues.CreateAsync(new NewIssue(type, "Container")); await Sprints.AddIssueAsync(sprint.Id, item.Id);
        }
        for (var i = 0; i < 8; i++)
        {
            var blocker = await _issues.CreateAsync(new NewIssue("dev", "Blocker " + i));
            await _issues.TransitionAsync(blocker.Id, IssueStatus.Failed, "test failure");
            await _issues.AddDependencyAsync(blocker.Id, blocked.Id, IssueDepKind.Blocks);
        }
        var reader = new SprintReadStore(_issues.Db);
        var page = await reader.BoardAsync("forge", sprint.Id);
        Assert.Equal(3, page.Total);
        var blockedCard = Assert.Single(page.Members, row => row.Issue.Id == blocked.Id);
        Assert.Equal(8, blockedCard.BlockerCount); Assert.Equal(5, blockedCard.BlockedBy.Count);
        Assert.True(Assert.Single(page.Members, row => row.Issue.Id == booster.Id).Boosted);
        var queue = await reader.QueueAsync(sprint.Id, 1);
        Assert.Equal(2, queue.Total); Assert.Equal(1, queue.BlockedTotal);
        Assert.Equal(booster.Id, Assert.Single(queue.Ready).Issue.Id);
        var expected = (await _issues.ReadyAsync(0, sprint.Id)).Where(i => !AgentTaskTypes.IsContainer(i.Type) && i.Type != AgentTaskTypes.PrWatch).Take(1);
        Assert.Equal(expected.Select(i => i.Id), queue.Ready.Select(i => i.Issue.Id));
    }

    [Fact]
    public async Task HistoryCursorTieBreakersAndNoActiveFallbackAreStable()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < 7; i++) await Sprints.CreateAsync(new("History " + i, "goal", now, now, SprintStatus.Completed));
        var reader = new SprintReadStore(_issues.Db);
        var ids = new List<string>(); string? cursor = null;
        do { var page = await reader.HistoryAsync("forge", 2, cursor); ids.AddRange(page.History.Select(s => s.Id)); cursor = page.NextCursor; }
        while (cursor is not null);
        Assert.Equal(7, ids.Distinct().Count());
        Assert.Equal(ids.OrderDescending(StringComparer.Ordinal), ids);
        Assert.Equal(ids[0], (await reader.BoardAsync("forge")).Sprint!.Id);
        Assert.Null((await reader.BoardAsync("forge", "missing")).Sprint);
    }

    [Fact]
    public void ProjectAndSelectionChangesCancelRequestsAndRejectLateResults()
    {
        using var selection = new SprintSelection();
        var first = selection.Select("A", "same-id"); var token = selection.Token;
        var next = selection.Select("B", "same-id");
        Assert.True(token.IsCancellationRequested); Assert.False(selection.IsCurrent(first)); Assert.True(selection.IsCurrent(next));
        selection.Select("B", "other-sprint"); Assert.False(selection.IsCurrent(next));
    }

    private async Task SeedMembers(string sprintId, int count)
    {
        // Isolated fixture bulk load; no production queue or model execution.
        await using var connection = await _issues.Db.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        for (var index = 0; index < count; index++)
        {
            using var command = connection.CreateCommand(); command.Transaction = tx;
            command.CommandText = """
                INSERT INTO issue(id,short_id,type,title,status,priority,created_at,updated_at,metadata_json)
                VALUES(@id,@id,'dev','Fixture',@status,2,'2026-01-01 00:00:00.000','2026-01-01 00:00:00.000','{}');
                INSERT INTO sprint_issue(sprint_id,issue_id,added_at) VALUES(@sprint,@id,'2026-01-01 00:00:00.000');
                """;
            command.AddParam("@id", $"fixture-{index:D5}"); command.AddParam("@sprint", sprintId);
            command.AddParam("@status", index % 2 == 0 ? "Completed" : "Pending");
            await command.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private sealed class CountingFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public List<string> Commands { get; } = [];
        public ForgeDbProvider Provider => inner.Provider;
        public ISqlDialect Dialect => inner.Dialect;
        public ForgeSchemaProfile Profile => inner.Profile;
        public string ConnectionString => inner.ConnectionString;
        public string Qualifier => inner.Qualifier;
        public bool InitializeSchema => false;
        public DbConnection Open() => new CountingConnection(inner.Open(), Commands);
        public async Task<DbConnection> OpenAsync(CancellationToken ct = default) => new CountingConnection(await inner.OpenAsync(ct), Commands);
    }
    private sealed class CountingConnection(DbConnection inner, List<string> commands) : DbConnection
    {
        [AllowNull] public override string ConnectionString { get => inner.ConnectionString; set => inner.ConnectionString = value; }
        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;
        public override void ChangeDatabase(string name) => inner.ChangeDatabase(name);
        public override void Close() => inner.Close();
        public override void Open() => inner.Open();
        protected override DbTransaction BeginDbTransaction(IsolationLevel level) => inner.BeginTransaction(level);
        protected override DbCommand CreateDbCommand() => new CountingCommand(inner.CreateCommand(), commands);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
    private sealed class CountingCommand(DbCommand inner, List<string> commands) : DbCommand
    {
        [AllowNull] public override string CommandText { get => inner.CommandText; set => inner.CommandText = value; }
        public override int CommandTimeout { get => inner.CommandTimeout; set => inner.CommandTimeout = value; }
        public override CommandType CommandType { get => inner.CommandType; set => inner.CommandType = value; }
        public override bool DesignTimeVisible { get => inner.DesignTimeVisible; set => inner.DesignTimeVisible = value; }
        public override UpdateRowSource UpdatedRowSource { get => inner.UpdatedRowSource; set => inner.UpdatedRowSource = value; }
        protected override DbConnection? DbConnection { get => inner.Connection; set => inner.Connection = value; }
        protected override DbTransaction? DbTransaction { get => inner.Transaction; set => inner.Transaction = value; }
        protected override DbParameterCollection DbParameterCollection => inner.Parameters;
        public override void Cancel() => inner.Cancel();
        public override void Prepare() => inner.Prepare();
        protected override DbParameter CreateDbParameter() => inner.CreateParameter();
        public override int ExecuteNonQuery() { commands.Add(CommandText); return inner.ExecuteNonQuery(); }
        public override object? ExecuteScalar() { commands.Add(CommandText); return inner.ExecuteScalar(); }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) { commands.Add(CommandText); return inner.ExecuteReader(behavior); }
        public override async Task<object?> ExecuteScalarAsync(CancellationToken ct) { commands.Add(CommandText); return await inner.ExecuteScalarAsync(ct); }
        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken ct) { commands.Add(CommandText); return await inner.ExecuteReaderAsync(behavior, ct); }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
