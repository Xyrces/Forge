#!/usr/bin/env pwsh
# Installs Forge as a Windows Service, pointed at a published release
# directory. Run this ONCE per machine to register the service; every
# subsequent deployment repoints $CurrentLink at a new release and
# restarts the service -- it does not need to be re-run.
#
# Usage:
#   scripts/install-service.ps1 -CurrentLink C:\ProgramData\Forge\current `
#                                -AppSettings C:\ProgramData\Forge\appsettings.json
#
# Requires an elevated (Administrator) PowerShell session -- New-Service
# and Start-Service both need service-control privileges.

param(
    [string]$ServiceName = 'Forge',
    [Parameter(Mandatory = $true)][string]$CurrentLink,
    [string]$AppSettings = (Join-Path (Split-Path $CurrentLink -Parent) 'appsettings.json'),
    [string]$DisplayName = 'Forge Orchestrator'
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run as Administrator (New-Service requires it)."
    exit 1
}

$exePath = Join-Path $CurrentLink 'Forge.Core.exe'
if (-not (Test-Path -LiteralPath $exePath)) {
    Write-Error "No published build found at $exePath. Publish a release and point -CurrentLink at it (or run scripts/deploy-forge.ps1) before installing the service."
    exit 1
}

if (-not (Test-Path -LiteralPath $AppSettings)) {
    Write-Warning "No appsettings.json found at $AppSettings. Forge will fail OptionsLoader validation at service start until one exists there."
}

$binPath = "`"$exePath`" --config `"$AppSettings`""

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' already exists (status: $($existing.Status)). Updating binPath via sc.exe..."
    & sc.exe config $ServiceName binPath= $binPath | Out-Null
} else {
    Write-Host "Creating service '$ServiceName' -> $exePath"
    New-Service -Name $ServiceName `
        -BinaryPathName $binPath `
        -DisplayName $DisplayName `
        -Description 'Forge multi-project AI coding agent orchestrator (self-hosted).' `
        -StartupType Automatic
}

Write-Host "Starting service '$ServiceName'..."
Start-Service -Name $ServiceName
Start-Sleep -Seconds 2
Get-Service -Name $ServiceName | Format-Table -AutoSize

Write-Host "Done. Logs go to the Windows Event Log (Application) under source 'Forge' by default;"
Write-Host "for a live tail during development, run Forge.Core.exe directly from a console instead of as a service."
