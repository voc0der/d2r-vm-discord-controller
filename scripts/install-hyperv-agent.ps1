param(
    [string]$InstallDir = "C:\D2ROps",
    [string]$ConfigPath = "C:\D2ROps\hyperv-agent.config.json",
    [string]$ExePath = ".\HyperVAgent.exe",
    [string]$TaskName = "D2R Hyper-V Agent"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $ExePath)) {
    throw "HyperVAgent.exe was not found at $ExePath"
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Force $ExePath (Join-Path $InstallDir "HyperVAgent.exe")

if (!(Test-Path $ConfigPath)) {
    Write-Warning "Config file does not exist yet: $ConfigPath"
    Write-Warning "Copy hyperv-agent.config.example.json there and edit it before starting the task."
}

$action = New-ScheduledTaskAction `
    -Execute (Join-Path $InstallDir "HyperVAgent.exe") `
    -Argument "`"$ConfigPath`""
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -RunLevel Highest

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Description "D2R ops Hyper-V host control agent." `
    -Force | Out-Null

Write-Host "Installed $TaskName. Start it with: Start-ScheduledTask -TaskName '$TaskName'"
