; Hirsch Notify — Inno Setup 6 installer
;
; Replaces the WiX v5 MSI installer. Collects port, event source mode, and
; service account at install time; registers the Windows service, firewall
; rule, and failure actions; launches the web UI on completion.
;
; Build with: ISCC.exe /DMyAppVersion=1.0.9 installer\HirschNotify.iss
; Output:     installer\Output\HirschNotifySetup-v1.0.9.exe

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppName=Hirsch Notify
AppId={{8F2E4A6B-1C3D-4E5F-A6B7-C8D9E0F1A2B3}
AppVersion={#MyAppVersion}
AppPublisher=Hirsch Notify
AppPublisherURL=https://github.com/aconley-hirsch/HirschNotify
DefaultDirName={autopf}\HirschNotify
DefaultGroupName=Hirsch Notify
DisableProgramGroupPage=yes
DisableReadyPage=no
DisableWelcomePage=no
PrivilegesRequired=admin
OutputBaseFilename=HirschNotifySetup-v{#MyAppVersion}
OutputDir=Output
SetupIconFile=assets\app-icon.ico
WizardImageFile=assets\wizard-image.bmp
WizardSmallImageFile=assets\wizard-small.bmp
WizardImageStretch=no
Compression=lzma2/ultra
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=Hirsch Notify
UninstallDisplayIcon={app}\HirschNotify.exe

[Files]
Source: "..\bin\Release\net10.0\win-x64\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Dirs]
Name: "{app}\Logs"; Permissions: users-modify
Name: "{app}\Data"; Permissions: users-modify
Name: "{app}\Keys"; Permissions: users-modify

[Run]
; Service registration — fresh install only.
Filename: "{sys}\sc.exe"; \
  Parameters: "create HirschNotify binPath= ""\""{app}\HirschNotify.exe\"" --urls http://0.0.0.0:{code:GetPort}"" DisplayName= ""Hirsch Notify"" start= auto obj= ""{code:GetSvcAccount}"" password= ""{code:GetSvcPassword}"""; \
  Flags: runhidden; \
  Check: IsFreshInstall; \
  StatusMsg: "Registering Windows service..."

Filename: "{sys}\sc.exe"; \
  Parameters: "description HirschNotify ""Monitors Velocity access control events and sends push notifications."""; \
  Flags: runhidden; \
  Check: IsFreshInstall

Filename: "{sys}\sc.exe"; \
  Parameters: "failure HirschNotify reset= 86400 actions= restart/60000/restart/60000/restart/60000"; \
  Flags: runhidden; \
  Check: IsFreshInstall

; Firewall rule — fresh install only.
Filename: "{sys}\netsh.exe"; \
  Parameters: "advfirewall firewall add rule name=""Hirsch Notify"" dir=in action=allow protocol=TCP localport={code:GetPort}"; \
  Flags: runhidden; \
  Check: IsFreshInstall; \
  StatusMsg: "Adding firewall rule..."

; Start (or restart) the service — always runs. PrepareToInstall stopped it
; first on upgrades so file replacement wouldn't fail on a locked EXE.
Filename: "{sys}\sc.exe"; \
  Parameters: "start HirschNotify"; \
  Flags: runhidden; \
  StatusMsg: "Starting service..."

; Launch the web UI in the default browser — fresh install only.
Filename: "http://localhost:{code:GetPort}"; \
  Flags: shellexec postinstall skipifsilent nowait; \
  Description: "Open Hirsch Notify web UI"; \
  Check: IsFreshInstall

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop HirschNotify"; Flags: runhidden; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete HirschNotify"; Flags: runhidden; RunOnceId: "DelSvc"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Hirsch Notify"""; Flags: runhidden; RunOnceId: "DelFw"

[Code]
var
  PortPage: TInputQueryWizardPage;
  EventSourcePage: TInputOptionWizardPage;
  AccountPage: TInputQueryWizardPage;
  WasServicePresent: Boolean;

function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'),
                 'query HirschNotify',
                 '',
                 SW_HIDE,
                 ewWaitUntilTerminated,
                 ResultCode)
            and (ResultCode = 0);
end;

function IsFreshInstall(): Boolean;
begin
  Result := not WasServicePresent;
end;

function InitializeSetup(): Boolean;
begin
  // Snapshot service state before anything else — PrepareToInstall will stop
  // the service on upgrades, so by then ServiceExists() would still return
  // true but we want the "was this a fresh install?" answer locked in here.
  WasServicePresent := ServiceExists();
  Result := True;
end;

procedure InitializeWizard();
begin
  { --- Port page --- }
  PortPage := CreateInputQueryPage(wpSelectDir,
    'Port Configuration',
    'Choose the port for the Hirsch Notify web interface.',
    'Enter the port number for the web UI (1-65535). The default is 5100.' + #13#10 +
    'After installation, the interface will be available at http://localhost:<port>.');
  PortPage.Add('Port number:', False);
  PortPage.Values[0] := '5100';

  { --- Event source page --- }
  EventSourcePage := CreateInputOptionPage(PortPage.ID,
    'Event Source',
    'Choose how events are received from the access control system.',
    'Velocity Adapter reads connection details from the local registry automatically. ' +
    'WebSocket requires additional configuration in the web interface after installation.',
    True,   { Exclusive radio buttons }
    False); { Not listbox style }
  EventSourcePage.Add('Velocity Adapter (Recommended) — connects directly to Velocity database');
  EventSourcePage.Add('WebSocket — connects to Velocity Web API event stream');
  EventSourcePage.SelectedValueIndex := 0;

  { --- Service account page --- }
  AccountPage := CreateInputQueryPage(EventSourcePage.ID,
    'Service Account',
    'Enter the Windows account to run the service as.',
    'The service must run as a user with access to the Velocity database. ' +
    'Enter the account in DOMAIN\Username format. Both fields are required.');
  AccountPage.Add('Username:', False);
  AccountPage.Add('Password:', True);  { masked }
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  { On upgrades, skip the three custom pages — we're only replacing binaries,
    not reconfiguring the service. }
  if WasServicePresent then
  begin
    if (PageID = PortPage.ID) or
       (PageID = EventSourcePage.ID) or
       (PageID = AccountPage.ID) then
      Result := True;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  PortNum: Integer;
begin
  Result := True;

  if CurPageID = PortPage.ID then
  begin
    try
      PortNum := StrToInt(PortPage.Values[0]);
      if (PortNum < 1) or (PortNum > 65535) then
      begin
        MsgBox('Port must be between 1 and 65535.', mbError, MB_OK);
        Result := False;
      end;
    except
      MsgBox('Port must be a number between 1 and 65535.', mbError, MB_OK);
      Result := False;
    end;
  end
  else if CurPageID = AccountPage.ID then
  begin
    if (Trim(AccountPage.Values[0]) = '') or (Trim(AccountPage.Values[1]) = '') then
    begin
      MsgBox('Both username and password are required.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function GetPort(Value: String): String;
begin
  if WasServicePresent then
    { On upgrades we don't have the port the user originally chose. SCM
      already has the right binPath with --urls, so this substitution is only
      consumed by [Run] entries that are gated on IsFreshInstall anyway.
      Return the default for safety if something slips through. }
    Result := '5100'
  else
    Result := PortPage.Values[0];
end;

function GetSvcAccount(Value: String): String;
begin
  if WasServicePresent then
    Result := 'LocalSystem'
  else
    Result := AccountPage.Values[0];
end;

function GetSvcPassword(Value: String): String;
begin
  if WasServicePresent then
    Result := ''
  else
    Result := AccountPage.Values[1];
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if WasServicePresent then
  begin
    { Blocking stop so the file-copy phase doesn't hit a locked EXE. `net stop`
      waits for SERVICE_STOPPED internally and returns non-zero only if the
      service refused to stop — which we ignore, since the user gets a clearer
      error from the subsequent file-copy failure if it does. }
    Exec(ExpandConstant('{sys}\net.exe'),
         'stop HirschNotify',
         '',
         SW_HIDE,
         ewWaitUntilTerminated,
         ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigPath: String;
  Contents: String;
  Mode: String;
begin
  if (CurStep = ssPostInstall) and IsFreshInstall() then
  begin
    { Hand off the chosen Event Source mode to the service's first-boot
      handler in Program.cs:~195. Deleted by the app after consumption. }
    if EventSourcePage.SelectedValueIndex = 0 then
      Mode := 'VelocityAdapter'
    else
      Mode := 'WebSocket';

    ConfigPath := ExpandConstant('{app}\install-config.json');
    Contents := '{"eventSourceMode":"' + Mode + '"}';
    SaveStringToFile(ConfigPath, Contents, False);
  end;
end;
