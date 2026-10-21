<#
  AFIF-PBX Native — post-install setup (runs elevated from the installer).
  - removes the old WSL-based AFIF-PBX if present and gives the USB dongle back to Windows
  - registers the AFIF-PBX Windows service and starts it
  - allows SIP, RTP and the web panel through the firewall for the local network
  - starts the tray icon at sign-in
#>
param([Parameter(Mandatory)] [string] $AppDir)
$ErrorActionPreference = 'Continue'
$DataDir = Join-Path $env:ProgramData 'AFIF-PBX'
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
Start-Transcript -Path (Join-Path $DataDir 'install.log') -Append | Out-Null
function Step($t) { Write-Host "`n=== $t ===" }

# ---------- old WSL edition ----------
Step 'Removing the previous AFIF-PBX (WSL edition) if installed'
$env:WSL_UTF8 = '1'
$wsl = Join-Path $env:SystemRoot 'System32\wsl.exe'
if (Test-Path $wsl) {
  $distros = (& $wsl --list --quiet 2>$null) -replace "`0", '' | ForEach-Object { $_.Trim() }
  if ($distros -contains 'AFIF-PBX') {
    & $wsl --terminate AFIF-PBX 2>$null
    & $wsl --unregister AFIF-PBX
    Write-Host 'Old AFIF-PBX Linux system removed.'
  }
}
Unregister-ScheduledTask -TaskName 'AFIF-PBX' -Confirm:$false -ErrorAction SilentlyContinue
if (Get-Command Get-NetFirewallHyperVRule -ErrorAction SilentlyContinue) {
  Get-NetFirewallHyperVRule -Name 'AFIFPBX-*' -ErrorAction SilentlyContinue | Remove-NetFirewallHyperVRule
}
$usbipd = Join-Path $env:ProgramFiles 'usbipd-win\usbipd.exe'
if (Test-Path $usbipd) {
  # the old version "shared" the dongle with Linux; give it back to Windows
  & $usbipd detach --all 2>$null
  & $usbipd unbind --all 2>$null
  Write-Host 'USB dongle returned to Windows (usbipd unbind).'
}
Remove-Item 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6C1F2A4E-AF1F-4B2B-9C55-A1F1B0000001}_is1' -Recurse -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData 'AFIF-PBX\system') -Recurse -Force -ErrorAction SilentlyContinue

# ---------- service ----------
Step 'Installing the AFIF-PBX service'
$exe = Join-Path $AppDir 'AFIF-PBX-Service.exe'
$svc = Get-Service -Name 'AFIF-PBX' -ErrorAction SilentlyContinue
if ($svc) {
  sc.exe config AFIF-PBX binPath= "`"$exe`"" start= auto | Out-Null
} else {
  sc.exe create AFIF-PBX binPath= "`"$exe`"" start= auto DisplayName= "AFIF-PBX Phone System" | Out-Null
}
sc.exe description AFIF-PBX "AFIF-PBX by AFIF-TECH: SIP phone system with GSM gateway, IVR, voicemail, SMS and call recording." | Out-Null
sc.exe failure AFIF-PBX reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
Start-Service -Name 'AFIF-PBX' -ErrorAction Continue

$port = $null
for ($i = 0; $i -lt 30 -and -not $port; $i++) {
  Start-Sleep 1
  $f = Join-Path $DataDir 'http-port.txt'
  if (Test-Path $f) { $port = (Get-Content $f).Trim() }
}
Write-Host "Web panel port: $port"

# ---------- firewall (local network only) ----------
Step 'Allowing phones on the local network to reach AFIF-PBX'
Get-NetFirewallRule -DisplayName 'AFIF-PBX*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'AFIF-PBX Phone System' -Direction Inbound -Action Allow -Program $exe `
  -RemoteAddress LocalSubnet -Profile Any | Out-Null

# ---------- tray icon at sign-in ----------
Step 'Starting the AFIF-PBX tray icon at sign-in'
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'AFIF-PBX' -Value "`"$(Join-Path $AppDir 'AFIF-PBX.exe')`" --tray"

# ---------- dongle driver check ----------
$ports = Get-CimInstance Win32_PnPEntity -Filter "Name LIKE '%(COM%'" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name
if ($ports -match 'PC UI') { Write-Host 'Huawei dongle ports found.' }
else { Write-Host 'Huawei dongle ports not found yet (plug in the dongle / install its driver).' }

Step 'AFIF-PBX setup complete'
Stop-Transcript | Out-Null
exit 0
