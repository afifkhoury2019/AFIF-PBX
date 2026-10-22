<#
  Installs the AFIF-PBX GSM dongle driver (WinUSB binding for Huawei voice dongles).

  How it is trusted: a code-signing certificate is created on THIS PC, used once to sign the
  driver catalog, added to the trusted stores, and its private key is deleted straight away -
  so nothing else can ever be signed with it. The driver binary itself is Microsoft's WinUSB.
#>
param([string] $DriverDir = (Join-Path $PSScriptRoot '..\driver'))
$ErrorActionPreference = 'Stop'
# re-launch as administrator when started from the Start menu
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
  Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -DriverDir `"$DriverDir`""
  exit 0
}
$DataDir = Join-Path $env:ProgramData 'AFIF-PBX'
$Work = Join-Path $DataDir 'driver'
New-Item -ItemType Directory -Force -Path $Work | Out-Null
Start-Transcript -Path (Join-Path $DataDir 'driver-install.log') -Append | Out-Null

try {
  Copy-Item (Join-Path $DriverDir 'afif-dongle.inf') $Work -Force
  Copy-Item (Join-Path $DriverDir 'afif-dongle.cat') $Work -Force
  $cat = Join-Path $Work 'afif-dongle.cat'
  $inf = Join-Path $Work 'afif-dongle.inf'

  # remove certificates left by an earlier install of this driver
  foreach ($store in 'Root', 'TrustedPublisher', 'My') {
    Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object Subject -eq 'CN=AFIF-PBX dongle driver (this PC only)' |
      ForEach-Object { Remove-Item $_.PSPath -DeleteKey -ErrorAction SilentlyContinue; Remove-Item $_.PSPath -ErrorAction SilentlyContinue }
  }
  Write-Host 'Creating a local signing certificate'
  $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=AFIF-PBX dongle driver (this PC only)' `
    -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
    -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(30)

  Write-Host 'Signing the driver catalog'
  $sig = Set-AuthenticodeSignature -FilePath $cat -Certificate $cert -HashAlgorithm SHA256
  if ($sig.Status -ne 'Valid' -and $sig.Status -ne 'UnknownError') { Write-Host "Signature status: $($sig.Status) $($sig.StatusMessage)" }

  Write-Host 'Trusting the certificate for driver installation'
  $cer = Join-Path $Work 'afif-dongle.cer'
  Export-Certificate -Cert $cert -FilePath $cer | Out-Null
  Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
  Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null

  Write-Host 'Deleting the private key'
  Remove-Item -Path ("Cert:\LocalMachine\My\" + $cert.Thumbprint) -DeleteKey

  Write-Host 'Installing the driver'
  $out = & pnputil.exe /add-driver $inf /install 2>&1
  $out | Write-Host
  if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 3010 -and ($out -notmatch 'Added|already')) {
    throw "pnputil failed ($LASTEXITCODE)"
  }
  # Re-scan so a plugged-in dongle picks the driver up immediately
  & pnputil.exe /scan-devices 2>&1 | Out-Null
  Write-Host 'AFIF-PBX dongle driver installed.'
  $code = 0
}
catch {
  Write-Host "Driver installation failed: $($_.Exception.Message)"
  $code = 1
}
Stop-Transcript | Out-Null
exit $code
