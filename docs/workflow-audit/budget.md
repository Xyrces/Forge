# Budget accuracy audit — 2026-11-17

## Confirmed cause

The pilot's $24.809140 is conservative authorization accounting, **not an OpenAI bill**. The operator explicitly reports **$1.01** for the pilot. That is accepted as operator-reported cost, distinct from an independently retrieved provider bill. The 43 historical ledger requests are completed with no outstanding holds. Their rows and the $25 authorization ceiling remain unchanged.

`Orchestrator/Control/BudgetRequestPolicy.cs` settled every input token at $10/million and output at $15/million. These are deliberately pessimistic reservation rates covering long context and long cache writes. Applying them after usage arrives ignores cache discounts. Exact input counting fixed reservation size, but did not fix settlement prices.

`Agents/ActivityTrackingChatClient.cs` also ignored OpenAI's typed `CachedInputTokenCount`, recognizing only Anthropic additional counters. Historical per-run cache totals therefore cannot reconstruct the missing breakdown. OpenAI total input already includes cached reads; adding them again would overstate context. Old budget rows and transcripts do not retain per-response billing categories, and native requests use `store=false`.

No spending UI existed before this audit. The new display must distinguish usage estimates, conservative legacy/fallback accounting, and outstanding holds. Available authorization after those amounts is not remaining provider credit. Billing remains unknown until reconciled.

## Verified rates and future settlement

The host's configured model was confirmed read-only as `gpt-6.1-sol`. The supported profile remains direct global Responses, Standard tier, text and functions. [Official Sol pricing](https://developers.openai.com/api/docs/models/gpt-6.1-sol.md), checked 2026-11-17, gives these per-million rates: ordinary input $2, cached reads $0.10, output $10. Above 272,000 input tokens, input/cache rates double and output becomes $15 for the full request. Maximum input is 922,000; context is 1,050,000; maximum output is 128,000.

The [caching guide](https://developers.openai.com/api/docs/guides/prompt-caching.md) defines disjoint reads and writes. Short writes cost $2.50 and 12-hour writes $5 per million at normal context. Their TTL counts must sum to total writes. Ordinary input equals total input minus reads minus writes; write prices replace ordinary prices. Missing fields cannot be assumed zero. An explicit zero-write count does not require TTL details. When writes are omitted but total usage and cached reads are valid, the calculator prices cached reads normally and upper-bounds remaining input at the maximum qualified write rate; that remains conservative accounting, not a fully classified usage estimate. [Reasoning documentation](https://developers.openai.com/api/docs/guides/reasoning.md) confirms reasoning is included in output and must not be charged twice.

Implemented offline changes:

- The native adapter preserves terminal SDK usage, including unknown cache-write TTL fields, for buffered and streaming responses. Only response ID, model, tier, status and usage become billing evidence; prompts, output text and encrypted reasoning are excluded.
- `OpenAiSolPricing` discounts future settlements only with a complete, consistent, qualified breakdown. Missing/invalid categories retain conservative accounting; missing terminal totals retain the reservation. Exact counting, ownership checks, duplicate prevention and the $25 cap remain enforced.
- Pre-generation holds retain the existing $10/$15 worst-case rates. No cache hit is assumed before generation. Verified usage releases excess reservation afterward.
- M039 adds nullable `usage_json` and `pricing_basis` to `core.budget_reservation`. Historical rows are untouched. Completed amounts and recorded evidence cannot be overwritten by replay, including uncertain-outcome replay.
- Snapshots distinguish `UsageEstimatedUsd`, `ConservativeSettledUsd`, `OutstandingUsd`, and `UncertainRequestCount`. Legacy `SpentUsd` is the combined settled accounting amount, not an invoice.

A real-SDK fixture with 1,000 input tokens (800 reads, 100 short writes, 50 long writes, 50 ordinary) and 2 output tokens settles at $0.000700. Its pre-call 100-output-token reservation remains $0.011500. Buffered and SSE tests verify preservation through the pinned SDK.

## Historical reconciliation: explicit $1.01 operator report

The immutable pilot manifest covers 43 completed requests for project `forge`, provider `OpenAI/gpt-6.1-sol`, created 2026-11-17 03:53:48.4607921Z through 04:31:47.4604388Z and settled by 04:31:51.5466902Z. Its original total is $24.809140. Manifest SHA256: `54a97d1f234dbf2e5c7fe60acae6e7bd9e1dc68ac759afde63be848d37f4c484`. Safe local evidence is `.portHorizon/validation/budget-historical-manifest.jsonl`.

The user supplied an exact $1.01 total and authorized correcting the accounting. No additional billing credential or repeat confirmation is required. A read-only Costs API probe returned HTTP 403; this only means the value is **operator-reported**, not API-verified. [Admin documentation](https://developers.openai.com/api/docs/guides/admin-apis.md) and the [Costs reference](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/costs) describe the separate billing-read route; it is not a prerequisite for this explicit operator correction.

Implemented, awaiting qualified schema cutover and explicit operator-helper execution:

- `DurableBudgetStore.ReconcileOperatorReportedAsync` accepts the dataset, reconciliation identity, $1.01, operator, evidence hash, and exact covered request IDs/original amounts. It is not exposed as a public or model-callable mutation endpoint.
- Under the same serializable budget lock used by admission, every covered row must belong to this dataset, be completed, and match its immutable original amount. Duplicate/overlapping coverage is rejected. Identical replay is idempotent; changed evidence under the same identity is rejected.
- Append-only `budget_reconciliation` and `budget_reconciliation_request` rows record source `operator-reported`, original/reported amounts and complete manifest. The 43 original rows remain untouched.
- Effective settled accounting becomes raw settled total minus covered originals plus the reported total. For the pilot alone, this is $1.01. Future requests are outside the manifest and continue to count normally. The $25 ceiling and outstanding holds do not change.
- Snapshots show `OperatorReportedUsd` separately from tariff estimates and conservative amounts, with `ReconciledOriginalUsd` available for audit. The UI must not relabel an operator report as a provider-verified invoice.

No production reconciliation has been executed by this agent. Runtime controller/worker identities require read access to the new tables only; a separate privileged operator helper records the one-time correction after schema qualification. An older rollback reader will ignore the adjustment and conservatively return to the old authorization amount, reducing availability without increasing spend.

## M038 to M039 deployment constraint

The migration is additive and nullable; old INSERT shapes still work. This does **not** prove rollback compatibility: existing controllers expect an exact schema version and an M038 binary may refuse M039. No live migration or deployment has been performed.

Rehearse upgrade and rollback with actual images and a database copy. Establish an explicitly reviewed compatible rollback binary, or a coordinated stop/migrate/start with a tested restoration path. The earlier same-schema blue/green proof does not cover this migration. Keep spending held during cutover.

## Verification

Offline tests cover native buffered/SSE usage preservation, pricing thresholds, invalid/missing categories, reasoning/output limits, exact admission, immutable replay, additive migration, and cache display counters. Production Release build is clean; final test totals are in the implementation summary. No paid generation, billing writes, live configuration changes or host DDL occurred in this audit.
