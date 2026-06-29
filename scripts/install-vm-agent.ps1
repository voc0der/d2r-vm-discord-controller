param(
    [string]$InstallDir = "C:\D2ROps",
    [string]$ConfigPath = "C:\D2ROps\vm-agent.config.json",
    [string]$ExePath = "",
    [string]$TaskName = "D2R VM Agent"
)

$ErrorActionPreference = "Stop"

function Resolve-AppExePath([string]$CandidatePath) {
    if (![string]::IsNullOrWhiteSpace($CandidatePath)) {
        if (!(Test-Path -LiteralPath $CandidatePath -PathType Leaf)) {
            throw "$CandidatePath was not found"
        }

        return (Resolve-Path -LiteralPath $CandidatePath).Path
    }

    $SearchDir = if (![string]::IsNullOrWhiteSpace($PSScriptRoot)) { $PSScriptRoot } else { (Get-Location).Path }
    $ExeCandidates = @(Get-ChildItem -LiteralPath $SearchDir -Filter "*.exe" -File | Sort-Object Name)
    if ($ExeCandidates.Count -eq 1) {
        return $ExeCandidates[0].FullName
    }

    if ($ExeCandidates.Count -eq 0) {
        throw "No VM agent exe was found in $SearchDir. Pass -ExePath .\YourAgent.exe."
    }

    $Names = ($ExeCandidates | ForEach-Object { $_.Name }) -join ", "
    throw "Multiple exe files were found in $SearchDir ($Names). Pass -ExePath .\YourAgent.exe."
}

$ResolvedExePath = Resolve-AppExePath $ExePath
$SourceDir = Split-Path -Parent $ResolvedExePath
$ExeName = Split-Path -Leaf $ResolvedExePath

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

    Write-Host "Copied app files from $SourceDir to $ResolvedInstallDir."
}

if (!(Test-Path -LiteralPath $ConfigPath)) {
    Write-Warning "Config file does not exist yet: $ConfigPath"
    Write-Warning "Copy vm-agent.config.example.json there and edit it before starting the task."
}

$action = New-ScheduledTaskAction `
    -Execute (Join-Path $InstallDir $ExeName) `
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
