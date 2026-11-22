; bmwDecode installer (Inno Setup 6).
;
; Public build (CI):   iscc bmwDecode.iss /DAppVersion=0.2.0 /DPublishDir=..\publish
; Private build:       installer\build-private.ps1 -EcuPath C:\EDIABAS\Ecu -DriverPath "<K+DCAN driver folder>"
;                      adds /DEcuDir=... and /DDriverDir=... so the setup carries the SGBD files and the FTDI driver.
;                      BMW SGBD files are BMW's property: never publish a private build.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{6B2F8B87-4C1E-4E0F-9C55-0B7D1E5A8B42}
AppName=bmwDecode
AppVersion={#AppVersion}
AppPublisher=bmwDecode contributors
AppPublisherURL=https://github.com/blacksan/bmwDecode
DefaultDirName={autopf}\bmwDecode
DefaultGroupName=bmwDecode
UninstallDisplayIcon={app}\bmwDecode.exe
OutputDir={#OutputDir}
#ifdef EcuDir
OutputBaseFilename=bmwDecode-{#AppVersion}-setup-private
#else
OutputBaseFilename=bmwDecode-{#AppVersion}-setup
#endif
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
LicenseFile=..\LICENSE.md
DisableProgramGroupPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
#ifndef EcuDir
Name: "copysgbd"; Description: "Copy the SGBD files from INPA/EDIABAS and ISTA on this PC into bmwDecode (works without them afterwards)"; GroupDescription: "Diagnostic data:"; Check: HasLocalSgbd
#endif
#ifdef DriverDir
Name: "driver"; Description: "Install the K+DCAN cable driver (FTDI)"; GroupDescription: "Cable:"
#endif
Name: "latency"; Description: "Set FTDI cable latency timer to 1 ms (faster, more reliable diagnostics)"; GroupDescription: "Cable:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "ftdi-latency.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "copy-sgbd.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
#ifdef EcuDir
Source: "{#EcuDir}\*"; DestDir: "{commonappdata}\bmwDecode\Ecu"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
#ifdef DriverDir
Source: "{#DriverDir}\*"; DestDir: "{app}\driver"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
#ifdef DataDir
; Built-in reference data (BMW-derived, private build only): job/table metadata, English texts, type keys, VIN ranges.
Source: "{#DataDir}\sgbd.pack"; DestDir: "{app}\Data"; Flags: ignoreversion
#endif

[Icons]
Name: "{autoprograms}\bmwDecode"; Filename: "{app}\bmwDecode.exe"
Name: "{autodesktop}\bmwDecode"; Filename: "{app}\bmwDecode.exe"; Tasks: desktopicon

[Run]
#ifndef EcuDir
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\copy-sgbd.ps1"" -Target ""{commonappdata}\bmwDecode\Ecu"""; StatusMsg: "Copying SGBD files…"; Flags: runhidden waituntilterminated; Tasks: copysgbd
#endif
#ifdef DriverDir
Filename: "{sys}\pnputil.exe"; Parameters: "/add-driver ""{app}\driver\*.inf"" /subdirs /install"; StatusMsg: "Installing the cable driver…"; Flags: runhidden waituntilterminated; Tasks: driver
#endif
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\tools\ftdi-latency.ps1"""; StatusMsg: "Setting FTDI latency timer…"; Flags: runhidden waituntilterminated; Tasks: latency
Filename: "{app}\bmwDecode.exe"; Description: "{cm:LaunchProgram,bmwDecode}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\tools"

[Code]
function HasLocalSgbd: Boolean;
begin
  Result := DirExists('C:\EDIABAS\Ecu') or DirExists(ExpandConstant('{commonpf64}\BMW\ISPI\TRIC\ISTA\Ecu'))
    or DirExists('C:\EC-APPS\ISTA\Ecu') or DirExists('C:\Rheingold\Ecu');
end;
