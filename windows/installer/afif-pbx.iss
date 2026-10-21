; AFIF-PBX installer (Inno Setup 6)
; Built by .github/workflows/build.yml — expects the staging folder in ..\..\dist

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define Dist "..\..\dist"

[Setup]
AppId={{6C1F2A4E-AF1F-4B2B-9C55-A1F1B0000001}
AppName=AFIF-PBX
AppVersion={#AppVersion}
AppVerName=AFIF-PBX {#AppVersion}
AppPublisher=AFIF-TECH
AppComments=Business phone system with GSM gateway for Windows 11
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
Compression=lzma2/fast
SolidCompression=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.22000
CloseApplications=yes
RestartApplications=no

[Messages]
WelcomeLabel1=Welcome to AFIF-PBX
WelcomeLabel2=This will install AFIF-PBX {#AppVersion} — your business phone system with Huawei E303 GSM gateway — on this computer.%n%nSetup installs the Windows Subsystem for Linux and USB passthrough if they are missing. An internet connection is needed for the WSL update.

[Tasks]
Name: "fresh"; Description: "Replace an existing AFIF-PBX system (deletes its current data)"; Flags: unchecked; Check: DistroExists

[Files]
Source: "{#Dist}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "{#Dist}\scripts\*"; DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "{#Dist}\deps\*"; DestDir: "{app}\deps"; Flags: ignoreversion
Source: "{#Dist}\system\afif-pbx-rootfs.tar.gz"; DestDir: "{app}\system"; Flags: ignoreversion nocompression

[Icons]
Name: "{group}\AFIF-PBX"; Filename: "{app}\AFIF-PBX.exe"
Name: "{autodesktop}\AFIF-PBX"; Filename: "{app}\AFIF-PBX.exe"
Name: "{group}\AFIF-PBX web interface"; Filename: "http://localhost/"
Name: "{group}\Uninstall AFIF-PBX"; Filename: "{uninstallexe}"

[Run]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\install.ps1"" -AppDir ""{app}"" {code:FreshArg}"; \
  StatusMsg: "Setting up the AFIF-PBX system (WSL, USB passthrough, firewall)…"; Flags: waituntilterminated
Filename: "{app}\AFIF-PBX.exe"; Description: "Start AFIF-PBX now"; Flags: postinstall nowait skipifsilent; Check: not NeedRestart

[Code]
var
  RemoveData: Boolean;

function DistroExists: Boolean;
var
  Keys: TArrayOfString;
  I: Integer;
  Name: String;
begin
  Result := False;
  if RegGetSubkeyNames(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Lxss', Keys) then
    for I := 0 to GetArrayLength(Keys) - 1 do
      if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Lxss\' + Keys[I], 'DistributionName', Name) then
        if CompareText(Name, 'AFIF-PBX') = 0 then Result := True;
end;

function FreshArg(Param: String): String;
begin
  if WizardIsTaskSelected('fresh') then Result := '-Fresh' else Result := '';
end;

function NeedRestart(): Boolean;
begin
  Result := FileExists(ExpandConstant('{commonappdata}\AFIF-PBX\reboot-required'));
end;

function InitializeUninstall(): Boolean;
begin
  RemoveData := MsgBox('Also delete the AFIF-PBX phone system and ALL of its data (extensions, voicemail, call logs)?' + #13#10#13#10 +
                       'Choose No to keep the data for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
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
