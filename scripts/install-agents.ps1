#!/usr/bin/env pwsh
# Registers the four PortHorizon role agents in the local kilo install.
# Idempotent: re-running is safe; `kilo agent create` overwrites existing.

$ErrorActionPreference = 'Stop'

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$AgentsDir = Join-Path $RepoRoot '.kilo/agents'

$Agents = @('coredev', 'clientdev', 'qa', 'reviewer')

foreach ($name in $Agents) {
    $path = Join-Path $AgentsDir "$name.md"
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Error "Missing agent definition: $path"
        exit 1
    }
    Write-Host "Registering kilo agent '$name' from $path"
    & kilo agent create --path $path --mode subagent
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "kilo agent create exited with $LASTEXITCODE for '$name' (continuing)"
    }
}

Write-Host "Done."
