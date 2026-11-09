; Установщик Music Mixer для Windows (Inno Setup 6).
; Собирается в GitHub Actions (.github/workflows/installer.yml):
; ISCC /DAppVersion=1.0.N /DPublishDir=<путь к publish> installer\MusicMixer.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif
#define AppName "Music Mixer"
#define AppExe "MusicMixer.exe"

[Setup]
AppId={{6F3B2A8E-4C1D-4E7A-9B5F-2D8C1A7E4B93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=zana052010
AppPublisherURL=https://github.com/zana052010/Music-Mixer
AppSupportURL=https://github.com/zana052010/Music-Mixer/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=MusicMixer-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=4
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
SetupIconFile=..\src\MusicMixer.Desktop\Assets\MusicMixer.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
MinVersion=10.0

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\tools\demucs\__pycache__"
