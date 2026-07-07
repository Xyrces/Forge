#!/usr/bin/env pwsh
# Stops and removes the Forge Windows Service registration. Does NOT
# delete anything under the releases root or the data root -- only the
# SCM registration goes away. Requires an elevated PowerShell session.

param(
    [string]$ServiceName = 'Forge'
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run as Administrator."
    exit 1
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service '$ServiceName' is not registered. Nothing to do."
    exit 0
}

if ($existing.Status -ne 'Stopped') {
    Write-Host "Stopping service '$ServiceName'..."
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus('Stopped', (New-TimeSpan -Seconds 30))
}

Write-Host "Removing service '$ServiceName'..."
& sc.exe delete $ServiceName | Out-Null
Write-Host "Done."
