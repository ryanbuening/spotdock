; Installer for SpotDock with the Spotify Web browser bridge.
; Build: dotnet publish src\SpotDock\SpotDock.csproj -c Release -o artifacts\publish  then  ISCC.exe build\setup.iss

#define MyAppName "SpotDock"
#define MyAppShortName "SpotDock"
#ifndef MyAppVersion
  #define MyAppVersion "2.0.0"
#endif
#define MyAppPublisher "ryanbuening"
#define MyAppURL "https://github.com/ryanbuening/spotdock"
#define MyAppExeName "SpotDock.exe"

[Setup]
; Own AppId: installing alongside the original widget must not overwrite it
AppId={{3F8B27D4-9C15-4A6E-B8D2-7E519A0C4F63}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={userpf}\SpotDock
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=..\artifacts\installer
OutputBaseFilename=SpotDock-Setup
SetupIconFile=..\src\SpotDock\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
en.AutoStartTask=Start with Windows
en.OpenExtDir=Open the browser extension folder
en.ExtCaption=Browser extension
en.ExtDesc=One last step - it has to be loaded by hand
en.ExtBody=SpotDock works automatically with the Spotify desktop app (classic installer and Microsoft Store). To also show playback from Spotify Web (open.spotify.com) or the Spotify browser PWA, load the SpotDock Bridge extension:%n%nHow to load it:%n%n1. Open edge://extensions (chrome://extensions in Chrome).%n2. Turn on "Developer mode".%n3. Click "Load unpacked" and pick this folder:%n%n%1%n%nWithout the extension, SpotDock still works for the Spotify desktop app.

[Tasks]
Name: "autostart"; Description: "{cm:AutoStartTask}"

[Files]
Source: "..\artifacts\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\spotdock-bridge\*"; DestDir: "{app}\spotdock-bridge"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "{#MyAppShortName}"; ValueData: """{app}\{#MyAppExeName}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    Flags: nowait postinstall skipifsilent
Filename: "{win}\explorer.exe"; Parameters: """{app}\spotdock-bridge"""; \
    Description: "{cm:OpenExtDir}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\{#MyAppShortName}"

; taskkill by process name would be dangerous here: it kills ANY copy of the
; widget, including one installed in another folder or built by hand.
; CloseApplications handles Setup/upgrades via Restart Manager, while Uninstall
; closes only the process whose full image path matches {app}\SpotDock.exe.

[Code]
const
  WM_CLOSE = $0010;
  HWND_MESSAGE = -3;
  PROCESS_TERMINATE = $0001;
  SYNCHRONIZE = $00100000;
  PROCESS_QUERY_LIMITED_INFORMATION = $1000;
  WAIT_OBJECT_0 = $00000000;

var
  ExtPage: TOutputMsgWizardPage;

function FindWindowExW(hwndParent, hwndChildAfter: LongInt; lpszClass: string; lpszWindow: LongInt): HWND;
  external 'FindWindowExW@user32.dll stdcall';
function GetWindowThreadProcessId(hWnd: HWND; var lpdwProcessId: DWORD): DWORD;
  external 'GetWindowThreadProcessId@user32.dll stdcall';
function OpenProcess(dwDesiredAccess: DWORD; bInheritHandle: BOOL; dwProcessId: DWORD): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function QueryFullProcessImageNameW(hProcess: THandle; dwFlags: DWORD; lpExeName: string; var lpdwSize: DWORD): BOOL;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function WaitForSingleObject(hHandle: THandle; dwMilliseconds: DWORD): DWORD;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function TerminateProcess(hProcess: THandle; uExitCode: UINT): BOOL;
  external 'TerminateProcess@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

procedure CloseInstalledApp();
var
  Wnd: HWND;
  Pid: DWORD;
  Proc: THandle;
  ExePath: string;
  PathLen: DWORD;
  TargetExe: string;
begin
  TargetExe := ExpandConstant('{app}\{#MyAppExeName}');

  // Locate the widget window, or fall back to its hidden dispatch window
  // (e.g. if the widget is waiting for Explorer/taskbar to come back).
  Wnd := FindWindowByClassName('SpotDock_Main');
  if Wnd = 0 then
    Wnd := FindWindowExW(HWND_MESSAGE, 0, 'SpotDock_DispatchMsg', 0);
  if Wnd = 0 then
    Exit;

  Pid := 0;
  GetWindowThreadProcessId(Wnd, Pid);
  if Pid = 0 then
    Exit;

  // Only close the process if its full image path matches THIS install folder,
  // so uninstalling one copy never kills a separate or dev copy of SpotDock.
  Proc := OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION or SYNCHRONIZE or PROCESS_TERMINATE, False, Pid);
  if Proc = 0 then
    Exit;
  try
    PathLen := 1024;
    ExePath := StringOfChar(#0, PathLen);
    if QueryFullProcessImageNameW(Proc, 0, ExePath, PathLen) then
    begin
      SetLength(ExePath, PathLen);
      if CompareText(ExePath, TargetExe) = 0 then
      begin
        PostMessage(Wnd, WM_CLOSE, 0, 0);
        if WaitForSingleObject(Proc, 3000) <> WAIT_OBJECT_0 then
        begin
          TerminateProcess(Proc, 0);
          WaitForSingleObject(Proc, 2000);
        end;
      end;
    end;
  finally
    CloseHandle(Proc);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    CloseInstalledApp();
end;

procedure InitializeWizard();
begin
  // The text is empty for now: the folder path is substituted when the page
  // is shown. {app} must not be expanded here - at this point the constant is
  // not initialized yet, Inno fails with an error (silently hangs in silent mode)
  ExtPage := CreateOutputMsgPage(wpInfoAfter,
    ExpandConstant('{cm:ExtCaption}'),
    ExpandConstant('{cm:ExtDesc}'),
    '');
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (ExtPage <> nil) and (CurPageID = ExtPage.ID) then
    ExtPage.MsgLabel.Caption :=
      FmtMessage(ExpandConstant('{cm:ExtBody}'), [ExpandConstant('{app}\spotdock-bridge')]);
end;
