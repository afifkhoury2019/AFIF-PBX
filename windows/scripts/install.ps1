<#
  AFIF-PBX setup (runs elevated from the installer).
  - makes sure WSL 2 is installed and current
  - turns on mirrored networking so phones on the LAN reach the PBX at this PC's IP
  - installs usbipd-win (USB passthrough for the Huawei E303)
  - imports the AFIF-PBX system (Debian 12 + Asterisk + FreePBX engine + chan_dongle)
  - opens the SIP / RTP / web ports and registers the launcher to start at logon
#>
param(
  [Parameter(Mandatory)] [string] $AppDir,
  [switch] $Fresh
)
$ErrorActionPreference = 'Stop'
$Distro   = 'AFIF-PBX'
$DataDir  = Join-Path $env:ProgramData 'AFIF-PBX'
$DistroDir = Join-Path $DataDir 'system'
$Rootfs   = Join-Path $AppDir 'system\afif-pbx-rootfs.tar.gz'
$UsbipdMsi = Get-ChildItem (Join-Path $AppDir 'deps') -Filter 'usbipd-win*.msi' -ErrorAction SilentlyContinue | Select-Object -First 1
$RebootFlag = Join-Path $DataDir 'reboot-required'
$WslVmCreator = '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}'   # Hyper-V firewall id of WSL

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
Start-Transcript -Path (Join-Path $DataDir 'install.log') -Append | Out-Null
Remove-Item $RebootFlag -ErrorAction SilentlyContinue
$env:WSL_UTF8 = '1'

function Step($t) { Write-Host "`n=== $t ===" }

# ---------- Windows version ----------
$build = [int](Get-CimInstance Win32_OperatingSystem).BuildNumber
if ($build -lt 22000) { throw "AFIF-PBX needs Windows 11 (build 22000 or newer). This PC is build $build." }

# ---------- WSL ----------
Step 'Checking WSL'
$wsl = Join-Path $env:SystemRoot 'System32\wsl.exe'
& $wsl --status *> $null
if ($LASTEXITCODE -ne 0) {
  Step 'Installing WSL (a restart will be needed)'
  & $wsl --install --no-distribution
  # Finish the rest automatically after the restart
  $cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -AppDir `"$AppDir`""
  Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce' -Name 'AFIF-PBX-Setup' -Value $cmd
  New-Item -ItemType File -Force -Path $RebootFlag | Out-Null
  Stop-Transcript | Out-Null
  exit 0
}
Step 'Updating WSL'
& $wsl --update 2>&1 | Write-Host
& $wsl --set-default-version 2 2>&1 | Out-Null

# ---------- .wslconfig: mirrored networking ----------
Step 'Configuring WSL networking (mirrored)'
$cfg = Join-Path $env:USERPROFILE '.wslconfig'
$lines = @()
if (Test-Path $cfg) { $lines = Get-Content $cfg }
$out = New-Object System.Collections.Generic.List[string]
$inWsl2 = $false; $haveSection = $false; $setNet = $false
foreach ($l in $lines) {
  if ($l -match '^\s*\[(.+)\]\s*$') {
    if ($inWsl2 -and -not $setNet) { $out.Add('networkingMode=mirrored'); $setNet = $true }
    $inWsl2 = ($Matches[1].Trim().ToLower() -eq 'wsl2'); if ($inWsl2) { $haveSection = $true }
    $out.Add($l); continue
  }
  if ($inWsl2 -and $l -match '^\s*networkingMode\s*=') { $out.Add('networkingMode=mirrored'); $setNet = $true; continue }
  $out.Add($l)
}
if ($inWsl2 -and -not $setNet) { $out.Add('networkingMode=mirrored'); $setNet = $true }
if (-not $haveSection) { $out.Add('[wsl2]'); $out.Add('networkingMode=mirrored') }
Set-Content -Path $cfg -Value $out -Encoding ASCII
& $wsl --shutdown

# ---------- usbipd-win ----------
Step 'USB passthrough (usbipd-win)'
$usbipd = Join-Path $env:ProgramFiles 'usbipd-win\usbipd.exe'
if (-not (Test-Path $usbipd)) {
  if (-not $UsbipdMsi) { throw 'usbipd-win installer missing from the package' }
  $p = Start-Process msiexec.exe -ArgumentList "/i `"$($UsbipdMsi.FullName)`" /qn /norestart" -Wait -PassThru
  if ($p.ExitCode -notin 0, 3010) { throw "usbipd-win install failed ($($p.ExitCode))" }
}
& $usbipd --version

# ---------- import the AFIF-PBX system ----------
$existing = (& $wsl --list --quiet) -replace "`0", '' | ForEach-Object { $_.Trim() } | Where-Object { $_ -eq $Distro }
if ($existing -and $Fresh) {
  Step 'Removing the previous AFIF-PBX system (fresh install requested)'
  & $wsl --unregister $Distro
  $existing = $null
}
if (-not $existing) {
  Step 'Installing the AFIF-PBX system (this takes a few minutes)'
  if (-not (Test-Path $Rootfs)) { throw "System image not found: $Rootfs" }
  New-Item -ItemType Directory -Force -Path $DistroDir | Out-Null
  & $wsl --import $Distro $DistroDir $Rootfs --version 2
  if ($LASTEXITCODE -ne 0) { throw "wsl --import failed ($LASTEXITCODE)" }
  # The installer copy of the image is no longer needed
  Remove-Item $Rootfs -Force -ErrorAction SilentlyContinue
} else {
  Step 'AFIF-PBX system already installed — keeping its data'
}

Step 'First boot'
& $wsl -d $Distro -u root -- bash -lc "systemctl is-system-running --wait >/dev/null 2>&1; systemctl enable --now afif-dongle freepbx >/dev/null 2>&1; echo booted"

# ---------- firewall ----------
Step 'Opening SIP, RTP and web ports'
$rules = @(
  @{ Name = 'AFIF-PBX SIP UDP';  Protocol = 'UDP'; Ports = @('5060','5061','5160','5161') },
  @{ Name = 'AFIF-PBX SIP TCP';  Protocol = 'TCP'; Ports = @('5060','5061','5160','5161') },
  @{ Name = 'AFIF-PBX RTP';      Protocol = 'UDP'; Ports = @('10000-20000') },
  @{ Name = 'AFIF-PBX Web';      Protocol = 'TCP'; Ports = @('80','443','8001','8003') }
)
foreach ($r in $rules) {
  Get-NetFirewallRule -DisplayName $r.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
  New-NetFirewallRule -DisplayName $r.Name -Direction Inbound -Action Allow -Protocol $r.Protocol -LocalPort $r.Ports -Profile Private,Domain | Out-Null
  if (Get-Command New-NetFirewallHyperVRule -ErrorAction SilentlyContinue) {
    $id = 'AFIFPBX-' + ($r.Name -replace '[^A-Za-z]', '')
    Get-NetFirewallHyperVRule -Name $id -ErrorAction SilentlyContinue | Remove-NetFirewallHyperVRule
    New-NetFirewallHyperVRule -Name $id -DisplayName $r.Name -Direction Inbound -VMCreatorId $WslVmCreator `
      -Protocol $r.Protocol -LocalPorts $r.Ports -Action Allow | Out-Null
  }
}

# ---------- start at logon ----------
Step 'Starting AFIF-PBX automatically at sign-in'
$exe = Join-Path $AppDir 'AFIF-PBX.exe'
$action  = New-ScheduledTaskAction -Execute $exe -Argument '--tray'
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -RunLevel Highest -LogonType Interactive
Register-ScheduledTask -TaskName 'AFIF-PBX' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null

Step 'AFIF-PBX setup complete'
Stop-Transcript | Out-Null
exit 0
