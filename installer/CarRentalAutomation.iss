#ifndef PublishDir
  #error PublishDir must point to the staged application folder
#endif
#ifndef InstallerOutput
  #define InstallerOutput "output"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{E6B36E18-345B-4EB3-B65E-496B76560B48}
AppName=CarRentalAutomation
AppVersion={#AppVersion}
AppPublisher=Open Yazilim
DefaultDirName={localappdata}\Programs\CarRentalAutomation
DefaultGroupName=CarRentalAutomation
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#InstallerOutput}
OutputBaseFilename=CarRentalAutomation-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\CarRentalAutomation.WinForms.exe
CloseApplications=yes
InfoBeforeFile=stage-one-notice.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "appsettings.json,*.pdb,*.log,secrets.json,.env,.env.*"; Flags: ignoreversion recursesubdirs createallsubdirs
; Never replace a configuration which has already been provisioned on this PC.
Source: "appsettings.distribution.json"; DestDir: "{app}"; DestName: "appsettings.json"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\CarRentalAutomation"; Filename: "{app}\CarRentalAutomation.WinForms.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\CarRentalAutomation"; Filename: "{app}\CarRentalAutomation.WinForms.exe"; WorkingDir: "{app}"; Tasks: desktopicon

; Database data and browser sessions are deliberately not uninstall targets.
