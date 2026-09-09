<#
    Collect-D2RVmDiag.ps1

    Run this ON EACH HYPER-V HOST (CALYPSO, ADAMSMASHER, ...) in an ELEVATED PowerShell.
    It reads only; it never starts, stops, or power-cycles anything.

    Collects, per VM:
      * exactly what the stuck-VM watchdog's vm_status reads (State/Uptime/Heartbeat/CPUUsage)
      * whether Get-VM works at all for that name on this host - the silent hole where an
        unreadable VM becomes invisible to the watchdog
      * KVP OSName, which the guest only publishes once Windows is genuinely up
      * the VM's console framebuffer as a PNG, captured from the hypervisor with no help from
        the guest - this is the "stuck screen right in my face" made machine-readable
      * pixel statistics of that framebuffer, which is what I need to write a classifier that
        fires on a wedged boot logo without firing on every healthy boot

    Output: a folder of PNGs + d2r-vm-diag.json. Zip it and send it back.
#>

[CmdletBinding()]
param(
    [string[]] $VmNames = @(),
    [string]   $OutDir  = "$env:USERPROFILE\d2r-vm-diag",
    [int]      $ThumbWidth  = 0,
    [int]      $ThumbHeight = 0
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing | Out-Null
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$ns = 'root\virtualization\v2'
$report = [ordered]@{
    CollectedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    HostName       = $env:COMPUTERNAME
    HyperVModule   = $null
    Vms            = @()
}

try { $report.HyperVModule = (Get-Module -ListAvailable Hyper-V | Select-Object -First 1).Version.ToString() } catch {}

if (-not $VmNames -or $VmNames.Count -eq 0) {
    try { $VmNames = (Get-VM -ErrorAction Stop | Select-Object -ExpandProperty Name) } catch { $VmNames = @() }
}

function Get-ThumbnailPng {
    param([string] $VmName, [string] $PngPath, [int] $W, [int] $H)

    $out = [ordered]@{ Captured = $false; Width = 0; Height = 0; Error = $null; Stats = $null }

    try {
        $cs = Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem -Filter "ElementName='$($VmName.Replace("'","''"))'" -ErrorAction Stop | Select-Object -First 1
        if (-not $cs) { $out.Error = 'Msvm_ComputerSystem not found'; return $out }

        $sd = Get-CimAssociatedInstance -InputObject $cs -ResultClassName Msvm_VirtualSystemSettingData -ErrorAction Stop |
              Where-Object { $_.VirtualSystemType -eq 'Microsoft:Hyper-V:System:Realized' } | Select-Object -First 1
        if (-not $sd) { $out.Error = 'Msvm_VirtualSystemSettingData not found'; return $out }

        # Prefer the guest's actual video resolution; the boot logo screen still reports one.
        if ($W -le 0 -or $H -le 0) {
            $vh = Get-CimInstance -Namespace $ns -ClassName Msvm_VideoHead -ErrorAction SilentlyContinue |
                  Where-Object { $_.SystemName -eq $cs.Name } | Select-Object -First 1
            if ($vh -and $vh.CurrentHorizontalResolution -gt 0) {
                $W = [int]$vh.CurrentHorizontalResolution
                $H = [int]$vh.CurrentVerticalResolution
            } else { $W = 640; $H = 480 }
        }
        # Keep it modest; the API is a thumbnail service, not a screen recorder.
        if ($W -gt 1366) { $H = [int]($H * (1366 / $W)); $W = 1366 }

        $vsms = Get-CimInstance -Namespace $ns -ClassName Msvm_VirtualSystemManagementService -ErrorAction Stop
        $res  = Invoke-CimMethod -InputObject $vsms -MethodName GetVirtualSystemThumbnailImage -Arguments @{
            TargetSystem = [ciminstance]$sd
            WidthPixels  = [uint16]$W
            HeightPixels = [uint16]$H
        } -ErrorAction Stop

        if ($res.ReturnValue -ne 0) { $out.Error = "GetVirtualSystemThumbnailImage returned $($res.ReturnValue)"; return $out }
        $bytes = $res.ImageData
        if (-not $bytes -or $bytes.Length -lt ($W * $H * 2)) { $out.Error = "short image buffer: $($bytes.Length) bytes for ${W}x${H}"; return $out }

        # RGB565 -> Bitmap, copied row by row so a stride wider than W*2 cannot skew the image.
        $bmp  = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format16bppRgb565)
        $rect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
        $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format16bppRgb565)
        try {
            for ($y = 0; $y -lt $H; $y++) {
                [System.Runtime.InteropServices.Marshal]::Copy(
                    $bytes, $y * $W * 2,
                    [IntPtr]::Add($data.Scan0, $y * $data.Stride),
                    $W * 2)
            }
        } finally { $bmp.UnlockBits($data) }

        $bmp.Save($PngPath, [System.Drawing.Imaging.ImageFormat]::Png)

        # Stats over the raw RGB565 so they match what a host-side classifier would see.
        $black = 0; $logoBlue = 0; $sumR = 0.0; $sumG = 0.0; $sumB = 0.0
        $total = $W * $H
        for ($i = 0; $i -lt $total; $i++) {
            $px = [uint16]($bytes[$i * 2] -bor ($bytes[$i * 2 + 1] -shl 8))
            $r = ((($px -shr 11) -band 0x1F) * 255) / 31
            $g = ((($px -shr 5)  -band 0x3F) * 255) / 63
            $b = (( $px          -band 0x1F) * 255) / 31
            $sumR += $r; $sumG += $g; $sumB += $b
            if ($r -lt 24 -and $g -lt 24 -and $b -lt 24) { $black++ }
            # Windows boot-logo blue sits near (0,120,215)-(0,164,239): blue dominant, red low.
            if ($b -gt 120 -and $b -gt ($r + 60) -and $g -gt 60 -and $g -lt ($b + 20) -and $r -lt 90) { $logoBlue++ }
        }

        $out.Captured = $true; $out.Width = $W; $out.Height = $H
        $out.Stats = [ordered]@{
            MeanR = [math]::Round($sumR / $total, 2)
            MeanG = [math]::Round($sumG / $total, 2)
            MeanB = [math]::Round($sumB / $total, 2)
            NearBlackRatio = [math]::Round($black / $total, 4)
            LogoBlueRatio  = [math]::Round($logoBlue / $total, 4)
        }
    } catch { $out.Error = $_.Exception.Message }

    return $out
}

foreach ($name in $VmNames) {
    Write-Host "Collecting $name ..."
    $entry = [ordered]@{ Name = $name; GetVmOk = $false; GetVmError = $null }

    try {
        $vm = Get-VM -Name $name -ErrorAction Stop
        $entry.GetVmOk        = $true
        $entry.State          = [string]$vm.State
        $entry.Status         = [string]$vm.Status
        $entry.Uptime         = [string]$vm.Uptime
        $entry.UptimeSeconds  = [int]$vm.Uptime.TotalSeconds
        $entry.CPUUsage       = $vm.CPUUsage
        $entry.MemoryAssigned = $vm.MemoryAssigned
        $entry.IntegrationServicesState   = [string]$vm.IntegrationServicesState
        $entry.IntegrationServicesVersion = [string]$vm.IntegrationServicesVersion
    } catch {
        # This is the case that makes a VM invisible to the watchdog: vm_status fails, the sweep
        # calls RecordUnobserved (resetting the streak) and logs it at Debug only.
        $entry.GetVmError = $_.Exception.Message
    }

    try {
        $hb = Get-VMIntegrationService -VMName $name -Name 'Heartbeat' -ErrorAction Stop
        $entry.Heartbeat        = [string]$hb.PrimaryStatusDescription
        $entry.HeartbeatSecondary = [string]$hb.SecondaryStatusDescription
        $entry.HeartbeatEnabled = [bool]$hb.Enabled
    } catch { $entry.HeartbeatError = $_.Exception.Message }

    # KVP: the guest only publishes OSName once Windows is actually up. Absent at the boot logo.
    try {
        $cs  = Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem -Filter "ElementName='$($name.Replace("'","''"))'" -ErrorAction Stop | Select-Object -First 1
        $kvp = Get-CimAssociatedInstance -InputObject $cs -ResultClassName Msvm_KvpExchangeComponent -ErrorAction Stop | Select-Object -First 1
        $osName = $null
        foreach ($item in @($kvp.GuestIntrinsicExchangeItems)) {
            if ($item -match '<PROPERTY NAME="Name".*?<VALUE>OSName</VALUE>') {
                if ($item -match '<PROPERTY NAME="Data".*?<VALUE>(.*?)</VALUE>') { $osName = $Matches[1] }
            }
        }
        $entry.KvpOSName = $osName
        $entry.KvpItemCount = @($kvp.GuestIntrinsicExchangeItems).Count
    } catch { $entry.KvpError = $_.Exception.Message }

    $safe = ($name -replace '[^A-Za-z0-9_.-]', '_')
    $png  = Join-Path $OutDir "$($env:COMPUTERNAME)-$safe.png"
    $thumb = Get-ThumbnailPng -VmName $name -PngPath $png -W $ThumbWidth -H $ThumbHeight
    $entry.Thumbnail = $thumb
    if ($thumb.Captured) { $entry.ThumbnailFile = Split-Path $png -Leaf }

    $report.Vms += $entry
}

$jsonPath = Join-Path $OutDir 'd2r-vm-diag.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonPath -Encoding UTF8

Write-Host ""
Write-Host "Wrote $jsonPath and $((Get-ChildItem $OutDir -Filter *.png | Measure-Object).Count) PNG(s) to $OutDir"
Write-Host "Zip that folder and send it back:"
Write-Host "  Compress-Archive -Path '$OutDir\*' -DestinationPath '$OutDir.zip' -Force"
