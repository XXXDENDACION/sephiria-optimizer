; Sephiria Optimizer - Inno Setup installation script
; Build: ISCC.exe installer\SephiriaOptimizer.iss
; Output: dist\SephiriaOptimizer_v0.1.1_Setup.exe
;
; Behavior: automatically locate the Sephiria installation via the Steam registry + libraryfolders.vdf,
;           then install BepInEx + the plugin. (Distribution package = release DLL without cheat features)

#define MyAppName "Sephiria Optimizer"
#define MyAppVersion "0.1.1"
#define MyAppPublisher "siggu"

[Setup]
AppId={{B2E4B8A1-1C2D-4E3F-9A6B-7C8D9E0F1A2B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Install with administrator privileges because the game may be under Program Files
PrivilegesRequired=admin
DefaultDirName={code:GetSephiriaDir}
DisableProgramGroupPage=yes
DisableWelcomePage=no
OutputDir=..\dist
OutputBaseFilename=SephiriaOptimizer_v{#MyAppVersion}_Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Show in Add/Remove Programs for uninstallation
Uninstallable=yes
AppComments=Sephiria inventory placement optimizer mod (BepInEx)

[Languages]
Name: "default"; MessagesFile: "compiler:Default.isl"

[Files]
; Copy all contents of dist\payload (winhttp.dll, doorstop, BepInEx\, README) to the game folder
Source: "..\dist\payload\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Run]
Filename: "{app}\README.md"; Description: "Open installation guide (README)"; Flags: postinstall shellexec skipifsilent

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

// Read libraryfolders.vdf to find Sephiria in libraries on other drives as well.
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

// Used by DefaultDirName: automatically detect the Sephiria folder
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
  // Default value (allows the user to select the folder manually if detection fails)
  Result := 'C:\Program Files (x86)\Steam\steamapps\common\Sephiria';
end;

// Verify that Sephiria.exe exists on the folder confirmation page
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    if not FileExists(AddBackslash(WizardDirValue) + 'Sephiria.exe') then
    begin
      if MsgBox('Sephiria.exe was not found in the selected folder.' + #13#10 +
                'Make sure this is the Sephiria installation folder. (Steam → right-click Sephiria → Manage → Browse local files)' + #13#10#13#10 +
                'Continue anyway?', mbConfirmation, MB_YESNO) = IDNO then
        Result := False;
    end;
  end;
end;
