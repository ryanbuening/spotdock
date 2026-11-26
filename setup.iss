; Installer for SpotDock with the Spotify Web browser bridge.
; Build: dotnet publish SpotDock.csproj -c Release -o publish  then  ISCC.exe setup.iss

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
OutputDir=installer
OutputBaseFilename=SpotDock-Setup
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
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
Source: "publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "spotdock-bridge\*"; DestDir: "{app}\spotdock-bridge"; Flags: ignoreversion recursesubdirs

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

; taskkill by process name would be dangerous here: it kills ANY copy of the
; widget, including one installed in another folder or built by hand.
; Closing is handled by CloseApplications - through Restart Manager it touches
; only the processes holding files from the install folder.

[Code]
var
  ExtPage: TOutputMsgWizardPage;

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
