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

function Resolve-GamePath([string]$ConfiguredPath, [string]$ProcessName, [string]$DefaultPath) {
    foreach ($Candidate in @($ConfiguredPath, $DefaultPath)) {
        if (![string]::IsNullOrWhiteSpace($Candidate) -and (Test-Path -LiteralPath $Candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $Candidate).Path
        }
    }

    $RunningProcess = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($RunningProcess -and $RunningProcess.Path) {
        return $RunningProcess.Path
    }

    return $null
}

function Set-InboundAnyProfileRule([string]$DisplayName, [string]$ProgramPath) {
    if ([string]::IsNullOrWhiteSpace($ProgramPath)) {
        Write-Warning "Skipping firewall rule '$DisplayName': could not resolve an executable path. Set the path in $ConfigPath and re-run this script."
        return
    }

    $Existing = Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction SilentlyContinue
    if ($Existing) {
        $Existing | Set-NetFirewallRule -Program $ProgramPath -Direction Inbound -Action Allow -Profile Any -Enabled True
    } else {
        New-NetFirewallRule -DisplayName $DisplayName -Program $ProgramPath -Direction Inbound -Action Allow -Profile Any -Enabled True | Out-Null
    }

    Write-Host "Firewall rule '$DisplayName' allows inbound traffic on all network profiles for $ProgramPath."
}

$ConfiguredBattleNetPath = $null
$ConfiguredD2RPath = $null
if (Test-Path -LiteralPath $ConfigPath) {
    try {
        $ConfigJson = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
        $ConfiguredBattleNetPath = $ConfigJson.battleNetPath
        $ConfiguredD2RPath = $ConfigJson.d2rPath
    } catch {
        Write-Warning "Could not parse $ConfigPath as JSON; using default game paths for firewall rules."
    }
}

$BattleNetPath = Resolve-GamePath $ConfiguredBattleNetPath "Battle.net" "C:\Program Files (x86)\Battle.net\Battle.net.exe"
$D2RPath = Resolve-GamePath $ConfiguredD2RPath "D2R" "C:\Program Files (x86)\Diablo II Resurrected\D2R.exe"

Set-InboundAnyProfileRule "D2R Ops - Battle.net (all profiles)" $BattleNetPath
Set-InboundAnyProfileRule "D2R Ops - D2R (all profiles)" $D2RPath

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
