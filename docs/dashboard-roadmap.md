# Dashboard & Orchestration Roadmap (P1 design draft)

## Goals

1. Interactive orchestration control (pause/resume/retry tasks, change priority).
2. Agent CRUD with role-prompt mirror (agents/*.md).
3. Skill CRUD (per-agent and global).
4. Backlog + Sprint views (flat table vs Kanban) over the same issue store.
5. Sprint orchestration: one active sprint at a time; orchestrator picks only sprint issues.

## Data model (SQLite v2)

Adds four tables to issues.db: `agent`, `skill`, `sprint`, `sprint_issue`.

- `agent` mirrors agents/*.md; one row per role agent. Fields: agent_name, display_name, scope, description, enabled, config_json.
- `skill` has agent_id (nullable; NULL = global). Unique on (name, agent_id).
- `sprint` has goal, start/end dates, status (active|completed|archived). Single-active invariant enforced transactionally.
- `sprint_issue` is the many-to-many linking sprint to issue.

## Endpoints (new)

- /api/agents CRUD
- /api/skills CRUD (filters: agent=, global=)
- /api/sprints CRUD + /api/sprints/{id}/issues add/remove + set-active
- /api/agents/{id}/messages (post a message to that agent's inbox)
- /api/state extended with agents/skills/sprints/agentMessages
- POST/PATCH on /api/state/issues

## UI

Five tabs: Tasks (current), Backlog, Sprints, Agents, Skills. Vanilla JS, dark theme.

## Orchestrator changes

- Startup: load agents/*.md into agent table.
- DispatchCycleAsync: ReadyAsync filters by active sprint when one exists.
- Before each prompt: drain AgentMessageBus and prepend queued messages.

See /docs/dashboard-roadmap.md for the full schema, invariants, and out-of-scope list.
