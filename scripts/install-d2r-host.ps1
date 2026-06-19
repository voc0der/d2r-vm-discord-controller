param(
    [string]$InstallDir = "C:\D2ROps",
    [string]$ConfigPath = "C:\D2ROps\d2r-host.config.json",
    [string]$ExePath = ".\D2RHost.exe",
    [string]$TaskName = "D2R Host Controller"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $ExePath)) {
    throw "D2RHost.exe was not found at $ExePath"
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Force $ExePath (Join-Path $InstallDir "D2RHost.exe")

if (!(Test-Path $ConfigPath)) {
    Write-Warning "Config file does not exist yet: $ConfigPath"
    Write-Warning "Copy d2r-host.config.example.json there and edit it before starting the task."
}

$action = New-ScheduledTaskAction `
    -Execute (Join-Path $InstallDir "D2RHost.exe") `
    -Argument "`"$ConfigPath`""
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -RunLevel Highest

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Description "D2R Discord controller, WebSocket listener, and Hyper-V host control." `
    -Force | Out-Null

Write-Host "Installed $TaskName. Start it with: Start-ScheduledTask -TaskName '$TaskName'"
