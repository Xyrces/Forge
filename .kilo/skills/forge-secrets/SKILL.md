---
name: forge-secrets
description: The Forge per-project secrets system — encrypted storage, the dashboard Secrets page, known vs custom kinds, and the by-reference consumption model (env vars in the agent bash tool; values never enter LLM context). Use when reasoning about credentials, 401s, secret rotation, or adding a new secret kind.
---

# forge-secrets

Per-project credential storage and consumption. The design goal: **agents and runtime code can USE a secret without the value ever entering the LLM's context window** (prompt, tool-call JSON, or logs).

## Storage

- Table: `secret` in each project's issues SQLite file (schema v18). `(project_id, kind)` unique.
- Encryption: `IDataProtector`, purpose string `forge.secret.v1` (`Core/SecretStore.cs`).
- Keyring: `~/.aspnet/DataProtection-Keys/`. **Rotating the keyring invalidates every stored secret** — `GetPlaintextAsync` returns null on decrypt failure and consumers fall back to global config.
- HTTP rule: plaintext is NEVER returned over the API. The list endpoint returns `(kind, set, createdAt, updatedAt)` only.

## Known vs custom kinds

| Kind | Consumer |
|---|---|
| `github_token` | `ProjectDispatchBundleFactory` — per-project GitHub PAT for `GitWorktreeService` (push) and `GitHubService` (PRs). Overrides global `GITHUB_TOKEN` env / `github.token` config. Also injected into the bash env as `GITHUB_TOKEN`. |
| `kilo_gateway_api_key` | Chat-client auth for the kilo gateway. Resolved at startup by `Program.cs::ResolveKiloGatewayKeyAsync` (first stored key across registered projects wins) and substituted into the `LlmConfig` kilo-gateway provider entry — the appsettings `apiKey` is only the `KILO_GATEWAY_API_KEY` placeholder. Free-tier models tolerate the placeholder; paid models (e.g. `minimax/minimax-m3`) 401 with `PAID_MODEL_AUTH_REQUIRED` without the real key. **Rotation takes effect on service restart.** |
| `meshy_api_key` | `MeshyClient` for the Designer/Artist 3D pipeline. |
| custom (any `[a-z0-9][a-z0-9_-]{0,63}`) | Injected into the agent bash environment only. |

`Core/SecretStore.cs::SecretKinds` holds the known-kind constants. `POST /api/projects/{id}/secrets` validates kind shape; the UI's upper panel renders the three known kinds (always, even when unset), the lower panel renders stored customs + an add form.

## By-reference consumption (the important part)

When `MafAgentRunner` builds the `bash` tool for a run whose context carries `projectId` (the dispatch workflow sets it), `ResolveSecretEnvAsync` decrypts the project's secrets and passes them to `BashTool(envVars:)`, which sets them on the spawned process:

- every kind → `FORGE_SECRET_<KIND>` (kind uppercased, `-` → `_`). Example: kind `npm_token` → `$FORGE_SECRET_NPM_TOKEN`.
- `github_token` additionally → `GITHUB_TOKEN` (conventional name CLIs already read).

The model sees only the variable NAMES (in the role prompt contract + its own commands). The values live in the process environment. Role prompts (`agents/*.md`) carry the contract:

1. Reference `$VAR` — never inline a literal credential into a command, file, commit, or PR body.
2. Never print secrets (`echo $VAR`, `env`, `printenv` are forbidden). Existence check pattern: `[ -n "$VAR" ] && echo present`.
3. On 401/auth failure, report the secret may be missing/wrong — never work around by embedding credentials.

## UI + API surface

- Page: `/projects/{id}/secrets` (upper panel = known kinds, lower = customs). Reachable from the project sub-nav chips and the OPS nav (current project's page).
- `GET /api/projects/{id}/secrets/` — metadata for known kinds (always) + stored customs.
- `POST /api/projects/{id}/secrets` — upsert `{kind, value}` (value ≤ 8KB).
- `DELETE /api/projects/{id}/secrets/{kind}` — removes the row; consumers fall back to global config.

## Failure modes to recognize

- **Decrypt failure after keyring rotation** → `GetPlaintextAsync` returns null → bundle factory falls back to the global GitHub PAT; bash env silently lacks the var. Symptom: push fails with auth error on a project that "has a token set". Fix: re-set the secret via the UI.
- **DI break on the Secrets/Board/Intake/Vision pages** → these inject a plain `HttpClient`; it's the named `"ForgeApi"` client registered in `UIExtensions.AddForgeUI`. If it's removed, those pages render their empty state with no error.
- **Custom kind not visible in list** → the list endpoint only shows kinds actually stored in DB (plus the three known). POST it first.

## Files

- `Core/SecretStore.cs` — store, `SecretKinds`, `ISecretStore`.
- `Dashboard/SecretsEndpoints.cs` — the three endpoints + kind validation.
- `Forge.UI/Components/Pages/Secrets.razor` — the two-panel page.
- `Agents/MafAgentRunner.cs::ResolveSecretEnvAsync` — env construction.
- `AgentTools/BashTool.cs` — `envVars` injection into `ProcessStartInfo.Environment`.
- `Orchestrator/ProjectDispatchBundle.cs::ResolveGitHubToken` — per-project PAT override.
