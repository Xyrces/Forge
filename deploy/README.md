# P4 Stage B — Durable Task Scheduler sidecar

This directory contains the dev-only Durable Task Scheduler
(DTS) emulator sidecar. The orchestrator talks to it over gRPC
(localhost:8080) when `Orchestrator:Execution=Durable` is set
in `appsettings.json`.

## Why?

Without DTS, in-flight orchestrations lose state on a crash
even though our in-process recovery service (P4 Stage A) can
replay the cheap side-effects. With DTS:

- The orchestrator can crash at any point; DTS replays the
  workflow from its last durable checkpoint.
- Agent conversation history (`AIAgent.SerializeSessionAsync`)
  is persisted across restarts.
- The PR merge signal becomes a real webhook instead of a 30s
  poll.

## Runtimes

The `docker-compose.yml` in this directory is plain Docker
Compose v3 syntax. Both Docker Compose and podman-compose
read this file with no changes:

```bash
# Docker
docker compose -f deploy/docker-compose.yml up -d

# Podman (rootless)
podman-compose -f deploy/docker-compose.yml up -d

# Verify
curl -sf http://localhost:8082/  # dashboard JSON
```

## Image

`mcr.microsoft.com/dts/dts-emulator:latest`. Pull from
Microsoft's public MCR (mirror of GCR) — no auth needed. The
emulator is documented as in-memory only: restart wipes state.
For production, swap to the hosted Azure Durable Task Scheduler
behind the same gRPC contract.

## Configuration

Add to `appsettings.json` (gitignored — commit the example
below to `appsettings.example.json` instead):

```json
{
  "orchestrator": {
    "execution": "Durable",
    "dtsConnectionString": "Endpoint=http://localhost:8080;TaskHub=default;Authentication=None"
  }
}
```

Default connection string matches what the emulator expects.
Override `TaskHub` to `default2` etc. if you run multiple
orchestrators against the same DTS instance and want state
isolation.

## Fallback

If `Orchestrator:Execution=InProcess` (default), the
orchestrator uses P4 Stage A's `StartupRecovery` for restart
safety. The DTS sidecar is opt-in and the orchestrator runs
fine without it.

## Files

- `docker-compose.yml` — the DTS emulator sidecar definition.
- `README.md` — this file.