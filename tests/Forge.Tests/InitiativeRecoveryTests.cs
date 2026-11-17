using Forge.Agents;
using Forge.Core;
using Forge.Core.Initiatives;
using Forge.Core.Db;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Forge.Dashboard;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Forge.Tests;

public sealed class InitiativeRecoveryTests : IAsyncDisposable
{
    private readonly IssueStore _issues = new(TempRoot.Instance.NewDbPath("initiative-recovery"), "test");
    private readonly InMemoryDashboardEventBus _bus = new();
    private InitiativeProposalStore Proposals => new(_issues);
    private InitiativeIngestionStore Ingestion => new(_issues);
    private InitiativeApprovalService Service => new(Proposals, _ => Ingestion, _bus);
    private async Task<InitiativeProposal> Approved()
    {
        var session=await new IntakeStore(_issues).CreateAsync("test","discussed");
        var draft=await Proposals.CreateAsync(new("test",Guid.NewGuid().ToString(),"user","approved scope","body",SessionId:session.Id));
        return await Proposals.ApproveAsync("test",draft.Id,draft.Revision,draft.Hash,"test operator");
    }
    private ProductRefinementQueue Queue(Func<string,string,CancellationToken,Task<SpecRecord?>> refine)
    {
        var queue = new ProductRefinementQueue(Service,()=>["test"],_=>Ingestion,_=>new SpecStore(_issues),refine,_bus,NullLogger<ProductRefinementQueue>.Instance);
        _ = queue.RunAsync(CancellationToken.None);
        return queue;
    }
    private static async Task Until(Func<Task<bool>> condition)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(!await condition()) await Task.Delay(20,timeout.Token);
    }
    [Fact]
    public async Task ConstructionDoesNotClaimBeforeControllerAdmissionStartsLoop()
    {
        var approved=await Approved();
        await Ingestion.IngestAsync(approved);
        var calls=0;
        await using var queue=new ProductRefinementQueue(Service,()=>["test"],_=>Ingestion,_=>new SpecStore(_issues),
            (_,_,_)=> { calls++; return Task.FromResult<SpecRecord?>(null); },_bus,NullLogger<ProductRefinementQueue>.Instance);
        await Task.Delay(100);
        Assert.Equal(0,calls);
        Assert.Equal("Pending",(await Ingestion.GetAsync(approved.Id,1))!.RefinementState);
        Assert.Equal(0,(await Ingestion.GetAsync(approved.Id,1))!.Attempts);
    }
    [Fact]
    public async Task FailedAcknowledgementRetriesOnlyDatabaseWrite()
    {
        var db=new FailOnceFactory(new SqliteConnectionFactory($"Data Source={TempRoot.Instance.NewDbPath("ack-failure")}"));
        await using var issues=new IssueStore(db,"test");
        var proposals=new InitiativeProposalStore(issues);
        var target=new InitiativeIngestionStore(issues);
        var session=await new IntakeStore(issues).CreateAsync("test","scope");
        var draft=await proposals.CreateAsync(new("test","source","user","scope","body",SessionId:session.Id));
        await proposals.ApproveAsync("test",draft.Id,1,draft.Hash,"operator");
        var service=new InitiativeApprovalService(proposals,_=>target,_bus);
        var logger=new PassFailureLogger();
        var calls=0;
        await using var queue=new ProductRefinementQueue(service,()=>["test"],_=>target,_=>new SpecStore(issues),
            (_,_,_)=> { calls++; db.FailNext=true; throw new InvalidOperationException("model failed"); },_bus,logger);
        _=queue.RunAsync(CancellationToken.None);
        await logger.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Running",(await target.GetAsync(draft.Id,1))!.RefinementState);
        await queue.ReconcileAsync(default); // previous pass is finished; retry acknowledgement without generation
        Assert.Equal("Failed",(await target.GetAsync(draft.Id,1))!.RefinementState);
        Assert.Equal(1,calls);
    }
    [Fact]
    public async Task LostEvent_StartupAdoptsCommittedIngestionAndRefinesOnce()
    {
        var approved=await Approved();
        var committed=await Ingestion.IngestAsync(approved); // simulate crash before primary acknowledgement/event
        var calls=0;
        await using var queue=Queue(async (_,id,ct)=> { Interlocked.Increment(ref calls); return await new SpecStore(_issues).UpdateBodyAsync(id,new("refined","product:test"),ct); });
        await Until(async()=>(await Ingestion.GetAsync(approved.Id,1))?.RefinementState=="Completed");
        Assert.Equal(1,calls);
        Assert.Equal(committed.EpicId,(await Proposals.GetAsync("test",approved.Id))!.EpicId);
        Assert.Single(await _issues.ListAsync(new IssueFilter()));
        Assert.Equal(SpecStatus.ReadyForDesign,(await new SpecStore(_issues).GetAsync(committed.SpecId))!.Status);
        _bus.Publish(new(DateTime.UtcNow,"intake.epic.accepted",null,"duplicate hint"));
        await Task.Delay(100);
        Assert.Equal(1,calls);
    }
    [Fact]
    public async Task EventAloneCannotAuthorizeGenerationOrIngestion()
    {
        var calls=0;
        await using var queue=Queue((_,_,_)=> { Interlocked.Increment(ref calls); return Task.FromResult<SpecRecord?>(null); });
        _bus.Publish(new(DateTime.UtcNow,"intake.epic.accepted",null,"forged hint",new Dictionary<string,object?>{{"epicId","epic-1"},{"projectId","test"}}));
        await Task.Delay(150);
        Assert.Equal(0,calls); Assert.Empty(await _issues.ListAsync(new IssueFilter()));
    }
    [Fact]
    public async Task InterruptedRunningWorkRequiresExplicitRetry()
    {
        var approved=await Approved(); await Ingestion.IngestAsync(approved);
        Assert.True(await Ingestion.TryBeginRefinementAsync(approved.Id,1));
        var calls=0;
        await using var queue=Queue(async(_,id,ct)=> { Interlocked.Increment(ref calls); return await new SpecStore(_issues).UpdateBodyAsync(id,new("refined","product:test"),ct); });
        await Until(async()=>(await Ingestion.GetAsync(approved.Id,1))?.RefinementState=="Failed");
        Assert.Equal(0,calls);
        Assert.Contains("uncertain",(await Ingestion.GetAsync(approved.Id,1))!.LastError);
        Assert.True(await Service.RetryRefinementAsync("test",approved.Id,1));
        await Until(async()=>(await Ingestion.GetAsync(approved.Id,1))?.RefinementState=="Completed");
        Assert.Equal(1,calls);
    }
    [Fact]
    public async Task RefinementFailureIsDurableAndHintsDoNotRetryIt()
    {
        var approved=await Approved(); var calls=0;
        await using var queue=Queue((_,_,_)=> { Interlocked.Increment(ref calls); throw new InvalidOperationException("fixture provider failure"); });
        await Until(async()=>(await Ingestion.GetAsync(approved.Id,1))?.RefinementState=="Failed");
        for(var i=0;i<5;i++) _bus.Publish(new(DateTime.UtcNow,"intake.epic.accepted",null,"duplicate"));
        await Task.Delay(150); Assert.Equal(1,calls);
        Assert.Equal(1,(await Ingestion.GetAsync(approved.Id,1))!.Attempts);
    }
    [Fact]
    public async Task RecoveryProcessesBeyondFirstPageWithoutRetryingFailedGeneration()
    {
        var approved=new List<InitiativeProposal>();
        for(var i=0;i<51;i++) approved.Add(await Approved());
        var calls=0;
        await using var queue=Queue(async(_,id,ct)=>
        {
            if(Interlocked.Increment(ref calls)==1) throw new InvalidOperationException("first item failed");
            return await new SpecStore(_issues).UpdateBodyAsync(id,new("refined","product:test"),ct);
        });
        await Until(async()=>
        {
            var receipts=await Task.WhenAll(approved.Select(p=>Ingestion.GetAsync(p.Id,1)));
            return receipts.All(r=>r?.RefinementState is "Completed" or "Failed");
        });
        var final=await Task.WhenAll(approved.Select(p=>Ingestion.GetAsync(p.Id,1)));
        Assert.Equal(50,final.Count(r=>r!.RefinementState=="Completed"));
        Assert.Single(final.Where(r=>r!.RefinementState=="Failed"));
        Assert.Equal(51,calls);
    }
    public ValueTask DisposeAsync()=>_issues.DisposeAsync();

    private sealed class FailOnceFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        public bool FailNext;
        public ForgeDbProvider Provider=>inner.Provider;
        public ISqlDialect Dialect=>inner.Dialect;
        public ForgeSchemaProfile Profile=>inner.Profile;
        public string ConnectionString=>inner.ConnectionString;
        public string Qualifier=>inner.Qualifier;
        public DbConnection Open()=>inner.Open();
        public Task<DbConnection> OpenAsync(CancellationToken ct=default)
        {
            if(FailNext) { FailNext=false; throw new InvalidOperationException("database temporarily unavailable"); }
            return inner.OpenAsync(ct);
        }
    }
    private sealed class PassFailureLogger : ILogger<ProductRefinementQueue>
    {
        public TaskCompletionSource Failed { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>true;
        public void Log<TState>(LogLevel level,EventId id,TState state,Exception? error,Func<TState,Exception?,string> formatter)
        {
            if(formatter(state,error).StartsWith("Initiative reconciliation failed",StringComparison.Ordinal)) Failed.TrySetResult();
        }
    }
}
