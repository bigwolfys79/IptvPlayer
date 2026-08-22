; Inno Setup-скрипт инсталятора IptvPlayer.
;
; Сборка инсталятора (из корня проекта):
;   1) dotnet publish IptvPlayer.csproj -c Release -p:PublishProfile=win-x64
;      (publish-папка собирается unpackaged + self-contained, ~260 МБ)
;   2) ISCC installer\IptvPlayer.iss
;      (Inno Setup 7: https://jrsoftware.org/isdl.php — ставится в
;       %LOCALAPPDATA%\Programs\Inno Setup 7)
; Готовый файл: installer\output\IptvPlayer-Setup-<версия>-x64.exe

#define MyAppName "IptvPlayer"
#define MyAppVersion "1.6.6"
#define MyAppExeName "IptvPlayer.exe"
#define MyAppPublisher "IptvPlayer"
; Папка публикации из win-x64.pubxml
#define PublishDir "..\bin\Release\net8.0-windows10.0.26100.0\win-x64\publish"

[Setup]
; Уникальный идентификатор приложения — менять нельзя, иначе Windows сочтёт
; новую версию отдельной программой.
AppId={{B6E9D3C4-52A7-4F18-9C2D-8E4A1F0B7C35}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Иконка инсталятора и приложения — общая с окном приложения.
SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
; Только 64-битные системы: приложение self-contained x64.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=IptvPlayer-Setup-{#MyAppVersion}-x64
; lzma2/max даёт заметно меньший файл на 260 МБ публикации.
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Два языка мастера — под них локализовано само приложение. При нескольких
; языках Inno Setup сам показывает диалог выбора языка при запуске установки
; (ShowLanguageDialog по умолчанию yes); русский первым — выбор по умолчанию.
; Английский — штатный Default.isl компилятора.
[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

; Локализуемые строки, которых нет в стандартных .isl (используются через
; {cm:...}; префикс ru./en. выбирает вариант по языку мастера).
[CustomMessages]
ru.DolbyPlusStatus=Установка декодера Dolby Digital Plus (AC-3 / E-AC-3)...
en.DolbyPlusStatus=Installing Dolby Digital Plus decoder (AC-3 / E-AC-3)...
ru.DolbyAC4Status=Установка декодера Dolby AC-4...
en.DolbyAC4Status=Installing Dolby AC-4 decoder...

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Декодеры Dolby (AC-3/E-AC-3/AC-4) — Microsoft убрала их из состава Windows 11
; 24H2+, из-за чего IPTV-потоки с дорожкой Dolby Digital не играют. Пакеты
; официально подписаны Dolby/Microsoft (те же, что в Store для OEM); ставятся
; тихо в [Run], только если ещё не установлены.
Source: "dolby\DolbyDigitalPlusDecoderOEM_1.1.285.0.AppxBundle"; DestDir: "{app}\dolby"; Flags: ignoreversion
Source: "dolby\DolbyAC4DecoderOEM_1.0.0.0.AppxBundle"; DestDir: "{app}\dolby"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Code]
// True, если декодер Dolby Digital Plus ещё не установлен у пользователя.
// Вызывается из Check у [Run]-записей ниже. 0 = установлен, 1 = нет,
// ошибка запуска PowerShell = считаем "не установлен" (лучше попытаться).
function DolbyDecoderMissing(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -Command "exit [int](-not [bool](Get-AppxPackage -Name ''DolbyLaboratories.DolbyDigitalPlusDecoderOEM''))"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := (ResultCode <> 0);
end;

[Run]
; Установка декодеров от имени исходного (не повышенного) пользователя —
; Appx-пакеты ставятся в профиль конкретного пользователя.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Add-AppxPackage -Path ''{app}\dolby\DolbyDigitalPlusDecoderOEM_1.1.285.0.AppxBundle''"""; \
  StatusMsg: "{cm:DolbyPlusStatus}"; \
  Flags: runasoriginaluser; Check: DolbyDecoderMissing
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Add-AppxPackage -Path ''{app}\dolby\DolbyAC4DecoderOEM_1.0.0.0.AppxBundle''"""; \
  StatusMsg: "{cm:DolbyAC4Status}"; \
  Flags: runasoriginaluser; Check: DolbyDecoderMissing
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
