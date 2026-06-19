param(
    [string]$InstallDir = "C:\D2ROps",
    [string]$ConfigPath = "C:\D2ROps\vm-agent.config.json",
    [string]$ExePath = ".\D2RAgent.exe",
    [string]$TaskName = "D2R VM Agent"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $ExePath)) {
    throw "D2RAgent.exe was not found at $ExePath"
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Force $ExePath (Join-Path $InstallDir "D2RAgent.exe")

if (!(Test-Path $ConfigPath)) {
    Write-Warning "Config file does not exist yet: $ConfigPath"
    Write-Warning "Copy vm-agent.config.example.json there and edit it before starting the task."
}

$action = New-ScheduledTaskAction `
    -Execute (Join-Path $InstallDir "D2RAgent.exe") `
    -Argument "`"$ConfigPath`""
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal `
    -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive `
    -RunLevel Highest

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Description "D2R ops VM agent. Runs in the logged-in desktop session." `
    -Force | Out-Null

Write-Host "Installed $TaskName. Start it with: Start-ScheduledTask -TaskName '$TaskName'"
