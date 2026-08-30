# Принцип работы IptvPlayer

Техническое описание того, как приложение устроено изнутри: как работает воспроизведение, отрисовка, EPG, архив и видеотека. Имена классов/методов — реальные, из кода. Пользовательский список возможностей — в README.md; здесь только механизмы и неочевидные решения.

## Общая схема

```
PlaylistSource (m3u | portal)
        │  M3UParserService / VideoPortalService
        ▼
ChannelRepository ──► MainPage / ViewModels ──► StreamService ──► FFmpegInteropX/FFmpeg ──► MediaPlayerElement
        │                    │                                                        ▲
        │                    ├── EpgViewModel ◄── EPGService ◄── XmlTvService            │
        │                    │        (EPG, напоминания)          (XMLTV + кэш)          │
        └── PlaylistDatabaseService (SQLite-кэш каналов/каталога)                                   │
                                                                                          │
MediaPlayer.StartPlaybackAsync(channel, url, ...): эфир / архив (timeshift) / VOD портала ┘
```

Ключевые классы:
- `HubPage` — экран запуска с карточками «Плейлисты», «Портал», «Настройки».
- `MainPage` (+ partial-файлы `MainPage.FullScreen/Hotkeys/Navigation/Overlays/Portal/Seek/Settings/StatsOverlay/VideoControls.cs`) — весь UI и оверлеи.
- `MainPageViewModel` (+ partial-файлы `MainPageViewModel.PortalFilters/Recording/VodResume.cs`) — логика списка каналов, фильтрация, запись, VOD resume.
- `EpgViewModel` — EPG: загрузка, ленивая загрузка по каналу, текущая передача.
- `PlayerViewModel` — управление плеером (FFmpegInteropX), архив, VOD.
- `ChannelViewModel` — модель канала с nullable-свойствами CurrentProgramTitle/Description.
- `Services/StreamService` — единая точка создания плеера (FFmpegInteropX) + диагностика.
- `Services/VideoPortalService` — клиент видео-портала (каталог + потоки).
- `Services/EPGService` + `XmlTvService` — загрузка и сопоставление программы передач.

---

## 1. Запуск приложения

Первым делом `App.OnLaunched` регистрирует единственный экземпляр (`AppInstance.FindOrRegisterForKey`): повторный запуск не создаёт второй процесс, а переадресует активацию работающему — тот поднимает окно из трея (`ShowFromTray`). Так исключена конкурентная запись `settings.json` параллельными экземплярами.

Затем проверяется лицензия (`LicenseService.CheckLicense`): персональный режим — без ограничений; коммерческий — 30-дневный триал (DPAPI-токен в HKLM) или купленная офлайн-лицензия. Лицензия — строка `IPL1.{payload}.{подпись RSA-2048}`, подпись проверяется зашитым публичным ключом, payload привязан к HWID (volume serial + MachineGuid); хранится в HKCU и перепроверяется при каждом запуске. Анти-откат часов: `LastSeenUtc` (DPAPI + HKCU) — откат системного времени не продлевает триал/лицензию. При истёкшем триале показывается диалог с HWID, полем ключа и импортом `.lic` — успешная активация продолжает запуск.

`App` → `MainWindow` → `HubPage` (если `ShowHubOnStartup`) или `MainPage`:

**Hub Page**: экран запуска с тёмным градиентом (`#0D1117→#161B22`), приветствие по времени суток с DropShadow-свечением, анимированная линия под заголовком, 3 карточки (Плейлисты/Портал/Настройки) с анимацией spin-in. Кастомные flyout-меню с позиционированием по границам экрана.

**MainPage** → `InitializeAsync()` (страница показывается сразу, ничего не блокирует):

1. Загружаются настройки (`SettingsService`, `%LocalAppData%\IptvPlayer\settings.json`); из них восстанавливается громкость.
2. Каналы активного плейлиста (`PlaylistSource.Type`: `m3u` — парсер, `portal` — каталог портала, оба — через кэш `PlaylistDatabaseService` (SQLite)) кладутся в `ChannelRepository`, получают последовательные `Id`, заполняют `ViewModel.Channels`.
3. `SelectedChannel` назначается сразу, `Task.Yield()` даёт UI отрисовать список; дальше фоном грузится EPG и срабатывает автопродолжение последнего канала.
4. После загрузки EPG — `LoadEPGForChannelAsync` для выбранного канала (полный список передач в панели EPG).

## 2. Отрисовка и ввод

Слои правой области задаются `Canvas.ZIndex`: видео (1) → шапка/панель управления (2) → EPG-оверлей (3).

**Список каналов** (`ChannelItemTemplate`, общий для окна и полноэкранного оверлея): логотип (28×28, `StringToImageSourceConverter` с системным кэшем) → точка-индикатор архива (`HasArchive`) → название; всплывающая подсказка с описанием (у элементов портала).

**Полноэкранный режим** (`MainPage.FullScreen.cs` → `MainWindow.SetOsFullScreen`): OS-presenter `AppWindowPresenterKind.FullScreen` растягивает окно на весь монитор (проверено замерами — клиентская область ровно в размер экрана); контент-обязка делается вручную: скрытие TitleBar-строки, сворачивание колонок списка каналов, обнуление декоративного `Padding` контейнера видео (`VideoAreaBorder`, в оконном режиме 12 px — без обнуления давал полосы по краям экрана), пересборка компоновки видео (`ForceVideoRelayout` — DComp-остров после смены presenter'а рисует по старым координатам).

**Оверлеи**: верхняя шапка (название канала, текущая передача + описание, прогресс, индикаторы архива/таймера сна) и нижняя панель управления (запись, пауза, перемотка, качество VOD, громкость, EPG, fullscreen) — в оконном и полноэкранном вариантах; появляются по движению мыши, скрываются через 3 с (`_overlayHideTimer`); в fullscreen вместе с ними прячется курсор (`CursorHider`, невидимый `CursorGrid`).

**Клавиатура** (`OnPagePreviewKeyDown`, MainPage): обработчик повешен на **корневой элемент XamlRoot** — туннелирующее событие идёт от корня к сфокусированному элементу, а окно хостит страницу внутри Grid+Frame, поэтому подписка на самой странице пропускала клавиши, пока фокуса внутри страницы нет. Корень ловит всё; открытые ContentDialog отсекаются проверкой фокуса. Полный список горячих клавиш — в README; здесь важно только это правило маршрутизации и то, что стрелки/PgUp/PgDn не перехватываются при фокусе на навигационных элементах (`IsNavigationControlFocused`, обход `VisualTreeHelper`).

## 3. Плейлисты и видеотека

**Источники** — список `PlaylistSource` в настройках (`Dialogs/PlaylistSettingsDialog`), переключение `MainPage.SwitchPlaylistAsync`: остановка плеера, перезагрузка каналов, избранное/группы/фильтр, EPG нового плейлиста. У каждого источника свой набор источников EPG и своё автопродолжение.

**Hub Page**: экран запуска с 3 карточками. Плейлисты — flyout «Загрузить»/«Последний». Портал — flyout «Загрузить»/«Недосмотренные». Настройки — flyout с безопасными настройками (Плейлисты/Интерфейс/Воспроизведение). Навигация через `Frame.Navigate(typeof(MainPage), tuple)`.

**M3U** (`M3UParserService`): классический разбор `#EXTINF` (tvg-logo/tvg-id/tvg-rec).

**Портал** (`Services/VideoPortalService`, источники `Type == "portal"`):
- Протокол — POST-запросы `{базовыйURL}/{команда}.json` с JSON-телом; авторизация — поле `"key"` в теле каждого запроса; команда `flicks` отдаёт страницы элементов (лимит сервера — 300, маркер следующей страницы `{type:"next"}`), `flick` — поток и варианты качества (480/720/1080/auto отдельными ссылками).
- Клиент «прозрачный»: request-объекты из ответов передаются серверу как есть, все поля optional, неизвестные игнорируются — новые команды протокола не требуют правок клиента. Каждый запрос/ответ логируется (обрезка 8 КБ) — протокол докручивается по логу.
- Инвалидация кэша ключа портала: SHA-256 хеш ключа хранится в SQLite; при смене ключа кэш каналов перескачивается.
- Каталог кэшируется как плейлист (`PlaylistDatabaseService`), категория = группа, у элементов хранится request-объект (`PortalRequest`) вместо ссылки — ссылки короткоживущие.
- Сезоны — отдельные карточки каталога: `ParsePortalSeasonName` выделяет базовое имя и номер(ы) сезона из названия, группы строятся лениво и инвалидируются при смене каналов (`GetPortalSeasonSiblings`). Эпизоды сериала — плоский список из flick («Серия N»), живёт в `PlayerViewModel.VodEpisodes` и переживает переключения качества; смена серии — `PlayVodEpisodeAsync` без запроса к порталу, смена сезона — полный `PlayChannelAsync(interactive:false)` соседней карточки.
- Воспроизведение (`MainPageViewModel.PlayChannelAsync`): по клику выполняется `flick` (лениво, без кэширования), старт в режиме VOD (`PlayerViewModel.IsVodPlaying`) — пауза без рестарта потока, перемотка на лету через `PlaybackSession.Position`, выбор качества — рестарт с новой ссылкой и переносом позиции.
- Возобновление просмотра VOD: позиция сохраняется в SQLite (`VodResumeStore`) с прореживанием (макс. 200 записей). При входе в VOD — диалог «продолжить с сохранённого места?».

## 4. EPG (XMLTV)

`EPGService` загружает источники активного плейлиста (свой список, фолбэк — глобальные), сливает их (`EpgSourceMerger`: первый источник в списке выигрывает при пересечении передач по времени) и сопоставляет с каналами: по `tvg-id` из плейлиста → по таблице «имя → tvg-id» (`Assets/epg-name-map.json`) → по нормализованному имени (`EpgNameNormalizer`, таймшифт-суффиксы учитываются).

`XmlTvService` парсит XMLTV окном ±3 дня (программы вне окна не разбираются вовсе — главная экономия на фидах в сотни тысяч передач). **Важно**: обход детей `programme`/`channel` идёт по основному ридеру с выходом ровно на закрывающий тег — `ReadElementContentAsString()` на ридере из `ReadSubtree()` в .NET «съедает» последующих соседей, из-за чего долгое время читались только title (desc/category/иконки терялись). Кэш разобранного фида — MemoryPack+Brotli (`EpgCacheStore`, версия формата инвалидируется при изменении сериализуемых полей).

**Ленивая загрузка**: при старте `RecalculateCurrentProgramsAsync` загружает только текущую передачу для каждого канала (`GetCurrentProgramAsync`) — экономия ~20MB. Полный список передач (`EPGEntries`) загружается только при клике на канал (`LoadEPGForChannelAsync`). Панель EPG при старте показывает список передач выбранного канала (для него вызывается `LoadEPGForChannelAsync` после `LoadEPGAsync`).

Текущая передача канала (`CurrentProgramTitle/CurrentProgramDescription`) пересчитывается таймером (30 с); клик по начавшейся передаче запускает архив.

## 5. Архив передач (timeshift)

Признак — `tvg-rec`/`catchup-days` плейлиста (`ChannelViewModel.CatchupDays`). Запуск: `ArchiveUrlBuilder.BuildUrl` добавляет к live-URL параметры `utc`/`lutc` — провайдер отдаёт сдвинутый HLS-плейлист.

HLS-timeshift не ищется на лету, поэтому перемотка — перезапуск потока с новой точкой старта; позиция считается в `PlayerViewModel` по стенным часам от старта показа минус суммарное время пауз, не дальше живого эфира. (VOD портала, в отличие от архива, перематывается движком на лету — см. §3.) Пользовательская механика полосы описана в README.

## 6. Воспроизведение

`StreamService.CreatePlayerAsync` — единая точка создания плеера (на каждый канал новый `MediaPlayer`):

1. **FFmpegInteropX + FFmpeg** — демуксинг и декодирование (системный HLS-стек Windows не декодирует HEVC в MPEG-TS, а AC-3 с 24H2 удалён из системы). Конфигурация: режим декодера из настроек (`VideoDecoderMode.Automatic` = GPU с откатом / `ForceFFmpegSoftwareDecoder` по умолчанию), `DownmixAudioStreamsToStereo = false` (многоканальный звук сводит аудиодвижок Windows — downmix FFmpeg тише), упреждающая буферизация 15 с / 32 МБ.
2. **Время жизни источника**: `FFmpegMediaSource` привязан к плееру через `ConditionalWeakTable` — без этого GC собирал источник посреди воспроизведения (рывки → пропажа звука → крах 0xC00D36B6).
3. **Откат**: если FFmpeg не смог открыть URL — системный `MediaSource.CreateFromUri`.
4. **Диагностика**: снимок параметров потока кладётся в `CurrentDiagnostics`, оверлей статистики (Ctrl+J) добавляет живые метрики секундным тиком. `StreamService.DiagnoseStreamUrl` проверяет URL при ошибке (HTTP-статус, таймаут, доступность). Для измерения реальной скорости потока — `LocalStreamProxy`: FFmpeg качает через локальный TCP-прокси на 127.0.0.1 (HLS-плейлисты перезаписываются на прокси-маршруты), прокси считает байты; включается галкой в настройках воспроизведения, по умолчанию выключен.
5. **Нормализация громкости** — аудиофильтр FFmpeg по настройке: `Dynamic` (dynaudnorm, усиливает тихие каналы, по умолчанию) или `Loudness` (loudnorm, единая громкость EBU R128); тяжёлые фильтры могут влиять на плавность — режим пишется в лог при старте потока.
6. Ошибки плеера логируются с кодами (`MediaPlayer.MediaFailed`); `OnMediaFailed` — async с диагностикой.

**Пауза** — только архив и VOD портала (пробел, `ToggleArchivePause`): живой эфир паузить нельзя, это осознанное ограничение. Для VOD тот же переключатель работает без архивных часов.

**Закрытие приложения**: подписка на `MainWindow.Closed` останавливает/освобождает плеер и записи и вызывает `Environment.Exit(0)` — иначе медиа-конвейер держал процесс живым несколько секунд.

## 7. Данные на диске

| Что | Где |
|---|---|
| Настройки (источники, порталы, периодичности, громкость, декодер, избранное) | `%LocalAppData%\IptvPlayer\settings.json` (запись атомарная, через `.tmp`; прошлые версии — `settings.json.prev`, битые — `*.corrupt-*`) |
| Кэш каналов/каталога (SQLite, общий файл на все плейлисты; старые JSON-кэши `playlist_cache_{id}.json` мигрируются в него разово) | `%LocalAppData%\IptvPlayer\iptvplayer_cache.db` |
| Кэш разобранных XMLTV-источников (MemoryPack+Brotli) | `%LocalAppData%\IptvPlayer\cache\` |
| Записи (ffmpeg, MPEG-TS без перекодирования) | «Видео\IptvPlayer» или настроенная папка |
| Лог (Serilog, ежедневный роллинг, 14 дней) | `%LocalAppData%\IptvPlayer\logs\` |
| Позиции просмотра VOD (SQLite) | `%LocalAppData%\IptvPlayer\` |

В MSIX-режиме (Debug) пути `%LocalAppData%` виртуализуются в пакет; в unpackaged-режиме (Release/Inno) используются напрямую — код одинаково работает в обоих.

## 8. Производительность больших каталогов

Каталог портала — 20k+ элементов; ключевые решения:
- `FilterChannels` заменяет `DisplayedChannels` целиком (одна смена ItemsSource вместо тысяч CollectionChanged). Выделение в списках — **OneWay + SelectionChanged**: TwoWay-привязка затирала `SelectedChannel` в null при очистке ItemsSource скрытого вида (видео привязано к `SelectedChannel.IsPlaying` и пропадало); после пересборки выделение в контролы возвращает MainPage по событию FilterChanged.
- Сгруппированный источник полноэкранного оверлея (`RefreshOverlayChannelGroups`) строится только когда оверлей виден; при входе в fullscreen — явно.
- Скрытый вид списка/сеток постеров отсоединяется от данных (ItemsSource = null).
- Старт фильмов: мгновенно по ссылке каталога, варианты качества догружаются фоновым flick и подкладываются (`PlayerViewModel.SetVodVariants`); сериалы ждут flick (нужен список серий).
- Буфер: эфир — `ReadAheadSeconds` (15 с / 32+ МБ), VOD — отдельный `VodReadAheadSeconds` (4 с / 8+ МБ): большой буфер на медленном CDN VOD держал старт потока несколько секунд.
- Оптимизация памяти: `EPGEntry.Description` и `ChannelViewModel.CurrentProgram*` — nullable (~46 MB экономии при 2000+ каналах + 400k передач).

## 9. Обновление приложения

Полуавтоматическое обновление (`Services/UpdateService` + `MainPage.RunAutoUpdateCheckAsync`): фоновая проверка через 2 мин после старта (не чаще раза в сутки — `AppSettings.LastUpdateCheckUtc`), разбор GitHub API тот же, что у ручной кнопки в «О программе». Скачанный установщик проверяется по SHA256 (`assets[].digest`, если источник отдал). Согласие пользователя — ContentDialog; установка — `setup.exe /VERYSILENT /NORESTART /SUPPRESSMSGBOXES` от имени оболочки (UAC: Program Files), приложение закрывается штатно, а после тихой установки запускается снова (отдельная запись `[Run]` с `Check: WizardSilent` в .iss — интерактивную установку не затрагивает). Пока идут записи, установка откладывается до события `RecordingsChanged`. Любая ошибка — тихая: старая версия продолжает работать (Inno ставит поверх).

## 10. Логирование и DI

**Serilog.** Статический логгер настраивается первым делом в конструкторе `App` (до `InitializeComponent` — глобальные обработчики исключений уже должны писать в лог): Debug-вывод (всегда) + файловый sink с ежедневным роллингом. Классы получают `ILogger<T>` конструктором (источник в логе = имя класса); файловый лог выключается тумблером в настройках на лету через `LoggingLevelSwitch`.

**DI.** `App` — composition root: `ServiceCollection` собирается в конструкторе, провайдер доступен как `App.Services`. Все сервисы и ViewModel'ы — singletons (одна сессия, одно окно). Страницы резолвят зависимости через `App.Services.GetRequiredService` в конструкторе — WinUI не даёт внедрять их в конструкторы XAML-элементов.

**MVVM-договорённости.** Свойства — ручные `SetProperty` вместо `[ObservableProperty]` (генератор не создаёт WinRT-проекторов — MVVMTK0045, важно для AOT/ABI); действия — `[RelayCommand]`; code-behind MainPage разбит на partial-файлы по зонам.

## 11. Разбиение на partial-файлы

**MainPage** (3133 → 1329 строк):

| Файл | Строк | Содержимое |
|---|---|---|
| `MainPage.xaml.cs` | 1329 | Поля, конструктор, InitializeAsync, OnNavigatedTo, Overlays, ToggleFullScreen |
| `MainPage.Portal.cs` | 264 | Portal API методы |
| `MainPage.Settings.cs` | 98 | Диалоги настроек |
| `MainPage.Navigation.cs` | 375 | Переключение плейлистов, навигация |
| `MainPage.VideoControls.cs` | 442 | Volume/Mute, Stretch, Sleep timer, Mini player, Favorite/Reminder/Record |
| `MainPage.Seek.cs` | 584 | VOD seek/quality/season/episode, Archive seek, EPG, Fullscreen, PIN |
| `MainPage.FullScreen.cs` | 277 | Полноэкранный режим |
| `MainPage.Hotkeys.cs` | 388 | Горячие клавиши (описания — в справке F1, см. HOTKEYS-SYNC) |
| `MainPage.Overlays.cs` | 450 | Оверлеи |
| `MainPage.StatsOverlay.cs` | 213 | Статистика |

**HubPage** (843 строки):

| Файл | Строк | Содержимое |
|---|---|---|
| `HubPage.xaml.cs` | 843 | Экран запуска: приветствие, карточки, кастомные flyout-меню, справка горячих клавиш (F1) |

**MainPageViewModel** (1870 → 941 строк):

| Файл | Строк | Содержимое |
|---|---|---|
| `MainPageViewModel.cs` | 941 | Инициализация, фильтры, категории, EPG, SaveSettings |
| `MainPageViewModel.PortalFilters.cs` | 275 | Portal API + фильтры портала |
| `MainPageViewModel.Recording.cs` | 284 | Запись, напоминания, избранное, архив |
| `MainPageViewModel.VodResume.cs` | 247 | VOD resume, PlayChannelAsync (interactive) |
