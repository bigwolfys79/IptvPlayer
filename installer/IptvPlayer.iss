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
#define MyAppVersion "1.16.0"
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

; Базовый файл лицензии по умолчанию (если выбран не русский язык)
LicenseFile=LICENSE_en.txt

; Два языка мастера — под них локализовано само приложение. При нескольких
; языках Inno Setup сам показывает диалог выбора языка при запуске установки
; (ShowLanguageDialog по умолчанию yes); русский первым — выбор по умолчанию.
; Английский — штатный Default.isl компилятора.
[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"; LicenseFile: "LICENSE_ru.txt"
Name: "en"; MessagesFile: "compiler:Default.isl"

; Локализуемые строки, которых нет в стандартных .isl (используются через
; {cm:...}; префикс ru./en. выбирает вариант по языку мастера).
[CustomMessages]
ru.DolbyPlusStatus=Установка декодера Dolby Digital Plus (AC-3 / E-AC-3)...
en.DolbyPlusStatus=Installing Dolby Digital Plus decoder (AC-3 / E-AC-3)...
ru.DolbyAC4Status=Установка декодера Dolby AC-4...
en.DolbyAC4Status=Installing Dolby AC-4 decoder...
ru.UsageTypeTitle=Тип использования
en.UsageTypeTitle=Usage Type
ru.UsageTypeSubtitle=Выберите, как вы будете использовать IptvPlayer
en.UsageTypeSubtitle=Select how you will use IptvPlayer
ru.UsageTypeDesc=Личное использование — бесплатно без ограничений.%nКоммерческое использование — пробный период 30 дней.
en.UsageTypeDesc=Personal use — free without limitations.%nCommercial use — 30-day trial period.
ru.UsageTypePersonal=Личное использование (Personal)
en.UsageTypePersonal=Personal use (Personal)
ru.UsageTypeCommercial=Коммерческое использование (Commercial)
en.UsageTypeCommercial=Commercial use (Commercial)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "LICENSE_ru.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE_en.txt"; DestDir: "{app}"; Flags: ignoreversion
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

; Ассоциации видеофайлов (карточка «Видео»): ProgId + «Открыть с помощью».
; Дефолтную ассоциацию НЕ перехватываем (только OpenWithProgids) — Windows
; предложит IptvPlayer в контекстном меню «Открыть с помощью», не отбирая
; файлы у существующего плеера пользователя. HKLM (машинная область) —
; установщик работает в admin-режиме, и записи в HKCU попадали в реестр
; повышенного пользователя, а не того, кто будет смотреть видео.
[Registry]
Root: HKLM; Subkey: "Software\Classes\IptvPlayer.Video"; ValueType: string; ValueData: "IptvPlayer Video"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\IptvPlayer.Video"; ValueType: string; ValueName: "AppUserModelId"; ValueData: "IptvPlayer.Video"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\IptvPlayer.Video\DefaultIcon"; ValueType: string; ValueData: "{app}\{#MyAppExeName},0"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\IptvPlayer.Video\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "IptvPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "IptvPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: string; ValueName: "IptvPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "IptvPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "IptvPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.flv\OpenWithProgids"; ValueType: string; ValueName: "IptvPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.ts\OpenWithProgids"; ValueType: string; ValueName: "IptvPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.m2ts\OpenWithProgids"; ValueType: string; ValueName: "IptvPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.wmv\OpenWithProgids"; ValueType: string; ValueName: "IptvPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "IptvPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.mpg\OpenWithProgids"; ValueType: string; ValueName: "IptvPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.mpeg\OpenWithProgids"; ValueType: string; ValueName: "IptvPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.3gp\OpenWithProgids"; ValueType: string; ValueName: "IptvPlayer.Video"; ValueData: ""; Flags: uninsdeletevalue

[Code]
var
  UsageTypePage: TInputOptionWizardPage;

function UsageTypeExists(): Boolean;
var
  Existing: String;
begin
  Result := RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\IptvPlayer',
    'UsageType', Existing);
end;

function UsageTypeIndex(): Integer;
var
  Existing: String;
begin
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\IptvPlayer',
      'UsageType', Existing) and
     (CompareText(Existing, 'Commercial') = 0) then
    Result := 1
  else
    Result := 0;
end;

procedure InitializeWizard;
begin
  UsageTypePage := CreateInputOptionPage(wpSelectDir,
    ExpandConstant('{cm:UsageTypeTitle}'),
    ExpandConstant('{cm:UsageTypeSubtitle}'),
    ExpandConstant('{cm:UsageTypeDesc}'),
    True, False);
  UsageTypePage.Add(ExpandConstant('{cm:UsageTypePersonal}'));
  UsageTypePage.Add(ExpandConstant('{cm:UsageTypeCommercial}'));
  // Если пользователь ранее уже выбрал тип использования (HKLM:UsageType),
  // выбор не спрашиваем повторно — при обновлении/переустановке
  // восстанавливаем сохранённое значение.
  if UsageTypeExists() then
    UsageTypePage.Values[UsageTypeIndex()] := True
  else
    UsageTypePage.Values[0] := True; // Personal — по умолчанию
end;

// Страница выбора типа показывается только если значение ещё не задано:
// обновление (в т.ч. тихое /VERYSILENT) не должно переспрашивать и
// случайно «сбрасывать» выбор пользователя на Personal.
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (PageID = UsageTypePage.ID) and UsageTypeExists() then
    Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  UsageType: String;
begin
  if CurStep = ssPostInstall then
  begin
    // Уже выбирал ранее — сохраняем его выбор, что бы там ни стояло.
    if UsageTypeExists() then
      Exit;

    if UsageTypePage.Values[1] then
      UsageType := 'Commercial'
    else
      UsageType := 'Personal';

    RegWriteStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\IptvPlayer', 'UsageType', UsageType);
  end;
end;

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
; Тихое автообновление (/VERYSILENT из UpdateService): запустить приложение
; после установки без вопросов. Интерактивную установку не затрагивает —
; там приложение запускается галочкой postinstall ниже.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; Check: WizardSilent
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
