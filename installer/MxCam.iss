; MxCam 설치 파일 (Inno Setup 6)
; 빌드: ISCC /DMyAppVersion=1.2.3 /DSourceExe=out\MxCam.exe installer\MxCam.iss
; 사용자 계정 설치(관리자 권한 불필요) → 자동 업데이트가 UAC 없이 동작

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\dist\MxCam.exe"
#endif
#ifndef OutputDir
  #define OutputDir "..\out"
#endif

[Setup]
AppId={{6B0E3D61-4F0A-4C8E-9C2B-3F6E7A1D2C10}
AppName=MxCam
AppVersion={#MyAppVersion}
AppVerName=MxCam {#MyAppVersion}
AppPublisher=YMX
AppPublisherURL=https://github.com/MrDH1004/MxCam
AppSupportURL=https://github.com/MrDH1004/MxCam/issues
AppUpdatesURL=https://github.com/MrDH1004/MxCam/releases
VersionInfoVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\MxCam
DefaultGroupName=MxCam
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=MxCam-Setup-{#MyAppVersion}
SetupIconFile=..\src\MxCam\MxCam.ico
UninstallDisplayIcon={app}\MxCam.exe
UninstallDisplayName=MxCam {#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "MxCam.exe"; Flags: ignoreversion

[Icons]
Name: "{group}\MxCam"; Filename: "{app}\MxCam.exe"
Name: "{group}\{cm:UninstallProgram,MxCam}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\MxCam"; Filename: "{app}\MxCam.exe"; Tasks: desktopicon

[Run]
; 일반 설치: 마지막 화면에서 "MxCam 실행" 선택
Filename: "{app}\MxCam.exe"; Description: "{cm:LaunchProgram,MxCam}"; Flags: nowait postinstall skipifsilent
; 자동 업데이트(무인 설치): /relaunch=1 이면 설치 후 바로 다시 실행
Filename: "{app}\MxCam.exe"; Parameters: "--after-update"; Flags: nowait; Check: ShouldRelaunch

[UninstallRun]
; 탐색기 우클릭 메뉴 제거
Filename: "{app}\MxCam.exe"; Parameters: "--unregister"; RunOnceId: "UnregisterShell"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: files; Name: "{app}\MxCam.exe.old"
Type: files; Name: "{app}\MxCam.exe.new"

[Code]
function ShouldRelaunch(): Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

// 자동 업데이트로 실행된 경우, 이전 MxCam이 완전히 종료될 때까지 잠시 기다린다
function InitializeSetup(): Boolean;
var
  i: Integer;
begin
  for i := 0 to 50 do
  begin
    if not CheckForMutexes('MxCam_single_instance') then Break;
    Sleep(200);
  end;
  Result := True;
end;
