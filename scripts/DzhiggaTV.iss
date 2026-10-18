#ifndef AppSource
  #error AppSource must point to the published portable directory
#endif
#ifndef OutputPath
  #define OutputPath "..\outputs"
#endif
[Setup]
AppId={{BC4C5157-E428-495E-8434-E6CF7B3A391A}
AppName=DzhiggaTV
AppVersion=1.0.0
AppPublisher=DzhiggaTV contributors
DefaultDirName={localappdata}\Programs\DzhiggaTV
DefaultGroupName=DzhiggaTV
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputPath}
OutputBaseFilename=DzhiggaTV-setup-win-x64
SetupIconFile=..\src\DzhiggaTV.App\Assets\DzhiggaTV.ico
UninstallDisplayIcon={app}\DzhiggaTV.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE
CloseApplications=yes
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked
[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\DzhiggaTV"; Filename: "{app}\DzhiggaTV.exe"
Name: "{userdesktop}\DzhiggaTV"; Filename: "{app}\DzhiggaTV.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\DzhiggaTV.exe"; Description: "Запустить DzhiggaTV"; Flags: nowait postinstall skipifsilent
