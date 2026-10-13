#if Ver < EncodeVer(6, 7, 3)
  #error Inno Setup 6.7.3 or newer is required.
#endif
#ifndef AppVersion
  #error AppVersion must be passed by installer\build.ps1.
#endif
#ifndef SourceRoot
  #error SourceRoot must be passed by installer\build.ps1.
#endif
#ifndef PublishDir
  #error PublishDir must be passed by installer\build.ps1.
#endif
#ifndef OutputDir
  #error OutputDir must be passed by installer\build.ps1.
#endif
#ifndef UsbPcapUrl
  #error UsbPcapUrl must be passed by installer\build.ps1.
#endif
#ifndef UsbPcapSha256
  #error UsbPcapSha256 must be passed by installer\build.ps1.
#endif

[Setup]
AppId={{C7BF00D9-2083-4B85-9DD5-81D96F8E9418}
AppName=Expert Mouse Pro 2026
AppVersion={#AppVersion}
AppPublisher=Expert Mouse Pro 2026 contributors
AppComments=Independent community remapper; not affiliated with Kensington.
DefaultDirName={autopf}\Expert Mouse Pro 2026
DefaultGroupName=Expert Mouse Pro 2026
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
PrivilegesRequired=admin
LicenseFile={#SourceRoot}\LICENSE
InfoBeforeFile={#SourceRoot}\installer\setup-information.txt
SetupIconFile={#SourceRoot}\ExpertMouse\Assets\App.ico
UninstallDisplayIcon={app}\ExpertMouse.exe
WizardStyle=modern dark polar includetitlebar
WizardSizePercent=110
Compression=lzma2
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=ExpertMousePro2026-Setup-{#AppVersion}-win-x64
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Expert Mouse Pro 2026"; Filename: "{app}\ExpertMouse.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Expert Mouse Pro 2026"; Filename: "{app}\ExpertMouse.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{group}\Uninstall Expert Mouse Pro 2026"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\ExpertMouse.exe"; Parameters: "--app"; Description: "Open Expert Mouse Pro 2026"; Flags: nowait postinstall skipifsilent runascurrentuser; Check: CanLaunch

[Code]
var
  DriverPage: TWizardPage;
  DriverConsent: TNewCheckBox;
  DriverSummary: TNewStaticText;
  DownloadPage: TDownloadWizardPage;
  DriverAdded: Boolean;

function DriverInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\USBPcap') and
    FileExists(ExpandConstant('{sys}\drivers\USBPcap.sys'));
end;

function DriverPendingRemoval: Boolean;
var
  Flag: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SYSTEM\CurrentControlSet\Services\USBPcap', 'DeleteFlag', Flag);
end;

procedure InitializeWizard;
begin
  DriverAdded := False;
  DriverPage := CreateCustomPage(wpSelectTasks, 'USBPcap capture driver',
    'The six DirectLaunch buttons require this independent third-party driver.');
  DriverSummary := TNewStaticText.Create(DriverPage);
  DriverSummary.Parent := DriverPage.Surface;
  DriverSummary.SetBounds(0, 0, DriverPage.SurfaceWidth, DriverPage.SurfaceHeight - ScaleY(60));
  DriverSummary.AutoSize := False;
  DriverSummary.WordWrap := True;
  if DriverInstalled then
    DriverSummary.Caption := 'USBPcap is already installed. Setup will leave it unchanged.' + #13#10#13#10 +
      'This app captures only the connected Expert Mouse Pro. Exit X-Mouse Button Control before using the new app.'
  else
    DriverSummary.Caption := 'Setup can download the unchanged official USBPcap 1.5.4.0 installer from GitHub.' + #13#10#13#10 +
      'The download is verified against a pinned SHA-256. The driver installs USB hub filters and requires a reboot.' + #13#10#13#10 +
      'Windows 11 compatibility is experimental. If Windows rejects the driver, do not disable Secure Boot, Memory Integrity, or signature enforcement.' + #13#10#13#10 +
      'Project and licenses: https://desowin.org/usbpcap/';
  DriverConsent := TNewCheckBox.Create(DriverPage);
  DriverConsent.Parent := DriverPage.Surface;
  DriverConsent.SetBounds(0, DriverPage.SurfaceHeight - ScaleY(45), DriverPage.SurfaceWidth, ScaleY(35));
  DriverConsent.Caption := 'Download and install official USBPcap (requires restart).';
  DriverConsent.Checked := False;
  DriverConsent.Visible := not DriverInstalled;
  DownloadPage := CreateDownloadPage('Downloading USBPcap',
    'Retrieving the verified official capture-driver installer.', nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = DriverPage.ID) and not DriverInstalled and not DriverConsent.Checked then
  begin
    MsgBox('USBPcap is required. To continue, approve its official download, or cancel and install USBPcap separately.' + #13#10#13#10 +
      'Setup will not download or install the driver without your consent.', mbInformation, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if FindWindowByWindowName('Expert Mouse Pro 2026') <> 0 then
  begin
    Result := 'Exit Expert Mouse Pro 2026 from its blue M tray menu, then choose Retry.';
    Exit;
  end;
  if DriverPendingRemoval then
  begin
    NeedsRestart := True;
    Result := 'USBPcap is pending removal. Restart Windows before installing.';
    Exit;
  end;
  if DriverInstalled then Exit;
  if not DriverConsent.Checked then
  begin
    Result := 'The required USBPcap download has not been approved.';
    Exit;
  end;
  try
    DownloadPage.Clear;
    DownloadPage.Add('{#UsbPcapUrl}', 'USBPcapSetup-1.5.4.0.exe', '{#UsbPcapSha256}');
    DownloadPage.Show;
    try
      DownloadPage.Download;
    finally
      DownloadPage.Hide;
    end;
    if not Exec(ExpandConstant('{tmp}\USBPcapSetup-1.5.4.0.exe'), '/S', '',
      SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      RaiseException('The official USBPcap installer could not be started.');
    if ExitCode <> 0 then
      RaiseException(Format('The official USBPcap installer returned error %d.', [ExitCode]));
    if not DriverInstalled then
      RaiseException('USBPcap did not register its service and driver. Setup will not report success.');
    DriverAdded := True;
    Log('USBPcap installed from its official, SHA-256-verified installer. A reboot is required.');
  except
    Result := 'USBPcap setup failed: ' + GetExceptionMessage;
    Log(Result);
  end;
end;

function NeedRestart: Boolean;
begin
  Result := DriverAdded;
end;

function CanLaunch: Boolean;
begin
  Result := not DriverAdded;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if not Exec(ExpandConstant('{app}\ExpertMouse.exe'), '--install-migrate-startup',
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      RaiseException('Could not check the existing Windows startup entry.');
    if ExitCode <> 0 then
      RaiseException(Format('The app installed, but startup migration failed (%d). Open the app and re-save Start with Windows.', [ExitCode]));
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := False;
  if FindWindowByWindowName('Expert Mouse Pro 2026') <> 0 then
  begin
    MsgBox('Exit Expert Mouse Pro 2026 from its tray menu before uninstalling.', mbInformation, MB_OK);
    Exit;
  end;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExitCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    if not Exec(ExpandConstant('{app}\ExpertMouse.exe'), '--uninstall-cleanup',
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      RaiseException('Could not remove this installation''s startup entries. Uninstall was canceled.');
    if ExitCode <> 0 then
      RaiseException(Format('Startup cleanup failed (%d). Uninstall was canceled so no broken startup entry is left behind.', [ExitCode]));
  end;
end;
