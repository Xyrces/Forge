# Intake approval UI qualification

On November 17, 2026, the actual dashboard was exercised through the browser
at an isolated local preview. It used two disposable SQLite project stores and
a typed scripted chat client, with no provider credentials, engineering dispatch
or refinement scheduler. SQLite here is a test fixture; production remains
Azure SQL. The final UI/backend was the same implementation integrated at
`e1b1045715098919df31827e375e3476ead22b10` (preview branch `3cfe35d`).

Observed through UI interactions:

- A 24-proposal project loaded 20 summaries, then four; switching projects
  replaced the list and conversation context.
- Expanding a draft opened its Intake conversation. Repeating the action
  reused the conversation. The Specs page remained empty before approval.
- A new conversation and message submitted through the UI created a draft
  proposal. Enter submission worked after fixing the input binding.
- Explicit approval of the displayed revision created the epic and Draft spec,
  changed the list's status to approved, and populated the conversation's spec
  panel. Proposal and spec links retained the owning project.
- The linked Draft spec displayed approval already recorded and refinement
  Pending; premature Approve/Send to Designer actions were absent after the
  guard fix. The API guard also has targeted rejection tests.
- Conversation and proposal review are separate views. At a 1280px browser
  width, the conversation is readable and the composer stays visible while
  messages scroll. The spec version renders as `v1`.

The browser checks found and drove fixes to stale sidebar instructions,
title-based acceptance display, input synchronization, approval status refresh,
session-spec lookup, cross-links, premature spec actions, and cramped layout.

Private screenshots are retained in the integration checkout under
`.portHorizon/validation/intake-conversation.jpg` and `intake-approval.jpg`.
The scripted client creates predetermined draft responses; this does not prove
live model reasoning or revision behavior. Refinement remained Pending because
the fixture deliberately has no scheduler. This is UI-through-approval and
handoff verification, **not** the complete live Intake-to-deployment journey.

The earlier schema39 compatibility release is independently live:
`deploy-e2d4475b03134f37973ebc4e31aec01a`, source
`a7c25bcd57078aedba44b46d37a94c56feed0efa`, image digest
`06deeee51f2da257fc66fdb10f2dde18e169b3b4bbd4cf116a9960ea3e364ec7`.
Its release journal committed, blue owns epoch20, readiness is healthy, and
all four planning/merge gates remain held. The accounting readback remains
$1.01 operator-reported usage and $23.99 available, with no outstanding or
uncertain requests. No paid model calls were needed for this qualification.

Linux CI run `44110803592` passed all 1,876 .NET tests, 42 deployment protocol
tests, and the fake closed-loop engineering harness at the exact integration
revision above. Image run `44110862238` independently passed its build/tests
and published immutable digest
`9bd125844f0b43f164fce71f30bee403b36c495c71503d687a4f88b469569b04`.
