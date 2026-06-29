# Kilo host prerequisite checklist

The orchestrator assumes a working **kilo CLI** on the host and a populated local config. Follow these steps once per machine.

## 1. Install the kilo CLI

Requires Node.js + npm (or bun). The machine already has `~/.config/kilo/package.json`, so npm is available.

```bash
npm install -g @kilocode/cli
kilo --version
```

If your CPU lacks AVX and the install crashes with a baseline-build error, install the legacy build:

```bash
npm install -g @kilocode/cli --cpu=baseline
```

## 2. Populate the local kilo config

The file lives at `~/.config/kilo/opencode.jsonc` (Windows: `%USERPROFILE%\.config\kilo\opencode.jsonc`). At minimum:

```jsonc
{
  "$schema": "https://app.kilo.ai/config.json",
  "provider": "kilocode",
  "model": "kilocode/minimax-m3",
  "kilocode": {
    "options": {
      "apiKey": "KILO_API_KEY_HERE",
      "orgId": "KILO_ORG_ID_HERE"
    }
  }
}
```

Prefer environment variables over checking secrets into the config file:

| Env var | Purpose |
|---|---|
| `KILO_API_KEY` | Gateway API key. |
| `KILO_ORG_ID` | Routes requests to a specific organization. |
| `KILO_PROVIDER` | Overrides `provider`. |
| `KILO_MODEL` | Overrides `model`. |

The orchestrator reads these and passes them to `kilo acp` on launch.

## 3. Register the role agents

From the repo root:

```bash
# bash
./scripts/install-agents.sh

# PowerShell
pwsh ./scripts/install-agents.ps1
```

This calls `kilo agent create --path .kilo/agents/<name>.md --mode subagent` for each of `coredev`, `clientdev`, `qa`, `reviewer`.

## 4. Smoke-test the ACP server

```bash
kilo acp --port 4096 --hostname 127.0.0.1
```

You should see Kilo log that it's listening on `http://127.0.0.1:4096`. `Ctrl+C` to stop. If this fails, do not proceed — the orchestrator cannot start without it.

## 5. Optional: enable OpenTelemetry export

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
```

Kilo exports OTel traces for ACP sessions when this is set. The orchestrator correlates by `session.id`.
