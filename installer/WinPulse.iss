; Установщик WinPulse (Inno Setup 6).
; Ставится для текущего пользователя в %LOCALAPPDATA%\Programs\WinPulse — без прав администратора,
; поэтому автообновление тоже работает без запроса прав.
; Сборка: ISCC.exe /DAppVersion=0.9.0 /DNumericVersion=0.9.0 /DSourceDir=..\dist installer\WinPulse.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist"
#endif

[Setup]
AppId={{9C1F3A52-6B7E-4D2B-9E61-2F4C8A7D5B10}
AppName=WinPulse
AppVersion={#AppVersion}
AppVerName=WinPulse {#AppVersion}
AppPublisher=WinPulse
AppPublisherURL=https://github.com/Satith666/prostor/tree/winpulse
AppSupportURL=https://github.com/Satith666/prostor/issues
VersionInfoVersion={#NumericVersion}
DefaultDirName={autopf}\WinPulse
DefaultGroupName=WinPulse
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=..\release
OutputBaseFilename=WinPulse-Setup-{#AppVersion}
SetupIconFile=..\src\WinPulse.App\Assets\WinPulse.ico
UninstallDisplayIcon={app}\WinPulse.exe
UninstallDisplayName=WinPulse
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809 и новее
MinVersion=10.0.17763
CloseApplications=force
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.UninstallWarn=Настройки Windows, которые WinPulse менял по вашей команде, останутся как есть. Если хотите вернуть их, сначала нажмите «Вернуть всё как было» в разделах WinPulse.%n%nРезервные копии и история остаются в %LOCALAPPDATA%\WinPulse — их можно удалить вручную.%n%nУдалить WinPulse?
en.UninstallWarn=Windows settings that WinPulse changed at your request will stay as they are. To restore them, first press "Revert everything" in WinPulse.%n%nBackups and history stay in %LOCALAPPDATA%\WinPulse — you can delete them by hand.%n%nUninstall WinPulse?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Старые версии PresentMon, чтобы рядом всегда лежала одна, свежая.
Type: files; Name: "{app}\PresentMon-*.exe"

[Files]
Source: "{#SourceDir}\WinPulse.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\NvAPIWrapper.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\PresentMon-*.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\docs\THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\WinPulse"; Filename: "{app}\WinPulse.exe"
Name: "{autodesktop}\WinPulse"; Filename: "{app}\WinPulse.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WinPulse.exe"; Description: "{cm:LaunchProgram,WinPulse}"; Flags: nowait postinstall skipifsilent
; Автообновление ставит тихо (/SILENT) — после установки WinPulse запускается снова.
Filename: "{app}\WinPulse.exe"; Flags: nowait; Check: WizardSilent

[Code]
function InitializeUninstall(): Boolean;
begin
  Result := True;
  if not UninstallSilent then
    Result := MsgBox(CustomMessage('UninstallWarn'), mbConfirmation, MB_YESNO) = IDYES;
end;
