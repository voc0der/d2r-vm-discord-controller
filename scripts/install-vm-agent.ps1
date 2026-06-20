param(
    [string]$InstallDir = "C:\D2ROps",
    [string]$ConfigPath = "C:\D2ROps\vm-agent.config.json",
    [string]$ExePath = ".\D2RAgent.exe",
    [string]$TaskName = "D2R VM Agent"
)

$ErrorActionPreference = "Stop"

if (!(Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "D2RAgent.exe was not found at $ExePath"
}

$ResolvedExePath = (Resolve-Path -LiteralPath $ExePath).Path
$SourceDir = Split-Path -Parent $ResolvedExePath

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$ResolvedInstallDir = (Resolve-Path -LiteralPath $InstallDir).Path

function Normalize-Path([string]$Path) {
    return [System.IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
}

$SkippedNames = @(
    (Split-Path -Leaf $ConfigPath)
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

    Write-Host "Copied D2RAgent app files from $SourceDir to $ResolvedInstallDir."
}

if (!(Test-Path -LiteralPath $ConfigPath)) {
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
