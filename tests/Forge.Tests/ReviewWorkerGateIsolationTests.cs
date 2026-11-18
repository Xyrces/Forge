using Forge.Core;
using Forge.Reviewer;
using Xunit;

namespace Forge.Tests;

public sealed class ReviewWorkerGateIsolationTests
{
    [Fact]
    public async Task IndependentReviewsNeverShareBusyGateAndSameIdentityStillCoordinates()
    {
        var project = Guid.NewGuid().ToString("N");
        var held = new List<SemaphoreSlim>();
        try
        {
            // More keys than the former stripe count makes the regression deterministic.
            for (var i = 0; i < 128; i++)
            {
                var gate = ReviewWorkerAttempt.Gate(project, "task-" + i, AgentType.Reviewer);
                Assert.True(await gate.WaitAsync(0));
                held.Add(gate);
                Assert.Same(gate, ReviewWorkerAttempt.Gate(project, "task-" + i, AgentType.Reviewer));
                Assert.False(await ReviewWorkerAttempt.Gate(project, "task-" + i, AgentType.Reviewer).WaitAsync(0));
            }
            var otherProject = ReviewWorkerAttempt.Gate(project + "-other", "task-0", AgentType.Reviewer);
            Assert.True(await otherProject.WaitAsync(0));
            otherProject.Release();
            var qa = ReviewWorkerAttempt.Gate(project, "task-0", AgentType.QA);
            Assert.True(await qa.WaitAsync(0));
            qa.Release();
            var retirement = ReviewWorkerAttempt.RetirementGates(project, "task-0");
            Assert.Equal(2, retirement.Count);
            Assert.Equal(retirement, ReviewWorkerAttempt.RetirementGates(project, "task-0"));
        }
        finally { foreach (var gate in held) gate.Release(); }
    }
}
