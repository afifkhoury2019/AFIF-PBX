; AFIF-PBX Native installer (Inno Setup 6) — built by .github/workflows/native.yml

#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif
#define Dist "..\dist"

[Setup]
AppId={{A1F1B0B0-2E6D-4C8F-9A1E-AF1F0000NA71}
AppName=AFIF-PBX
AppVersion={#AppVersion}
AppVerName=AFIF-PBX {#AppVersion}
AppPublisher=AFIF-TECH
AppComments=Phone system with GSM gateway for Windows — no WSL, no virtual machine
DefaultDirName={autopf}\AFIF-PBX
DefaultGroupName=AFIF-PBX
DisableProgramGroupPage=yes
OutputDir={#Dist}\out
OutputBaseFilename=AFIF-PBX-Setup-{#AppVersion}
SetupIconFile={#Dist}\branding\afif-pbx.ico
UninstallDisplayIcon={app}\AFIF-PBX.exe
WizardStyle=modern
WizardImageFile={#Dist}\branding\wizard-large.bmp
WizardSmallImageFile={#Dist}\branding\wizard-small.bmp
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
CloseApplications=force
RestartApplications=no

[Messages]
WelcomeLabel1=Welcome to AFIF-PBX
WelcomeLabel2=This installs AFIF-PBX {#AppVersion}, the AFIF-TECH phone system for Windows: extensions, the GSM line through your Huawei dongle, ring groups, IVR, voicemail, SMS and call recording.%n%nIt runs as a normal Windows service — no Linux, WSL or virtual machine.%n%nIf the previous AFIF-PBX (WSL edition) is installed, it is removed and the dongle is given back to Windows.

[Tasks]
Name: "dongledriver"; Description: "Install the AFIF-PBX dongle driver (Huawei E303 and similar — no Huawei software needed)"
Name: "tailscale"; Description: "Install Tailscale — use Linphone from anywhere through your tailnet (no router ports)"; Check: not TailscaleInstalled

[InstallDelete]
; leftovers of the WSL edition
Type: filesandordirs; Name: "{app}\system"
Type: filesandordirs; Name: "{app}\deps"
Type: filesandordirs; Name: "{app}\scripts"

[Files]
Source: "{#Dist}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "{#Dist}\scripts\*"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "{#Dist}\driver\*"; DestDir: "{app}\driver"; Flags: ignoreversion

[Icons]
Name: "{group}\AFIF-PBX"; Filename: "{app}\AFIF-PBX.exe"
Name: "{autodesktop}\AFIF-PBX"; Filename: "{app}\AFIF-PBX.exe"
Name: "{group}\AFIF-PBX web panel"; Filename: "http://localhost/"
Name: "{group}\Install dongle driver"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\install-driver.ps1"""; IconFilename: "{app}\AFIF-PBX.exe"
Name: "{group}\Uninstall AFIF-PBX"; Filename: "{uninstallexe}"

[Run]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\install.ps1"" -AppDir ""{app}"""; \
  StatusMsg: "Setting up the AFIF-PBX service…"; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\install-driver.ps1"" -DriverDir ""{app}\driver"""; \
  StatusMsg: "Installing the AFIF-PBX dongle driver…"; Flags: runhidden waituntilterminated; Tasks: dongledriver
Filename: "msiexec.exe"; \
  Parameters: "/i ""{app}\deps\tailscale.msi"" /qn /norestart TS_UNATTENDEDMODE=always TS_NOLAUNCH=yes"; \
  StatusMsg: "Installing Tailscale…"; Flags: runhidden waituntilterminated; Tasks: tailscale
Filename: "{app}\AFIF-PBX.exe"; Description: "Start AFIF-PBX now"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
var
  RemoveData: Boolean;

function TailscaleInstalled(): Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf64}\Tailscale\tailscale.exe'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // stop a running copy so its files can be replaced
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM AFIF-PBX.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop AFIF-PBX', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(2500);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM AFIF-PBX-Service.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  RemoveData := SuppressibleMsgBox('Also delete AFIF-PBX data (extensions, voicemail, recordings, SMS, call log)?' + #13#10#13#10 +
    'Choose No to keep it for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Args: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    Args := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\scripts\uninstall.ps1') + '"';
    if RemoveData then Args := Args + ' -RemoveData';
    Exec('powershell.exe', Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
