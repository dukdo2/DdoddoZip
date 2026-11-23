; 또또집 설치 파일 (Inno Setup 6)
; build.ps1 -Installer 가 /DAppVersion, /DSourceDir, /DOutputDir 를 넘겨서 만든다.
;
; - 모든 사용자용(관리자) 설치: 이 PC의 탐색기는 사용자별(HKCU) 우클릭 메뉴를 무시해서
;   HKLM에 등록해야 메뉴가 나온다. (docs/05_화면개발_결과.md 참고)
; - .NET 런타임 포함(self-contained): 받는 PC에 아무것도 깔려 있지 않아도 실행된다.
; - 우클릭 메뉴 DLL은 탐색기가 붙잡고 있으므로, 업데이트·제거 때 Restart Manager로
;   탐색기를 잠시 닫았다가 다시 연다.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef ShellExtHash
  #define ShellExtHash ""
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#define AppName "또또집"
#define AppExe "DdoddoZip.exe"

[Setup]
; AppId must never change: it ties upgrades and uninstall to the same install.
AppId={{6F3B2C1A-9D4E-4E7B-A1C5-DD0D0217A001}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Deokdo Kyle Kim
AppCopyright=Copyright (C) 2026 Deokdo Kyle Kim
VersionInfoVersion={#AppVersion}
VersionInfoProductName=DdoddoZip
DefaultDirName={autopf}\DdoddoZip
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=DdoddoZip-{#AppVersion}-setup
SetupIconFile=..\src\DdoddoZip.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} (DdoddoZip)
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Close a running 또또집 before replacing it. Only .exe files: Explorer holds the right-click DLL
; and Restart Manager cannot close it (tested 2026-11-23: three "could not close" boxes), so the
; DLL is left to "restartreplace" instead, and only when it really changed (ShellExtChanged).
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=yes
ChangesAssociations=yes

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 바로 가기 만들기"; GroupDescription: "추가 작업:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,DdoddoZip.ShellExt.dll"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
; If Explorer still holds the old DLL, replace it at the next restart instead of failing.
Source: "{#SourceDir}\DdoddoZip.ShellExt.dll"; DestDir: "{app}"; Flags: ignoreversion restartreplace uninsrestartdelete; Check: ShellExtChanged

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Drop any per-user entries left by a developer install, then register for all users.
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-shell --user"; Flags: runasoriginaluser runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Parameters: "--register-shell"; StatusMsg: "우클릭 메뉴와 연결 프로그램을 등록하는 중..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "또또집 실행"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Started by the app's auto update ("/SILENT /UPDATE"): reopen 또또집 when done.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: IsAppUpdate

[Code]
function IsAppUpdate: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/UPDATE') = 0 then
      Result := True;
end;

// True unless the installed right-click DLL is already this exact build (same SHA-256).
function ShellExtChanged: Boolean;
var
  Path: String;
begin
  Result := True;
  Path := ExpandConstant('{app}\DdoddoZip.ShellExt.dll');
  if ('{#ShellExtHash}' = '') or not FileExists(Path) then
    Exit;
  try
    Result := CompareText(GetSHA256OfFile(Path), '{#ShellExtHash}') <> 0;
  except
    Result := True;
  end;
end;

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-shell"; RunOnceId: "UnregisterShell"; Flags: runhidden waituntilterminated

[InstallDelete]
; Language folders shipped by 1.0.0 test builds; the app is Korean only now.
Type: filesandordirs; Name: "{app}\cs"
Type: filesandordirs; Name: "{app}\de"
Type: filesandordirs; Name: "{app}\es"
Type: filesandordirs; Name: "{app}\fr"
Type: filesandordirs; Name: "{app}\it"
Type: filesandordirs; Name: "{app}\ja"
Type: filesandordirs; Name: "{app}\pl"
Type: filesandordirs; Name: "{app}\pt-BR"
Type: filesandordirs; Name: "{app}\ru"
Type: filesandordirs; Name: "{app}\tr"
Type: filesandordirs; Name: "{app}\zh-Hans"
Type: filesandordirs; Name: "{app}\zh-Hant"

[UninstallDelete]
Type: files; Name: "{app}\DdoddoZip.ShellExt.dll.old*"
