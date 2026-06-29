#!/usr/bin/env bash
# Registers the four PortHorizon role agents in the local kilo install.
# Idempotent: re-running is safe; `kilo agent create` overwrites existing.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
AGENTS_DIR="$REPO_ROOT/.kilo/agents"

for name in coredev clientdev qa reviewer; do
    path="$AGENTS_DIR/$name.md"
    if [[ ! -f "$path" ]]; then
        echo "Missing agent definition: $path" >&2
        exit 1
    fi
    echo "Registering kilo agent '$name' from $path"
    kilo agent create --path "$path" --mode subagent
done

echo "Done."
