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
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Lonnii API"""; Components: api; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Lonnii API"" dir=in action=allow protocol=TCP localport=5280 profile=private,domain"; Components: api; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Lonnii Discovery"""; Components: api; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Lonnii Discovery"" dir=in action=allow protocol=UDP localport=5281 profile=private,domain"; Components: api; Flags: runhidden
Filename: "{sys}\schtasks.exe"; Parameters: "/Create /F /TN ""Lonnii API"" /SC ONSTART /RU SYSTEM /RL HIGHEST /TR ""\""{app}\Api\Lonnii.Api.exe\"""""; Components: api; Flags: runhidden
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
