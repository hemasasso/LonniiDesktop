; Lonnii Desktop installer. Built by deploy/windows/build-installer.ps1, which publishes the
; API and client self-contained into artifacts\publish first - run that, not this file directly.
;
; Two install types: the shop's main machine (API + client; the API starts at boot as a SYSTEM
; scheduled task so it serves the tills before anyone signs in) and a till (client only).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\artifacts\publish"
#endif

[Setup]
AppId={{8F0B6C2E-4C1D-4E7A-9B53-2E6A1D0C7F41}
AppName=Lonnii Business
AppVersion={#AppVersion}
AppPublisher=Lonnii
DefaultDirName={autopf}\Lonnii Business
DefaultGroupName=Lonnii Business
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\artifacts\installer
OutputBaseFilename=LonniiBusiness-Setup-{#AppVersion}
SetupIconFile=..\..\src\Lonnii.Client\Assets\icon-light.ico
UninstallDisplayIcon={app}\Client\Lonnii.Client.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; The API keeps lonnii.db open; let setup stop it instead of failing on locked files.
CloseApplications=yes

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[Types]
Name: "server"; Description: "Serveur + Client (poste principal de la boutique)"
Name: "till"; Description: "Client seulement (caisse reliée au poste principal)"

[Components]
Name: "client"; Description: "Client Lonnii Business (application de caisse)"; Types: server till; Flags: fixed
Name: "api"; Description: "Serveur Lonnii Business (base de données de la boutique)"; Types: server

[Files]
Source: "{#PublishDir}\Client\*"; DestDir: "{app}\Client"; Components: client; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#PublishDir}\Api\*"; DestDir: "{app}\Api"; Components: api; Flags: recursesubdirs createallsubdirs ignoreversion

[Dirs]
; The shop's data (database, signing key, backups). Created here so its permissions can be set
; below before the API first writes to it.
Name: "{commonappdata}\Lonnii"; Components: api

[InstallDelete]
; Shortcuts from the builds named plain "Lonnii".
Type: files; Name: "{autodesktop}\Lonnii.lnk"
Type: filesandordirs; Name: "{autoprograms}\Lonnii"

[UninstallDelete]
; The verified copy of an update the API last launched (see UpdateInstallEndpoints).
Type: filesandordirs; Name: "{app}\Api\update-run"

[Icons]
Name: "{group}\Lonnii Business"; Filename: "{app}\Client\Lonnii.Client.exe"
Name: "{autodesktop}\Lonnii Business"; Filename: "{app}\Client\Lonnii.Client.exe"

[Run]
; Server: open the LAN ports the tills use (API on TCP 5280, discovery on UDP 5281), then
; register and start the API at boot as SYSTEM. Data stays in C:\ProgramData\Lonnii.
; Every network type, but only to the local network (remoteip=localsubnet): Windows files a new
; Wi-Fi or a phone hotspot as "Public", and a private-only rule left the tills unable to find the
; server there. localsubnet keeps the ports closed to anything beyond the shop's own network.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Lonnii API"""; Components: api; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Lonnii API"" dir=in action=allow protocol=TCP localport=5280 profile=any remoteip=localsubnet"; Components: api; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Lonnii Discovery"""; Components: api; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Lonnii Discovery"" dir=in action=allow protocol=UDP localport=5281 profile=any remoteip=localsubnet"; Components: api; Flags: runhidden
; Only SYSTEM (the API) and administrators may open the data folder: by default ProgramData lets
; every Windows account read it - the database, and jwt.key, with which a cashier's own account
; could forge an administrator's sign-in. SIDs, not names: French Windows says "Administrateurs".
; Re-applied on every install, so an update also repairs a folder opened up by hand.
; Three steps, in this order: take ownership (a file left with no permissions can still be reset by
; its owner), reset every file to inherit from the folder (drops grants added by hand, like
; "Users: Modify"), then lock the folder alone - its files inherit SYSTEM and Administrators from it.
; Never (OI)(CI) with /T: on a file that flag is refused and, with /C, the file is left with no
; permissions at all - which once locked the API out of its own jwt.key.
; Ownership with icacls /setowner, not takeown: takeown's /D answer is in the Windows language
; ("Y" fails on a French Windows, which wants "O"); a SID works in every language.
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\Lonnii"" /setowner *S-1-5-32-544 /T /C /Q"; Components: api; Flags: runhidden
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\Lonnii"" /reset /T /C /Q"; Components: api; Flags: runhidden
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\Lonnii"" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F /Q"; Components: api; Flags: runhidden
Filename: "{sys}\schtasks.exe"; Parameters: "/Create /F /TN ""Lonnii API"" /SC ONSTART /RU SYSTEM /RL HIGHEST /TR ""\""{app}\Api\Lonnii.Api.exe\"""""; Components: api; Flags: runhidden
; schtasks keeps Windows' task defaults and has no switch for them: stop on battery, never start on
; battery, stop after 72 hours. On a shop's laptop unplugged, that stopped the server and left every
; screen on "Serveur introuvable" (2026-11-05). So: run on battery, no time limit, and if the API
; ever stops, Windows starts it again every minute. Re-applied on each install, since /Create /F
; rebuilds the task.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0 -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1); Set-ScheduledTask -TaskName 'Lonnii API' -Settings $s"""; Components: api; Flags: runhidden
Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN ""Lonnii API"""; Components: api; Flags: runhidden
Filename: "{app}\Client\Lonnii.Client.exe"; Description: "Lancer Lonnii Business"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/End /TN ""Lonnii API"""; Flags: runhidden; RunOnceId: "EndApi"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /F /TN ""Lonnii API"""; Flags: runhidden; RunOnceId: "DeleteApi"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Lonnii.Api.exe"; Flags: runhidden; RunOnceId: "KillApi"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Lonnii API"""; Flags: runhidden; RunOnceId: "FwApi"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Lonnii Discovery"""; Flags: runhidden; RunOnceId: "FwDiscovery"

[Code]
// An upgrade must stop the running API first or its files cannot be replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/End /TN "Lonnii API"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Lonnii.Api.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

// The earlier Lonnii Business app (installed per user in AppData\Local\Programs) left a desktop
// shortcut with this very name. Windows shows only one of two same-named desktop icons, the user's
// own, so the new shortcut stayed hidden behind the old app's. Only a shortcut that points into that
// old app's folder is removed - never one the person made to anything else.
procedure RemoveOldAppShortcut(const Folder: String);
var
  Shell, Link: Variant;
  Path, Target: String;
begin
  Path := AddBackslash(Folder) + 'Lonnii Business.lnk';
  if not FileExists(Path) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Link := Shell.CreateShortcut(Path);
    Target := Link.TargetPath;
    Target := Lowercase(Target);
    if Pos('\appdata\local\programs\lonnii business\', Target) > 0 then
      DeleteFile(Path);
  except
    // Unreadable shortcut: leave it alone.
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RemoveOldAppShortcut(ExpandConstant('{userdesktop}'));
    RemoveOldAppShortcut(ExpandConstant('{commondesktop}'));
  end;
end;
