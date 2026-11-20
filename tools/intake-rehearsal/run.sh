#!/usr/bin/env bash
set -euo pipefail
# Linux only; DOTNET and NODE_PATH may select already-installed tools.
root=$(cd "$(dirname "$0")/../.." && pwd)
evidence=${1:?Pass a new evidence directory}
mkdir -p "$evidence"
evidence=$(realpath "$evidence")
if [[ -e "$evidence/state" ]]; then echo 'Use a fresh evidence directory' >&2; exit 2; fi
dotnet=${DOTNET:-dotnet}
port=${INTAKE_PORT:-5127}
cd "$root"
"$dotnet" build tools/intake-rehearsal/IntakeRehearsal.csproj -c Release --nologo > "$evidence/build.log" 2>&1
ASPNETCORE_ENVIRONMENT=Development "$dotnet" tools/intake-rehearsal/bin/Release/net10.0/IntakeRehearsal.dll --state "$evidence/state" --port "$port" > "$evidence/host.log" 2>&1 &
host=$!
trap 'kill "$host" 2>/dev/null || true; wait "$host" 2>/dev/null || true' EXIT
for _ in $(seq 1 60); do
  if curl -fsS "http://127.0.0.1:$port/__qualification/seed" > /dev/null 2>&1; then break; fi
  kill -0 "$host"
  sleep 1
done
INTAKE_URL="http://127.0.0.1:$port" node tools/intake-rehearsal/verify.cjs "$evidence/browser"
