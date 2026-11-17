; One-click installer for Fractal Explorer (Inno Setup 6). Built by tools/build_installer.ps1,
; which publishes the app (self-contained: no .NET install needed) and adds the starter places.
;
; Per-user install (no admin prompt) into %LocalAppData%\Programs\Fractal Explorer, with Start
; menu and desktop shortcuts and an uninstaller. Running Setup.exe installs straight away.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif

[Setup]
AppId={{6B0E3C52-2F7A-4C1E-9D0B-8E3F5A7C1D24}
AppName=Fractal Explorer
AppVersion={#AppVersion}
AppVerName=Fractal Explorer {#AppVersion}
DefaultDirName={localappdata}\Programs\Fractal Explorer
PrivilegesRequired=lowest
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
OutputDir=..\artifacts
OutputBaseFilename=FractalExplorer-Setup-{#AppVersion}
SetupIconFile=..\Fractal\Assets\fractal.ico
UninstallDisplayIcon={app}\Fractal.exe
UninstallDisplayName=Fractal Explorer
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
CloseApplications=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[InstallDelete]
; Starter places are replaced as a set on upgrade (a user's own history lives elsewhere and is never touched).
Type: filesandordirs; Name: "{app}\StarterPlaces"

[Icons]
Name: "{autoprograms}\Fractal Explorer"; Filename: "{app}\Fractal.exe"
Name: "{autodesktop}\Fractal Explorer"; Filename: "{app}\Fractal.exe"

[Run]
Filename: "{app}\Fractal.exe"; Description: "Start Fractal Explorer"; Flags: nowait postinstall skipifsilent
