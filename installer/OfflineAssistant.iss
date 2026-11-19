; Offline Assistant installer (Inno Setup 6.3 or later).
; Built by build/build.ps1, which first fills ..\dist with the app, engine, PDF reader and licences.
; The model isn't included: the app downloads the right one on first run. For an offline install,
; put the model's .gguf file next to Setup.exe and it will be copied instead.

#define AppVersion GetEnv("APP_VERSION")
#if AppVersion == ""
  #define AppVersion "0.0.0-dev"
#endif

[Setup]
AppId={{8F6A3C2E-5B1D-4E7A-9C3F-2D8E1B4A6F90}
AppName=Offline Assistant
AppVersion={#AppVersion}
AppVerName=Offline Assistant {#AppVersion}
AppPublisher=PotatoLLM
AppPublisherURL=https://github.com/concannonl/PotatoLLM
DefaultDirName={localappdata}\OfflineAssistant
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\output
OutputBaseFilename=OfflineAssistant-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\OfflineAssistant.exe
UninstallDisplayName=Offline Assistant
AppMutex=OfflineAssistant_SingleInstance
CloseApplications=yes
SetupLogging=yes

[Messages]
WelcomeLabel2=This will install Offline Assistant, a private AI assistant that runs entirely on this computer.%n%nThe first time it opens, it downloads the assistant itself (about 1-3 GB, depending on this computer). After that it works without the internet.

[InstallDelete]
; Replace the engine and tools cleanly on upgrade; settings are worked out by the app itself now.
Type: filesandordirs; Name: "{app}\engine"
Type: filesandordirs; Name: "{app}\tools"
Type: files; Name: "{app}\config.ini"

[Files]
Source: "..\dist\OfflineAssistant.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\engine\*"; DestDir: "{app}\engine"; Flags: ignoreversion recursesubdirs
Source: "..\dist\tools\*"; DestDir: "{app}\tools"; Flags: ignoreversion recursesubdirs
Source: "..\dist\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion
; Offline install: a model file placed next to Setup.exe is copied instead of downloaded.
Source: "{src}\*.gguf"; DestDir: "{app}\models"; Flags: external skipifsourcedoesntexist ignoreversion

[Icons]
Name: "{userdesktop}\Offline Assistant"; Filename: "{app}\OfflineAssistant.exe"; Comment: "A private AI assistant that runs on this computer"
Name: "{userprograms}\Offline Assistant"; Filename: "{app}\OfflineAssistant.exe"; Comment: "A private AI assistant that runs on this computer"

[Run]
Filename: "{app}\OfflineAssistant.exe"; Description: "Open Offline Assistant now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\models"
Type: filesandordirs; Name: "{app}\memory"
Type: filesandordirs; Name: "{app}\logs"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Saved chats are the user's own: ask before deleting them. }
    if DirExists(ExpandConstant('{app}\chats')) and not UninstallSilent then
      if MsgBox('Do you also want to delete your saved chats?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{app}\chats'), True, True, True);
    RemoveDir(ExpandConstant('{app}'));
  end;
end;
