; Sephiria Optimizer - Inno Setup 설치 스크립트
; 빌드: ISCC.exe installer\SephiriaOptimizer.iss
; 결과: dist\SephiriaOptimizer_v0.1.1_Setup.exe
;
; 동작: Steam 레지스트리 + libraryfolders.vdf 로 세피리아 설치 폴더를 자동 탐지하여
;       BepInEx + 플러그인을 설치한다. (배포용 = 치트 기능 없는 릴리스 DLL)

#define MyAppName "Sephiria Optimizer"
#define MyAppVersion "0.1.1"
#define MyAppPublisher "siggu"

[Setup]
AppId={{B2E4B8A1-1C2D-4E3F-9A6B-7C8D9E0F1A2B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; 게임이 Program Files 아래 있을 수 있으므로 관리자 권한으로 설치
PrivilegesRequired=admin
DefaultDirName={code:GetSephiriaDir}
DisableProgramGroupPage=yes
DisableWelcomePage=no
OutputDir=..\dist
OutputBaseFilename=SephiriaOptimizer_v{#MyAppVersion}_Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 제거 시 Add/Remove Programs 에 표시
Uninstallable=yes
AppComments=세피리아 인벤토리 최적 배치 모드 (BepInEx)

[Languages]
Name: "default"; MessagesFile: "compiler:Default.isl"

[Files]
; dist\payload 의 모든 내용(winhttp.dll, doorstop, BepInEx\, README)을 게임 폴더로 복사
Source: "..\dist\payload\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Run]
Filename: "{app}\README.md"; Description: "설치 안내(README) 열기"; Flags: postinstall shellexec skipifsilent

[Code]
function NormalizePath(s: String): String;
begin
  StringChangeEx(s, '/', '\', True);
  Result := s;
end;

function HasSephiria(dir: String): Boolean;
begin
  Result := (dir <> '') and FileExists(AddBackslash(dir) + 'Sephiria.exe');
end;

function SteamFromReg(): String;
var p: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, 'Software\Valve\Steam', 'SteamPath', p) then
    Result := NormalizePath(p)
  else if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath', p) then
    Result := NormalizePath(p)
  else if RegQueryStringValue(HKLM, 'SOFTWARE\Valve\Steam', 'InstallPath', p) then
    Result := NormalizePath(p);
end;

// libraryfolders.vdf 를 읽어 다른 드라이브의 라이브러리에서도 세피리아를 찾는다.
function FindInLibraries(steam: String): String;
var vdf, content, rest, libpath, cand: String; q1, q2, idx: Integer;
begin
  Result := '';
  vdf := AddBackslash(steam) + 'steamapps\libraryfolders.vdf';
  if not LoadStringFromFile(vdf, content) then exit;
  rest := content;
  idx := Pos('"path"', rest);
  while idx > 0 do
  begin
    rest := Copy(rest, idx + 6, Length(rest));
    q1 := Pos('"', rest);
    if q1 = 0 then break;
    rest := Copy(rest, q1 + 1, Length(rest));
    q2 := Pos('"', rest);
    if q2 = 0 then break;
    libpath := Copy(rest, 1, q2 - 1);
    StringChangeEx(libpath, '\\', '\', True);
    libpath := NormalizePath(libpath);
    cand := AddBackslash(libpath) + 'steamapps\common\Sephiria';
    if HasSephiria(cand) then
    begin
      Result := cand;
      exit;
    end;
    rest := Copy(rest, q2 + 1, Length(rest));
    idx := Pos('"path"', rest);
  end;
end;

// DefaultDirName 으로 사용: 세피리아 폴더 자동 탐지
function GetSephiriaDir(Param: String): String;
var steam, cand: String;
begin
  steam := SteamFromReg();
  if steam <> '' then
  begin
    cand := AddBackslash(steam) + 'steamapps\common\Sephiria';
    if HasSephiria(cand) then begin Result := cand; exit; end;
    cand := FindInLibraries(steam);
    if cand <> '' then begin Result := cand; exit; end;
  end;
  // 기본값 (못 찾아도 사용자가 직접 선택할 수 있게)
  Result := 'C:\Program Files (x86)\Steam\steamapps\common\Sephiria';
end;

// 폴더 확인 페이지에서 Sephiria.exe 존재 검증
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    if not FileExists(AddBackslash(WizardDirValue) + 'Sephiria.exe') then
    begin
      if MsgBox('선택한 폴더에서 Sephiria.exe 를 찾지 못했습니다.' + #13#10 +
                '세피리아 설치 폴더가 맞는지 확인하세요. (Steam → Sephiria 우클릭 → 관리 → 로컬 파일 보기)' + #13#10#13#10 +
                '그래도 계속할까요?', mbConfirmation, MB_YESNO) = IDNO then
        Result := False;
    end;
  end;
end;
