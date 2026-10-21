param([switch] $RemoveData)
$ErrorActionPreference = 'Continue'
$env:WSL_UTF8 = '1'
Unregister-ScheduledTask -TaskName 'AFIF-PBX' -Confirm:$false -ErrorAction SilentlyContinue
Get-NetFirewallRule -DisplayName 'AFIF-PBX*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if (Get-Command Get-NetFirewallHyperVRule -ErrorAction SilentlyContinue) {
  Get-NetFirewallHyperVRule -Name 'AFIFPBX-*' -ErrorAction SilentlyContinue | Remove-NetFirewallHyperVRule
}
Get-Process 'AFIF-PBX' -ErrorAction SilentlyContinue | Stop-Process -Force
if ($RemoveData) {
  & "$env:SystemRoot\System32\wsl.exe" --unregister AFIF-PBX
  Remove-Item (Join-Path $env:ProgramData 'AFIF-PBX') -Recurse -Force -ErrorAction SilentlyContinue
} else {
  & "$env:SystemRoot\System32\wsl.exe" --terminate AFIF-PBX
}
exit 0
