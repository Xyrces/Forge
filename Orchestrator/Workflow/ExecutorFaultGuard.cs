using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Forge.Orchestrator.Workflow;

/// <summary>
/// Executor fault guard. MAF's in-process execution swallows executor
/// faults — the run halts with no exception surfaced anywhere, and
/// only the dispatch checkpoint hints where it stopped (the
/// 2026-08-01 phantom-dispatch saga: silent non-fast-forward push
/// rejections, silent worktree checkout failures). Wrapping every
/// stage delegate in this logs the fault AT THE SOURCE (stage name +
/// exception) before rethrowing into the halt, so the journal always
/// has the real error within one timestamp of the checkpoint.
/// </summary>
internal static class ExecutorFaultGuard
{
    public static Func<TIn, IWorkflowContext, CancellationToken, ValueTask<TOut>> Wrap<TIn, TOut>(
        string stage, ILogger logger, Func<TIn, IWorkflowContext, CancellationToken, ValueTask<TOut>> fn)
        => async (input, ctx, ct) =>
        {
            try
            {
                return await fn(input, ctx, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "workflow stage '{Stage}' faulted (MAF in-process execution would swallow this into a silent halt)",
                    stage);
                throw;
            }
        };
}
