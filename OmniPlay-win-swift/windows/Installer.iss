; 安装包脚本。StageDir 与 AppVersion 由 package.ps1 用 /D 传进来。
#ifndef StageDir
  #define StageDir "publish"
#endif
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppName=觅影 Windows 演示
AppVersion={#AppVersion}
AppPublisher=OmniPlay
DefaultDirName={autopf}\OmniPlaySpike
DisableProgramGroupPage=yes
DisableWelcomePage=no
OutputDir=..\artifacts
OutputBaseFilename=OmniPlay-win-swift-{#AppVersion}-x64-setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Icons]
Name: "{autoprograms}\觅影 Windows 演示"; Filename: "{app}\OmniPlaySpike.exe"
Name: "{autodesktop}\觅影 Windows 演示"; Filename: "{app}\OmniPlaySpike.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\OmniPlaySpike.exe"; Description: "运行觅影 Windows 演示"; Flags: nowait postinstall skipifsilent
