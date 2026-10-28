; Inno Setup script. Build with: iscc installer.iss /DSourceDir=out-fd /DOutName=FluentVantageToolbar-Setup /DBundled=0
#ifndef SourceDir
  #define SourceDir "out"
#endif
#ifndef OutName
  #define OutName "FluentVantageToolbar-Setup"
#endif
#ifndef Bundled
  #define Bundled 0
#endif
#define AppName "Fluent Vantage Toolbar"
#define AppVersion "1.0.0"

[Setup]
AppId={{6B2D0C0E-5A3F-4E0B-9C61-4F1A7E2D9A10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Mohamed Elnaggar
DefaultDirName={autopf}\FluentLegionToolbar
DefaultGroupName={#AppName}
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=installers
OutputBaseFilename={#OutName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#SourceDir}\app.ico
UninstallDisplayIcon={app}\FluentLegionToolbar.exe
DisableProgramGroupPage=yes
CloseApplications=force
RestartApplications=no
CloseApplicationsFilter=FluentLegionToolbar.exe

[Tasks]
Name: "startup"; Description: "Start with Windows with administrator rights, without a UAC prompt (scheduled task)"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\FluentLegionToolbar.exe"; Parameters: "--show"
Name: "{commonprograms}\{#AppName}"; Filename: "{app}\FluentLegionToolbar.exe"; Parameters: "--show"
Name: "{commonprograms}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install-startup.ps1"" -Silent"; Flags: runhidden waituntilterminated; Tasks: startup; StatusMsg: "Registering the startup task..."
Filename: "{app}\FluentLegionToolbar.exe"; Parameters: "--show"; Description: "Open {#AppName}"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall-startup.ps1"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveStartupTask"

[Code]
procedure OpenInstinct(Sender: TObject);
var ErrorCode: Integer;
begin
  ShellExec('open', 'https://instinct.com', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure InitializeWizard;
var
  Credit: TNewStaticText;
begin
  Credit := TNewStaticText.Create(WizardForm);
  Credit.Parent := WizardForm;
  Credit.Caption := 'brought to you by Instinct';
  Credit.Left := ScaleX(12);
  Credit.Top := WizardForm.ClientHeight - ScaleY(33);
  Credit.Font.Color := clBlue;
  Credit.Cursor := crHand;
  Credit.OnClick := @OpenInstinct;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  { Standard elevated process termination works with older hide-to-tray builds,
    portable copies and different installation paths. No dependency on new IPC. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM FluentLegionToolbar.exe', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  { taskkill returns nonzero when there is no instance. Verify absence separately. }
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "if(Get-Process -Name FluentLegionToolbar -ErrorAction SilentlyContinue){exit 1}else{exit 0}"',
    '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := 'Could not verify toolbar shutdown. Close the toolbar and retry.'
  else if ExitCode <> 0 then
    Result := 'A toolbar instance is still running. Close it and retry.';
end;

#if Str(Bundled) == "0"
function HasDotNet8: Boolean;
var
  R: TFindRec;
begin
  Result := False;
  { The app runtimeconfig requires Microsoft.NETCore.App, not WindowsDesktop.App.
    commonpf64 avoids the 32-bit installer's Program Files redirection. }
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.NETCore.App\8.*'), R) then
  begin
    repeat
      if ((R.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
         FileExists(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.NETCore.App\') + R.Name + '\coreclr.dll') then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(R);
    FindClose(R);
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not HasDotNet8 then
    if MsgBox('The .NET 8 Runtime (x64) was not found. The app will not start without it.' + #13#10 +
              'Install it from https://dotnet.microsoft.com/download/dotnet/8.0 or use the installer that includes .NET.' + #13#10#13#10 +
              'Continue anyway?', mbConfirmation, MB_YESNO) = IDNO then Result := False;
end;
#endif



