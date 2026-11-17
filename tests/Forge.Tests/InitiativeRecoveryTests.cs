using Forge.Agents;
using Forge.Core;
using Forge.Core.Initiatives;
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
        => new(Service,()=>["test"],_=>Ingestion,_=>new SpecStore(_issues),refine,_bus,NullLogger<ProductRefinementQueue>.Instance);
    private static async Task Until(Func<Task<bool>> condition)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(!await condition()) await Task.Delay(20,timeout.Token);
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
    public ValueTask DisposeAsync()=>_issues.DisposeAsync();
}
