; cccxa.iss - Inno Setup script that builds a single-file setup.exe.
;
; Build (needs Inno Setup 6 - https://jrsoftware.org/isdl.php):
;   1) Run installer\publish.ps1 first to produce ..\dist\cccxa.exe
;   2) Compile this script:  "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" cccxa.iss
;   Output: ..\dist-setup\cccxa-setup.exe
;
; The setup includes a wizard page for user filtering (record only / never record),
; supports Hebrew user names and names with spaces (passed via UTF-8 temp files),
; and lets the user opt out of the desktop icon and the Start Menu folder.

#define AppName "cccxa"
#define AppVer  "1.0.0"

[Setup]
AppId={{9F2C7A54-6B1E-4C0A-9E3D-CCCXA0000001}}
AppName={#AppName}
AppVersion={#AppVer}
DefaultDirName={autopf}\cccxa
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist-setup
OutputBaseFilename=cccxa-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=cccxa

[Files]
Source: "..\dist\cccxa.exe";     DestDir: "{app}"; Flags: ignoreversion
Source: "..\appsettings.json";   DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "install.ps1";              DestDir: "{app}"; Flags: ignoreversion
Source: "uninstall.ps1";            DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut to the dashboard"; GroupDescription: "Shortcuts:"
Name: "startmenu";   Description: "Create a Start Menu folder";                 GroupDescription: "Shortcuts:"

[Icons]
Name: "{autodesktop}\cccxa - Activity Dashboard";           Filename: "{app}\cccxa.exe"; Parameters: "serve"; WorkingDir: "{app}"; IconFilename: "{sys}\shell32.dll"; IconIndex: 171; Tasks: desktopicon
Name: "{autoprograms}\cccxa\cccxa - Activity Dashboard";    Filename: "{app}\cccxa.exe"; Parameters: "serve"; WorkingDir: "{app}"; IconFilename: "{sys}\shell32.dll"; IconIndex: 171; Tasks: startmenu
Name: "{autoprograms}\cccxa\Uninstall cccxa";              Filename: "{uninstallexe}"; Tasks: startmenu

[Run]
; Configure storage permissions + register the hidden per-user scheduled task.
Filename: "powershell.exe"; \
  Parameters: "-ExecutionPolicy Bypass -NoProfile -File ""{app}\install.ps1"" -ConfigureOnly -InstallDir ""{app}"" -StorageDir ""{commonappdata}\cccxa"" -OnlyUsersFile ""{tmp}\cccxa_only.txt"" -ExcludeUsersFile ""{tmp}\cccxa_exclude.txt"" -Quiet"; \
  StatusMsg: "Configuring background service..."; Flags: runhidden waituntilterminated
; Optional: open the dashboard right after install.
Filename: "{app}\cccxa.exe"; Parameters: "serve"; Description: "Open the dashboard now"; Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
; Stop the running collector, then remove the scheduled task. schtasks/taskkill are
; used (not PowerShell) so there are no "{ }" braces for Inno to misparse as constants.
Filename: "taskkill.exe"; Parameters: "/IM cccxa.exe /F"; Flags: runhidden; RunOnceId: "KillProc"
Filename: "schtasks.exe"; Parameters: "/Delete /TN cccxa /F"; Flags: runhidden; RunOnceId: "RemoveTask"

[Code]
var
  UserPage: TWizardPage;
  OnlyMemo, ExcludeMemo: TNewMemo;

procedure InitializeWizard;
var
  lbl1, lbl2: TNewStaticText;
begin
  UserPage := CreateCustomPage(wpSelectTasks,
    'User filtering',
    'Choose which Windows users to record. Names may be in any language and may contain spaces.');

  lbl1 := TNewStaticText.Create(UserPage);
  lbl1.Parent := UserPage.Surface;
  lbl1.Top := 0;
  lbl1.Width := UserPage.SurfaceWidth;
  lbl1.AutoSize := True;
  lbl1.Caption := 'Record ONLY these users (one per line). Leave blank to record all users:';

  OnlyMemo := TNewMemo.Create(UserPage);
  OnlyMemo.Parent := UserPage.Surface;
  OnlyMemo.Left := 0;
  OnlyMemo.Top := lbl1.Top + lbl1.Height + ScaleY(4);
  OnlyMemo.Width := UserPage.SurfaceWidth;
  OnlyMemo.Height := ScaleY(72);
  OnlyMemo.ScrollBars := ssVertical;
  OnlyMemo.WordWrap := False;

  lbl2 := TNewStaticText.Create(UserPage);
  lbl2.Parent := UserPage.Surface;
  lbl2.Top := OnlyMemo.Top + OnlyMemo.Height + ScaleY(12);
  lbl2.Width := UserPage.SurfaceWidth;
  lbl2.AutoSize := True;
  lbl2.Caption := 'NEVER record these users (one per line), e.g. a work account with sensitive data:';

  ExcludeMemo := TNewMemo.Create(UserPage);
  ExcludeMemo.Parent := UserPage.Surface;
  ExcludeMemo.Left := 0;
  ExcludeMemo.Top := lbl2.Top + lbl2.Height + ScaleY(4);
  ExcludeMemo.Width := UserPage.SurfaceWidth;
  ExcludeMemo.Height := ScaleY(72);
  ExcludeMemo.ScrollBars := ssVertical;
  ExcludeMemo.WordWrap := False;
end;

procedure WriteMemoToFile(const FileName: string; Memo: TNewMemo);
var
  i: Integer;
  s, line: string;
begin
  s := '';
  for i := 0 to Memo.Lines.Count - 1 do
  begin
    line := Trim(Memo.Lines[i]);
    if line <> '' then
      s := s + line + #13#10;
  end;
  { In Unicode Inno Setup this writes UTF-8; install.ps1 reads it with -Encoding UTF8. }
  SaveStringToFile(FileName, s, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    WriteMemoToFile(ExpandConstant('{tmp}\cccxa_only.txt'), OnlyMemo);
    WriteMemoToFile(ExpandConstant('{tmp}\cccxa_exclude.txt'), ExcludeMemo);
  end;
end;
