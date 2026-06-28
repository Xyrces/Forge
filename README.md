# PortHorizon.Agents

Semantic Kernel-based agent orchestration for PortHorizon game development.

## Architecture

```
OrchestratorAgent (Planning, task decomposition, PR gating)
    ├── CoreDevAgent (ECS, Systems, Pathfinding, Atmospherics)
    ├── ClientDevAgent (Godot View, SyncBridge, UI)
    ├── QAAgent (Tests, MCP playtest harness)
    └── ReviewerAgent (PR review, code quality gates)
```

## Key Features

- **Out-of-process agents**: Each agent runs as separate `dotnet run` process
- **Max 2 concurrent agents**: Prevents runaway spawning
- **State persistence**: Task queue persisted to `.portHorizon/state/`
- **GitHub PR integration**: Branch creation, CI checks, merge gates
- **Budget enforcement**: Heartbeat every 60 seconds

## Projects

| Project | Purpose |
|---------|---------|
| `PortHorizon.Agents.Core` | Interfaces, base classes, state management |
| `OrchestratorAgent` | Central planning and task dispatch |
| `DevAgents` | CoreDev, ClientDev, QA agents |
| `ReviewerAgent` | PR review and approval |

## Usage

```bash
# Build
dotnet build PortHorizon.Agents.sln

# Run orchestrator
dotnet run --project PortHorizon.Agents.Orchestrator

# Run agent directly
dotnet run -- --task=<id> --branch=<name>
```

## Configuration

Set workspace root in `Program.cs` to point to PortHorizon repo:
```csharp
var workspaceRoot = @"C:\Users\jtn50\repos\gamedev\PortHorizon";
```

## State Files

State is persisted to `.portHorizon/state/`:
- `orchestrator-state.json` - Task queue and progress
- `heartbeat-<agentId>.json` - Agent heartbeat data