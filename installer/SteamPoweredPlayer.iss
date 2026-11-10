; SPDX-License-Identifier: GPL-3.0-or-later
; Inno Setup script for the SteamPoweredPlayer installer. Built by tools\package.ps1:
;   ISCC.exe /DAppVersion=0.1.0 /DSourceDir=<app folder> /DOutDir=<output folder> [/DRepoUrl=https://github.com/owner/repo] installer\SteamPoweredPlayer.iss
; Per-user install (no administrator prompt) into %LOCALAPPDATA%\Programs\SteamPoweredPlayer, a Start menu shortcut and a desktop
; shortcut (ticked by default). The user's settings and data live elsewhere and are never touched by the installer or uninstaller.

#ifndef AppVersion
  #error AppVersion is required: ISCC /DAppVersion=0.1.0
#endif
#ifndef SourceDir
  #error SourceDir is required: the folder that holds the published app
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

#define AppName "SteamPoweredPlayer"
#define AppExe "SteamPoweredPlayer.exe"

[Setup]
; Never change AppId: it is how Windows (and the next installer) recognises this app, for upgrades and uninstall.
AppId={{F6F154D3-8221-47EB-BCA3-D3AFD39D77D8}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=SteamPoweredPlayer authors
#ifdef RepoUrl
; Passed in by tools\package.ps1 from GITHUB_REPOSITORY (the release workflow), so no account name is written into the repository.
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
#endif
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
LicenseFile=..\LICENSE
SetupIconFile=..\src\Assets\SteamPoweredPlayer.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutDir}
OutputBaseFilename={#AppName}-{#AppVersion}-Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; No "unchecked" flag: the desktop shortcut is on by default, the user can untick it.
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
