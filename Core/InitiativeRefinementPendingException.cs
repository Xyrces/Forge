namespace Forge.Core;

public sealed class InitiativeRefinementPendingException(string state)
    : InvalidOperationException($"Initiative refinement is {state}. Wait for completion, or retry failed refinement from Intake Proposals before changing stages.");
