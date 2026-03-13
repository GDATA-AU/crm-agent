; setup.iss — Inno Setup installer script for LGA CRM Agent
;
; Prerequisites:
;   1. Inno Setup 6.x installed (https://jrsoftware.org/isinfo.php)
;   2. The project has been published:
;        dotnet publish dotnet/CrmAgent -c Release -r win-x64 --self-contained -o publish
;   3. Compile this script:
;        iscc installer\setup.iss
;
; Output: installer\Output\crm-agent-setup.exe

#define AppName    "LGA CRM Agent"
#define AppVersion "1.0.0"
#define AppPublisher "GDATA-AU"
#define ServiceName "crm-agent"
#define ExeName "CrmAgent.exe"
; Path to the published output, relative to this script
#define PublishDir "..\publish"

[Setup]
AppId={{A3F6B2D1-4C8E-4F2A-9D3B-7E1C5A0F8B2D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/GDATA-AU/crm-agent
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=Output
OutputBaseFilename=crm-agent-setup
Compression=lzma2/ultra64
SolidCompression=yes
; Require admin so we can register the Windows service
PrivilegesRequired=admin
; Minimum Windows 10
MinVersion=10.0
WizardStyle=modern
; Allow upgrade installs (same AppId)
CloseApplications=yes
RestartIfNeededByRun=no
UninstallDisplayName={#AppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.ConfigPageCaption=Service Configuration
english.ConfigPageDescription=Enter the connection details for this agent instance.
english.LblPortalUrl=Portal URL:
english.LblPortalUrlHint=e.g. https://portal.example.com
english.LblApiKey=Agent API Key:
english.LblAzureConn=Azure Storage Connection String:
english.LblAzureConnHint=DefaultEndpointsProtocol=https;AccountName=...

[Files]
; Copy everything from the publish folder
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; No Start Menu shortcuts needed for a background service — just an uninstaller entry
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Code]

//-----------------------------------------------------------------------
// Custom wizard page variables
//-----------------------------------------------------------------------
var
  ConfigPage: TInputQueryWizardPage;

//-----------------------------------------------------------------------
// CreateCustomPages — called by Inno Setup during wizard page creation
//-----------------------------------------------------------------------
procedure InitializeWizard();
begin
  ConfigPage := CreateInputQueryPage(
    wpSelectDir,
    ExpandConstant('{cm:ConfigPageCaption}'),
    ExpandConstant('{cm:ConfigPageDescription}'),
    '');

  ConfigPage.Add(ExpandConstant('{cm:LblPortalUrl}'),     False);
  ConfigPage.Add(ExpandConstant('{cm:LblApiKey}'),        True);   // password field
  ConfigPage.Add(ExpandConstant('{cm:LblAzureConn}'),     True);   // password field

  // Friendly placeholder hints (Inno Setup doesn't support watermarks, so
  // pre-fill with example text that the user clears)
  ConfigPage.Values[0] := 'https://portal.example.com';
  ConfigPage.Values[1] := '';
  ConfigPage.Values[2] := '';
end;

//-----------------------------------------------------------------------
// Validate inputs before allowing Next
//-----------------------------------------------------------------------
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = ConfigPage.ID then
  begin
    if Trim(ConfigPage.Values[0]) = '' then
    begin
      MsgBox('Portal URL is required.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Trim(ConfigPage.Values[1]) = '' then
    begin
      MsgBox('Agent API Key is required.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Trim(ConfigPage.Values[2]) = '' then
    begin
      MsgBox('Azure Storage Connection String is required.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

//-----------------------------------------------------------------------
// Helpers
//-----------------------------------------------------------------------

// Escape a string for inclusion inside a JSON string value.
function JsonEscape(const S: String): String;
var
  I: Integer;
  C: Char;
  R: String;
begin
  R := '';
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if C = '"'  then R := R + '\"'
    else if C = '\' then R := R + '\\'
    else R := R + C;
  end;
  Result := R;
end;

//-----------------------------------------------------------------------
// WriteAppSettings — writes appsettings.json from wizard inputs
//-----------------------------------------------------------------------
procedure WriteAppSettings();
var
  PortalUrl, ApiKey, AzureConn: String;
  Lines: TArrayOfString;
  FilePath: String;
begin
  PortalUrl := JsonEscape(Trim(ConfigPage.Values[0]));
  ApiKey    := JsonEscape(Trim(ConfigPage.Values[1]));
  AzureConn := JsonEscape(Trim(ConfigPage.Values[2]));
  FilePath  := ExpandConstant('{app}\appsettings.json');

  SetArrayLength(Lines, 20);
  Lines[0]  := '{';
  Lines[1]  := '  "Serilog": {';
  Lines[2]  := '    "MinimumLevel": {';
  Lines[3]  := '      "Default": "Information",';
  Lines[4]  := '      "Override": {';
  Lines[5]  := '        "Microsoft": "Warning",';
  Lines[6]  := '        "System": "Warning"';
  Lines[7]  := '      }';
  Lines[8]  := '    }';
  Lines[9]  := '  },';
  Lines[10] := '  "Agent": {';
  Lines[11] := '    "PortalUrl": "' + PortalUrl + '",';
  Lines[12] := '    "AgentApiKey": "' + ApiKey + '",';
  Lines[13] := '    "AzureStorageConnectionString": "' + AzureConn + '",';
  Lines[14] := '    "PollIntervalMs": 30000,';
  Lines[15] := '    "HeartbeatIntervalMs": 30000';
  Lines[16] := '  }';
  Lines[17] := '}';
  SetArrayLength(Lines, 18);

  SaveStringsToFile(FilePath, Lines, False);
end;

//-----------------------------------------------------------------------
// Service helpers
//-----------------------------------------------------------------------

procedure StopAndDeleteService();
begin
  // Stop — ignore errors if already stopped or not installed
  Exec('sc.exe', 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, 0);
  // Brief pause for the service to terminate
  Sleep(2000);
  Exec('sc.exe', 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, 0);
  Sleep(1000);
end;

procedure InstallAndStartService();
var
  ExePath: String;
  ResultCode: Integer;
begin
  ExePath := ExpandConstant('{app}\{#ExeName}');

  // Register service
  Exec('sc.exe',
    'create {#ServiceName} binPath= "\"' + ExePath + '\"" start= auto DisplayName= "LGA CRM Agent"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Set description
  Exec('sc.exe',
    'description {#ServiceName} "Polls the council portal for extraction jobs and writes results to Azure Blob Storage."',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Configure automatic restart on failure
  Exec('sc.exe',
    'failure {#ServiceName} reset= 86400 actions= restart/10000/restart/30000/restart/60000',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Start
  Exec('sc.exe', 'start {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

//-----------------------------------------------------------------------
// CurStepChanged — hook into install/uninstall lifecycle
//-----------------------------------------------------------------------
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    // Remove any existing service before overwriting files
    StopAndDeleteService();
  end;

  if CurStep = ssPostInstall then
  begin
    WriteAppSettings();
    InstallAndStartService();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopAndDeleteService();
  end;
end;
