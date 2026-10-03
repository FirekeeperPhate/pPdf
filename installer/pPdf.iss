; Inno Setup installer for pPdf, two editions from the same script:
;   Light: framework-dependent build, needs the .NET 10 Desktop Runtime (small setup)
;   Full:  self-contained build, .NET runtime included (no prerequisites)
; Build with build.ps1, which publishes to ..\publish\<edition> and passes
; /DFlavor=Light|Full and /DAppVersion=<version from pPdf.csproj>.

#ifndef Flavor
  #define Flavor "Light"
#endif
#if Flavor != "Light" && Flavor != "Full"
  #error Flavor must be Light or Full
#endif
#ifndef AppVersion
  #error AppVersion is required (pass /DAppVersion=x.y.z)
#endif

#define AppName "pPdf"
#define AppExe "pPdf.exe"
#define SourceDir AddBackslash(SourcePath) + "..\publish\" + LowerCase(Flavor)

[Setup]
; One AppId for both editions: Light and Full replace each other
AppId={{5B7E2C41-6A0D-4F3B-9C58-2D1E7A94B6F0}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion} ({#Flavor})
AppPublisher=Phate
AppPublisherURL=https://github.com/FirekeeperPhate/pPdf
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default (no admin rights needed); all users (with UAC) can be chosen
; in the first dialog. HKA below follows the choice (HKCU or HKLM).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=Output
OutputBaseFilename=pPdf-Setup-{#AppVersion}-{#Flavor}
SetupIconFile=..\src\pPdf\Assets\pPdf.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} ({#Flavor})
WizardStyle=modern dynamic
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
; Created by the running app: setup and uninstall ask to close pPdf first
AppMutex=pPdf.Running
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
RuntimeMissing=pPdf requires the .NET 10 Desktop Runtime (x64), which does not appear to be installed.%n%nYes = open the download page and close setup%nNo = install anyway%nCancel = close setup%n%nAlternatively use the Full edition, which includes the runtime.
OpenWithTask=Add pPdf to the "Open with" menu of PDF and EPUB files

[Tasks]
Name: "openwith"; Description: "{cm:OpenWithTask}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; Excludes: "*.pdb,settings.json"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Parent of Capabilities: removed at uninstall once empty
Root: HKA; Subkey: "Software\pPdf"; Flags: uninsdeletekeyifempty
; ProgId used by "Open with" and by Settings > Default apps (never forced)
Root: HKA; Subkey: "Software\Classes\pPdf.Document"; ValueType: string; ValueData: "PDF document"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\pPdf.Document\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\pPdf.Document\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\pPdf.Book"; ValueType: string; ValueData: "EPUB book"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\pPdf.Book\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\pPdf.Book\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".epub"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "pPdf.Document"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.epub\OpenWithProgids"; ValueType: string; ValueName: "pPdf.Book"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKA; Subkey: "Software\pPdf\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\pPdf\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "PDF and EPUB reader with text search, selection and annotations"; Tasks: openwith
Root: HKA; Subkey: "Software\pPdf\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "pPdf.Document"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\pPdf\Capabilities\FileAssociations"; ValueType: string; ValueName: ".epub"; ValueData: "pPdf.Book"; Tasks: openwith
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "pPdf"; ValueData: "Software\pPdf\Capabilities"; Flags: uninsdeletevalue; Tasks: openwith

[UninstallDelete]
; the converted books (they are rebuilt from the originals whenever needed)
Type: filesandordirs; Name: "{localappdata}\pPdf"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Update started by pPdf itself (/SILENT /RELAUNCH=1 /OPEN="file"): start it again on that file,
; as the user who ran it (not elevated, also for an all-users install).
Filename: "{app}\{#AppExe}"; Parameters: "{code:RelaunchParameters}"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[Code]
{ Switching edition (Full <-> Light) or upgrading: remove the previous program files so no
  stale runtime or library DLLs are left behind. Both editions publish everything flat in the
  program folder, so only files are touched (never folders the user may have put there);
  settings are kept in %AppData% and the uninstaller files (unins*) are kept. }
procedure CleanProgramFolder;
var
  App, Name: String;
  FindRec: TFindRec;
begin
  App := ExpandConstant('{app}\');
  { Only a folder this setup installed before }
  if (WizardForm.PrevAppDir = '') or
     (CompareText(AddBackslash(WizardForm.PrevAppDir), App) <> 0) or
     not FileExists(App + '{#AppExe}') then
    Exit;
  { The program itself first: if it cannot be deleted pPdf is still running, and nothing else
    must be removed (an interrupted setup would leave a program that cannot start). }
  if not DeleteFile(App + '{#AppExe}') then
    Exit;
  if FindFirst(App + '*', FindRec) then
  begin
    try
      repeat
        Name := FindRec.Name;
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0) and
           (CompareText(Copy(Name, 1, 5), 'unins') <> 0) then
          DeleteFile(App + Name);
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

{ The "Open with" task unticked on an upgrade: Inno skips the registry lines of an unselected
  task, so the registrations of the previous install would stay until uninstall. }
procedure RemoveOpenWith;
begin
  RegDeleteKeyIncludingSubkeys(HKA, 'Software\Classes\pPdf.Document');
  RegDeleteKeyIncludingSubkeys(HKA, 'Software\Classes\pPdf.Book');
  RegDeleteKeyIncludingSubkeys(HKA, 'Software\Classes\Applications\{#AppExe}');
  RegDeleteKeyIncludingSubkeys(HKA, 'Software\pPdf\Capabilities');
  RegDeleteValue(HKA, 'Software\RegisteredApplications', 'pPdf');
  RegDeleteValue(HKA, 'Software\Classes\.pdf\OpenWithProgids', 'pPdf.Document');
  RegDeleteValue(HKA, 'Software\Classes\.epub\OpenWithProgids', 'pPdf.Book');
end;

function OpenEvent(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: String): THandle;
external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL;
external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): BOOL;
external 'CloseHandle@kernel32.dll stdcall';

{ Update started by pPdf (/NOTIFYPID=<its process id>): tell it that setup is really starting
  (after the UAC prompt of an all-users install; if that is refused, pPdf stays open), then
  give it time to close before the AppMutex check, which comes after InitializeSetup. }
procedure ReleasePPdf;
var
  Pid: Integer;
  Ready: THandle;
  Waited: Integer;
begin
  Pid := StrToIntDef(ExpandConstant('{param:NOTIFYPID|0}'), 0);
  if Pid > 0 then
  begin
    Ready := OpenEvent($0002 { EVENT_MODIFY_STATE }, False, 'pPdf.UpdateReady.' + IntToStr(Pid));
    if Ready <> 0 then
    begin
      SetEvent(Ready);
      CloseHandle(Ready);
    end;
  end;
  if ExpandConstant('{param:RELAUNCH|0}') = '1' then
  begin
    Waited := 0;
    while CheckForMutexes('pPdf.Running') and (Waited < 15000) do
    begin
      Sleep(250);
      Waited := Waited + 250;
    end;
  end;
end;

function ShouldRelaunch: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

function RelaunchParameters(Param: String): String;
var
  FileToOpen: String;
begin
  FileToOpen := ExpandConstant('{param:OPEN|}');
  if FileToOpen <> '' then
    Result := AddQuotes(FileToOpen)
  else
    Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    CleanProgramFolder;
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('openwith') then
    RemoveOpenWith;
end;

#if Flavor == "Light"
{ Looks for a release (not preview) x64 .NET 10 Desktop Runtime. On ARM64 Windows the x64 runtime
  lives in dotnet\x64, the plain dotnet folder holds the native ARM64 one. }
function HasDesktopRuntime(Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if Root = '' then
    Exit;
  if FindFirst(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\10.*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Pos('-', FindRec.Name) = 0) then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

{ The places the app host itself looks in: DOTNET_ROOT, the registered install location and the
  standard folder. }
function IsDesktopRuntimeInstalled: Boolean;
var
  Registered: String;
begin
  Result := HasDesktopRuntime(GetEnv('DOTNET_ROOT_X64')) or HasDesktopRuntime(GetEnv('DOTNET_ROOT'));
  if Result then
    Exit;
  if RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Registered) then
    Result := HasDesktopRuntime(Registered);
  if Result then
    Exit;
  if IsArm64 then
    Result := HasDesktopRuntime(ExpandConstant('{commonpf64}\dotnet\x64'))
  else
    Result := HasDesktopRuntime(ExpandConstant('{commonpf64}\dotnet'));
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDesktopRuntimeInstalled then
    case SuppressibleMsgBox(CustomMessage('RuntimeMissing'), mbConfirmation, MB_YESNOCANCEL, IDNO) of
      IDYES:
        begin
          ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
          Result := False;
        end;
      IDCANCEL:
        Result := False;
    end;
  if Result then
    ReleasePPdf;
end;
#else
function InitializeSetup: Boolean;
begin
  ReleasePPdf;
  Result := True;
end;
#endif
