# Sprint activation: Azure SQL qualification

On 2026-11-17, the isolated qualification harness ran the actual typed stores from commit `370979023719538b171cf4f7e84c5936e6706202` against Azure SQL. The assembly source revision was asserted before execution. Sprint store, activation validator and proposal-service sources are unchanged in integrated commit `207b1d3`.

The run passed these cases:

- Sprint creation, membership and proposal audit marker commit atomically.
- Replaying a committed proposal is rejected.
- Changed proposal task data and changed existing sprint membership are rejected.
- A duplicate member insertion rolls back the new sprint, memberships and proposal audit marker together.
- Two simultaneous activation attempts produce one successful active sprint with complete membership; the loser rejects or loses the SQL serialization race.

The harness was independently reviewed before execution. It required a fresh schema matching a fixed audit-name pattern, disabled automatic schema initialization, and created only five synthetic tables. The receipt confirms that `audit_sprint_activation_045634e8835e4571aa5f8479f3dd7ddf` was removed after qualification. Production tables were not seeded or changed. No model requests were made.

Local evidence is retained under `.portHorizon/sprint-activation-sql-qualification/`: `Program.cs`, `Proof.csproj`, `manifest.json` and `result.jsonl`. The result contains both the successful qualification and confirmed cleanup records; private connection values are not included.

This qualifies the listed SQL transaction behavior, not a new policy or a global hold-at-commit guarantee. Gate checks retain their documented evaluation-time semantics. Eligibility, cross-project/spec validation and endpoint bypass regressions are covered by the offline safety suite; browser presentation is qualified separately.
