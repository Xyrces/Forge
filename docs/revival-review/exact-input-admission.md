# Exact input admission

> Future settlement policy is updated by [the budget accuracy audit](../workflow-audit/budget.md): verified usage categories use the qualified tariff, while legacy rows remain unchanged. Pre-call conservative admission is retained.
The qualified Forge pilot retains its $25 dataset ceiling, existing ledger rows,
full 1,050,000-token context limit, and conservative $10/M input and $15/M output
rates. New requests reserve against a free server input count plus the capped
output allowance. Settlement still uses conservative reported-token costs; it is
not an invoice estimate. Unknown usage retains the reservation.

The Responses transport freezes the SDK's final serialized generation body.
It projects only the documented token-counter fields, preserving their JSON
values verbatim, including native encrypted reasoning, instructions, tools,
response schema, reasoning settings and tool selection. Mutable server-side
conversation references and unknown top-level fields are refused. The installed
SDK's `GetInputTokenCountAsync` calls `/v1/responses/input_tokens` with the same
provider credentials and organization/project settings. Both SDK paths disable
hidden retries.

After a valid count, the policy checks ownership, reserves durable liability,
checks ownership again and sends the frozen original generation bytes once.
Count errors, malformed results and context/output overflow stop generation.
Buffered responses and SSE share this transport gate. A second generation in
one admission scope is refused. Existing non-budgeted clients retain their
behavior; distributed composition applies the budget policy.

For example, 60,000 counted input tokens and an 8,192-token output allowance
reserve $0.72288 at the unchanged qualified rates. This does not reprice or
refund earlier conservative settlements or uncertain reservations.

Qualification sources checked 2026-11-17:

- [OpenAI input counting guide](https://developers.openai.com/api/docs/guides/token-counting): exact model input, including request structure.
- [Input-count endpoint reference](https://developers.openai.com/api/reference/resources/responses/subresources/input_tokens/methods/count): supported parameters, including encrypted reasoning input items.
- [OpenAI billing help](https://help.openai.com/en/articles/4936856-understanding-and-counting-tokens): input counting is free and does not consume normal inference quota.
- [Prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching) and [Sol model](https://developers.openai.com/api/docs/models/gpt-6.1-sol): existing worst-case cache/long-context rate qualification remains unchanged.

Offline tests cover native SDK serialization and replay, exact projected values,
unchanged generation input despite caller mutation after counting, headers,
buffered/SSE settlement, counter failures/cancellation, ownership changes and
single-send behavior and interleaved buffered/SSE scope isolation. These tests
use offline fakes; separately performed host checks belong in
[live qualification](live-qualification.md).
