param(
    [string]$InstallDir = "C:\D2ROps",
    [string]$ConfigPath = "C:\D2ROps\d2r-host.config.json",
    [string]$ExePath = ".\OpsHost.exe",
    [string]$TaskName = "D2R Host Controller"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "$ExePath was not found"
}

$ResolvedExePath = (Resolve-Path -LiteralPath $ExePath).Path
$SourceDir = Split-Path -Parent $ResolvedExePath
$ExeName = Split-Path -Leaf $ResolvedExePath

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$ResolvedInstallDir = (Resolve-Path -LiteralPath $InstallDir).Path

function Normalize-Path([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
}

$SkippedNames = @(
    (Split-Path -Leaf $ConfigPath),
    "d2r-host.sqlite",
    "d2r-host.sqlite-shm",
    "d2r-host.sqlite-wal"
) | Where-Object { ![string]::IsNullOrWhiteSpace($_) }

if ((Normalize-Path $SourceDir) -ieq (Normalize-Path $ResolvedInstallDir)) {
    Write-Host "Install source and destination are both $ResolvedInstallDir. App file copy skipped."
} else {
    foreach ($Item in Get-ChildItem -LiteralPath $SourceDir -Force) {
        if ($SkippedNames -contains $Item.Name) {
            continue
        }

        Copy-Item `
            -LiteralPath $Item.FullName `
            -Destination (Join-Path $ResolvedInstallDir $Item.Name) `
            -Recurse `
            -Force
    }

    Write-Host "Copied app files from $SourceDir to $ResolvedInstallDir."
}

if (!(Test-Path -LiteralPath $ConfigPath)) {
    Write-Warning "Config file does not exist yet: $ConfigPath"
    Write-Warning "Copy d2r-host.config.example.json there and edit it before starting the task."
}

$action = New-ScheduledTaskAction `
    -Execute (Join-Path $InstallDir $ExeName) `
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
