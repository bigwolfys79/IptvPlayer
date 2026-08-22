using System.Collections.Generic;
using System.Linq;

namespace IptvPlayer.Models;

/// <summary>
/// Всё, что должно переживать перезапуск приложения: список источников EPG,
/// плейлист по умолчанию и т.п. Сериализуется в JSON и хранится в
/// ApplicationData.LocalFolder через SettingsService.
/// </summary>
public class AppSettings
{
    public List<EPGSource> EpgSources { get; set; } = new()
    {
        // Полный фид epg.one (gzip XMLTV, ~45 МБ, ~420 тыс. передач) —
        // единственный источник по умолчанию, сразу включён.
        new EPGSource { Url = "http://epg.one/epg.xml.gz", IsEnabled = true },
    };

    /// <summary>
    /// УСТАРЕЛО: URL единственного плейлиста до появления поддержки нескольких
    /// плейлистов. Больше не читается для загрузки — при первой загрузке
    /// настроек мигрируется в Playlists (см. MainPage.InitializeAsync) и
    /// дальше живёт только список. Поле оставлено, чтобы старые файлы
    /// настроек распознавались.
    /// </summary>
    public string? PlaylistUrl { get; set; }

    /// <summary>
    /// Источники плейлистов пользователя. Каналы в список загружаются только
    /// из активного (ActivePlaylistId); кэш — свой на каждый (PlaylistCacheService).
    /// </summary>
    public List<PlaylistSource> Playlists { get; set; } = new();

    /// <summary>
    /// Id плейлиста из Playlists, каналы которого показываются в списке.
    /// Переключается комбобоксом в панели каналов и в диалоге «Плейлист».
    /// </summary>
    public int ActivePlaylistId { get; set; }

    /// <summary>
    /// Как часто при запуске перекачивать плейлист: 1/3/7 дней.
    /// 0 — никогда автоматически (плейлист скачивается один раз при добавлении
    /// источника в настройках и дальше живёт в локальном кэше).
    /// По умолчанию — раз в день.
    /// </summary>
    public int PlaylistRefreshDays { get; set; } = 1;

    /// <summary>
    /// Как часто при запуске перекачивать XMLTV-источники EPG: 1/3/7 дней.
    /// 0 — только вручную кнопкой "Обновить EPG" (она чистит кэш принудительно).
    /// По умолчанию — раз в день.
    /// </summary>
    public int EpgRefreshDays { get; set; } = 1;

    /// <summary>
    /// Последняя выбранная пользователем громкость (0..1). Раньше громкость
    /// жила только в памяти (MainPage._lastUserVolume) и после перезапуска
    /// приложения сбрасывалась к максимуму. Сохранение — с дебаунсом при
    /// отпускании слайдера и при закрытии окна.
    /// </summary>
    public double Volume { get; set; } = 1.0;

    /// <summary>
    /// Режим декодирования видео: "Hardware" (GPU с автоматическим откатом
    /// на процессор — VideoDecoderMode.Automatic) или "Software" (принудительно
    /// процессор — ForceFFmpegSoftwareDecoder). По умолчанию — Software: замер
    /// показал 12-кратный запас CPU даже на самом тяжёлом канале плейлиста,
    /// а программный путь ведёт себя предсказуемо. Применяется при следующем
    /// переключении канала (плеер пересоздаётся на каждый канал).
    /// </summary>
    public string DecoderMode { get; set; } = "Software";

    /// <summary>
    /// Предпочтительное качество видео: 0 = авто (максимальное), 480, 720, 1080, 2160.
    /// Применяется при выборе потока из доступных вариантов (если провайдер
    /// отдаёт несколько качеств в одном плейлисте). По умолчанию — авто.
    /// </summary>
    public int PreferredQuality { get; set; } = 0;

    /// <summary>
    /// Нормализация громкости: "Off" (без обработки), "Dynamic" (фильтр
    /// dynaudnorm — динамически усиливает тихие каналы) или "Loudness"
    /// (фильтр loudnorm — все каналы к единой громкости EBU R128).
    /// Часть каналов кодируется значительно тише остальных, а усилить
    /// звук выше 100% MediaPlayer не даёт — без нормализации такие
    /// каналы приходится слушать почти шёпотом. По умолчанию — Dynamic.
    /// </summary>
    public string AudioNormalization { get; set; } = "Dynamic";

    /// <summary>
    /// За сколько минут до начала передачи показывать тост-напоминание.
    /// </summary>
    public int ReminderMinutes { get; set; } = 5;

    /// <summary>
    /// Режим отображения видео: "Uniform" (вписать, letterbox — по умолчанию),
    /// "Fill" (растянуть, пропорции не сохраняются) или "UniformToFill"
    /// (обрезать: масштаб с сохранением пропорций, выступающее за края
    /// отсекается). Для контента не в пропорции окна (4:3 на 16:9).
    /// Переключается кнопкой в панелях управления и клавишей V.
    /// </summary>
    public string VideoStretch { get; set; } = "Uniform";

    /// <summary>
    /// Активные напоминания о передачах (тосты Windows). Очищаются по мере
    /// устаревания (таймер в MainPage удаляет начавшиеся передачи).
    /// </summary>
    public List<ProgramReminder> ProgramReminders { get; set; } = new();

    /// <summary>
    /// Избранные каналы — по имени (Id канала переназначается при каждой
    /// загрузке плейлиста и нестабилен между запусками, имена стабильны).
    /// </summary>
    public List<string> FavoriteChannels { get; set; } = new();

    /// <summary>
    /// Последний просмотренный канал (имя) — для автопродолжения при
    /// следующем запуске приложения.
    /// </summary>
    public string? LastWatchedChannel { get; set; }

    /// <summary>
    /// Тема интерфейса: "Light" / "Dark" / "Default" (системная).
    /// Применяется через FrameworkElement.RequestedTheme корневого элемента
    /// окна — на лету, без перезапуска.
    /// </summary>
    public string Theme { get; set; } = "Default";

    /// <summary>
    /// Язык интерфейса: "ru" / "en". Основные тексты интерфейса переводятся
    /// локализатором (Services/Localizer.cs) на лету; лог остаётся русским.
    /// </summary>
    public string Language { get; set; } = "ru";

    /// <summary>
    /// Упреждающая буферизация видео в секундах (5..60). Больше — плавнее на
    /// нестабильной сети, но дальше от живого эфира. Применяется при
    /// следующем переключении канала (плеер пересоздаётся).
    /// </summary>
    public int ReadAheadSeconds { get; set; } = 15;

    /// <summary>Сохранённое состояние окна: позиция, размер, максимизация.</summary>
    public WindowPlacement? WindowPlacement { get; set; }

    /// <summary>
    /// Ширина панели списка каналов, выбранная перетаскиванием разделителя.
    /// </summary>
    public double ChannelListWidth { get; set; }

    /// <summary>
    /// Запланированные записи будущих передач (кнопка записи в EPG).
    /// Запускаются, пока приложение запущено.
    /// </summary>
    public List<ScheduledRecording> ScheduledRecordings { get; set; } = new();

    /// <summary>
    /// Папка для записей (ffmpeg). null/пусто — «Видео\IptvPlayer».
    /// </summary>
    public string? RecordingsFolder { get; set; }

    /// <summary>
    /// Родительский контроль: каналы групп из ParentalControlBlockedGroups
    /// скрыты, пока контроль включён и не разблокирован временно. PIN —
    /// PBKDF2-хэш (см. ParentalControlService); null = без PIN (просто скрыть).
    /// </summary>
    /// <summary>
    /// URL проверки обновлений для кнопки в «О программе»: ожидается JSON
    /// {"version":"1.7.0","url":"https://.../setup.exe"}. null — проверка
    /// отключена (не хардкодим чужой сервер).
    /// </summary>
    public string? UpdateCheckUrl { get; set; }

    /// <summary>
    /// Сворачивать окно в трей вместо выхода при закрытии (крестик).
    /// Полный выход — через меню иконки в трее.
    /// </summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// Записи, прерванные закрытием приложения: при следующем запуске
    /// предлагается продолжить запись оставшейся части (см. InterruptedRecording).
    /// </summary>
    public List<InterruptedRecording> InterruptedRecordings { get; set; } = new();

    public bool ParentalControlEnabled { get; set; }
    public string? ParentalControlPinHash { get; set; }
    public List<string> ParentalControlBlockedGroups { get; set; } = new();
    public DateTime? ParentalControlUnlockedUntilUtc { get; set; }

    /// <summary>
    /// Показывать оверлей статистики потока (Ctrl+J): кодеки, разрешение,
    /// битрейты, фактический декодер, буфер и простои. Состояние переживает
    /// перезапуск — удобно для диагностики («включи и пришли скриншот»).
    /// Переключается и тумблером в настройках.
    /// </summary>
    public bool StatsOverlayVisible { get; set; }

    /// <summary>
    /// Вести ли файловый лог (Serilog: %LocalAppData%\IptvPlayer\logs,
    /// ежедневный роллинг). Переключается в настройках на лету через
    /// App.SetFileLoggingEnabled; вывод в Debug под отладчиком остаётся
    /// всегда. По умолчанию включён.
    /// </summary>
    public bool FileLoggingEnabled { get; set; } = true;

    /// <summary>
    /// Таймер сна: количество минут до автоматической остановки воспроизведения.
    /// 0 — таймер выключен (по умолчанию).
    /// </summary>
    public int SleepTimerMinutes { get; set; }

    /// <summary>
    /// Что делает таймер сна по истечении: "Stop" — остановить
    /// воспроизведение (по умолчанию), "Exit" — закрыть приложение,
    /// "Shutdown" — выключить компьютер (приложение закрывается, затем
    /// запускается shutdown /s /t 0). Выбирается комбобоксом в настройках.
    /// </summary>
    public string SleepTimerAction { get; set; } = "Stop";

    /// <summary>
    /// Источники EPG активного плейлиста: собственный список плейлиста, если
    /// он задан; иначе — глобальный EpgSources (плейлистов ещё нет, у плейлиста
    /// источники не настраивались, или все удалены — разумный фолбэк, чтобы
    /// EPG не пропадал молча).
    /// </summary>
    public List<EPGSource> GetActiveEpgSources()
    {
        var playlist = Playlists.FirstOrDefault(p => p.Id == ActivePlaylistId);
        return playlist?.EpgSources.Count > 0 ? playlist.EpgSources : EpgSources;
    }
}

/// <summary>
/// Один источник плейлиста M3U/M3U8 в списке пользователя. Id стабилен между
/// запусками (следующий за максимальным существующим), от него зависит имя
/// файла кэша каналов этого плейлиста.
/// </summary>
public class PlaylistSource
{
    public int Id { get; set; }

    /// <summary>Отображаемое имя; при добавлении без имени — хост URL.</summary>
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Последний смотренный канал ЭТОГО плейлиста (по имени) — автопродолжение
    /// восстанавливается при возврате на плейлист. Пер-плейлист, а не глобальный,
    /// чтобы переключение провайдеров не переносило канал из одного набора в другой.
    /// </summary>
    public string? LastWatchedChannel { get; set; }

    /// <summary>
    /// Источники EPG (XMLTV) этого плейлиста — у каждого свой набор (1-2-3 URL).
    /// При переключении плейлиста EPG перечитывается из его источников.
    /// Пустой список → используется глобальный AppSettings.EpgSources.
    /// </summary>
    public List<EPGSource> EpgSources { get; set; } = new();
}
