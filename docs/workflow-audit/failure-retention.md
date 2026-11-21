# Failed work requires explicit resolution

The assembler no longer closes Failed tasks after an aging window. Failed tasks retain their failure status and dependency edges, keep their story/epic open, and prevent their active sprint from reporting completion. Existing held-work cards remain available; active sprint build state now identifies Failed/Blocked members and names operator triage rather than promising eventual automatic closure.

Historical source explicitly conflicts: commit `e062272f7eadfd434a30899dc645274b359d2ff5` (2026-08-18) described an operator request to fix stalled planning and implemented age-based abandonment. Its comments narrowed the no-auto-clear rule to recent failures. Current AGENTS still categorically says not to auto-clear Failed issues. This repair follows the current explicit instruction to retain failed work; it does not infer completion from elapsed time.

The legacy `FailureAgingDays` configuration field and assembler constructor argument remain accepted for compatibility, but no longer enable any closure. Composition stops wiring the deprecated policy. Explicit operator close/requeue and dependency removal retain their existing meanings. Completed/Closed bookkeeping, sprint grouping, and gates are unchanged; an explicit closure can still complete a sprint while the assembly gate holds the next sprint.

Regression coverage exercises a positive legacy aging window, aged failure with open story/epic and blocked dependent, repeated ticks with a failed active member, no emitted sprint-completion outcome, truthful held-work state, and explicit operator closure restoring dependency readiness and completion bookkeeping under a held assembly gate.
