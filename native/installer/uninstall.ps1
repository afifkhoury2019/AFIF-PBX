param([switch] $RemoveData)
$ErrorActionPreference = 'Continue'
Get-Process 'AFIF-PBX' -ErrorAction SilentlyContinue | Stop-Process -Force
Stop-Service -Name 'AFIF-PBX' -Force -ErrorAction SilentlyContinue
sc.exe delete AFIF-PBX | Out-Null
Get-NetFirewallRule -DisplayName 'AFIF-PBX*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'AFIF-PBX' -ErrorAction SilentlyContinue
if ($RemoveData) {
  Remove-Item (Join-Path $env:ProgramData 'AFIF-PBX') -Recurse -Force -ErrorAction SilentlyContinue
}
exit 0
