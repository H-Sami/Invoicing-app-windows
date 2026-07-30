#define MyAppVersion "1.0.0"
#define MyAppPublisher "MHC Technology"
#define MyAppExeName "MHC.Invoicing.exe"
#ifdef LocalQaArtifact
#define MyAppName "MHC Invoices V4 - LOCAL QA"
#define MyAppId "{{E8161B82-C5AA-4D64-A1E7-CA1071C79891}"
#define MyAppDirectory "MHC Invoices V4 Local QA"
#else
#define MyAppName "MHC Invoices V4"
#define MyAppId "{{94DDA1A1-673E-4EBD-AD76-337F150024B4}"
#define MyAppDirectory "MHC Invoices V4"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
DefaultDirName={localappdata}\Programs\MHC Technology\{#MyAppDirectory}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\artifacts\installer
#ifdef LocalQaArtifact
OutputBaseFilename=MHC-Invoices-V4-Setup-x64-LocalQA
#else
OutputBaseFilename=MHC-Invoices-V4-Setup-x64-Unsigned
#endif
SetupIconFile=..\src\MHC.Invoicing.App\Assets\MHCLogo-20260729.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
ChangesAssociations=no
UsePreviousAppDir=yes
UsePreviousGroup=yes

[Languages]
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\MHCLogo-20260729.ico"; IconIndex: 0
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\MHCLogo-20260729.ico"; IconIndex: 0; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

#include "UpgradeCleanup.iss"
