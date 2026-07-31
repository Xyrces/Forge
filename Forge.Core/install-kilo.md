# Kilo gateway prerequisite checklist

The orchestrator uses the [kilo gateway](https://kilo.ai/docs/gateway) — an OpenAI-compatible HTTP endpoint. It does **not** require the legacy `kilo serve` / `kilo acp` CLI, agent registration, or per-task worktree cwd gymnastics. The Microsoft Agent Framework (`Microsoft.Agents.AI` 1.12.0) handles the agent loop in-process.

## 1. Get a kilo gateway API key

Sign in at <https://kilo.ai>, create a project API key. The JWT is your `KILO_GATEWAY_API_KEY`.

Smoke-test it with `curl` (replace `<KEY>` and `<MODEL>` — see the `models` list at <https://api.kilo.ai/api/gateway/models>):

```bash
curl.exe -sS -X POST "https://api.kilo.ai/api/gateway/v1/chat/completions" \
  -H "Authorization: Bearer <KEY>" \
  -H "Content-Type: application/json" \
  -d '{"model":"minimax/minimax-m3","messages":[{"role":"user","content":"ping"}],"max_tokens":8}'
```

You should get HTTP 200 with a `Pong!`-style reply.

## 2. Get a GitHub token

The orchestrator opens and merges PRs on your behalf. Create a classic PAT (Personal Access Token) with `repo` scope at <https://github.com/settings/tokens>. Or use `gh auth token` if you have `gh` CLI installed and authenticated.

## 3. Configure the orchestrator

Copy `appsettings.example.json` to `appsettings.json` and fill in the two secrets:

| Key | Replaces with |
|---|---|
| `llm.providers[0].apiKey` | your kilo gateway JWT |
| `github.token` | your GitHub PAT |

`appsettings.json` is in `.gitignore` — secrets never enter git history.

Environment variables override any field (use `__` for nested keys):

| Var | Maps to |
|---|---|
| `llm__providers__0__apiKey` or `KILO_GATEWAY_API_KEY` | `llm.providers[0].apiKey` |
| `llm__providers__0__defaultModel` or `KILO_MODEL` | default model id |
| `github__token` or `GITHUB_TOKEN` | `github.token` |
| `Workspace__Root` | `workspace.root` (path to the git repo the orchestrator will work on) |

## 4. (Optional) Custom role agents

By default, every role (CoreDev, ClientDev, QA, Reviewer, Intake) uses the same model. The system prompt + tools differ per role.

To customize the role instruction template per role, drop a Markdown file at `<workspace>/.kilo/agents/<role>.md` (e.g. `.kilo/agents/coredev.md`). The orchestrator loads the `description:` field from the YAML frontmatter as the MAF system instructions.

If the file is missing, the orchestrator logs a warning and uses a generic fallback:
> "You are the coredev agent."

## 5. Smoke-test the orchestrator

```bash
# Long-running orchestrator + dashboard
dotnet run --project Forge

# In a second terminal, enqueue a test task
curl.exe -X POST http://127.0.0.1:4097/api/state/issues \
  -H "Content-Type: application/json" \
  -d '{"type":"task","title":"Smoke test: list the top-level files","priority":2}'

# Open the dashboard
start http://127.0.0.1:4097
```

If the task is claimed and dispatched (the dashboard's "agent session started" event fires), you're set up.

## What this orchestrator no longer needs

The previous generation of the orchestrator (pre-MAF) used a separate `kilo serve` subprocess and an Agent Client Protocol HTTP client. **None of that is required any more.** You can ignore the legacy install steps in `install-kilo.md` history; they documented a now-deleted architecture. If you find old docs referring to `kilo acp`, `kilo serve`, `AcpClient`, or per-session worktree cd, those are stale.
