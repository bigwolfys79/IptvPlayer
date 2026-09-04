using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;
using Windows.Media.Playback;

namespace IptvPlayer.ViewModels;

/// <summary>
/// Управление воспроизведением (этап 2 MVVM: вынесено из code-behind MainPage).
/// Создаёт/останавливает плееры через StreamService, хранит состояние архива
/// и громкость. Представление реагирует на события и свойства:
///  - PlayerChanged           → MediaPlayerElement.SetMediaPlayer(...)
///  - ArchiveStateChanged     → баннер/кнопки паузы и «В эфир»
///  - IsBuffering/StreamError → прогресс-бар и текст ошибки.
/// </summary>
public partial class PlayerViewModel : ObservableObject
{
    private readonly IStreamService _streamService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<PlayerViewModel> _logger;

    // Свойства ниже — не [ObservableProperty], а ручные: сгенерированные
    // генератором свойства не создают WinRT-проекторов, и в WinUI/AOT-сценариях
    // (маршализация через ABI) это предупреждение MVVMTK0045. Ручное
    // свойство поверх поля даёт ту же семантику INotifyPropertyChanged.
    private string _streamId = string.Empty;
    private string? _lastStreamUrl;

    public string StreamId
    {
        get => _streamId;
        set => SetProperty(ref _streamId, value);
    }

    private string _currentPosition = "00:00:00";

    public string CurrentPosition
    {
        get => _currentPosition;
        set => SetProperty(ref _currentPosition, value);
    }

    private MediaPlayer? _player;

    public MediaPlayer? Player
    {
        get => _player;
        set => SetProperty(ref _player, value);
    }

    private bool _isBuffering;

    public bool IsBuffering
    {
        get => _isBuffering;
        set => SetProperty(ref _isBuffering, value);
    }

    private string? _streamError;

    public string? StreamError
    {
        get => _streamError;
        set => SetProperty(ref _streamError, value);
    }

    public int? CurrentPlayerChannelId { get; private set; }

    public bool IsArchivePlaying { get; private set; }

    /// <summary>
    /// Играет ли VOD-элемент видео-портала. В отличие от HLS-эфира, MP4/HLS-VOD
    /// перематывается и ставится на паузу самим медиа-движком — рестарт потока
    /// не нужен, поэтому пробел (ToggleArchivePause) работает и на VOD.
    /// </summary>
    public bool IsVodPlaying { get; private set; }

    // ===================== Качество VOD портала =====================
    // Портал отдаёт варианты качества отдельными ссылками (480/720/1080/auto
    // в ответе flick). Переключение = рестарт потока с новой ссылкой, позиция
    // фильма переносится на новый плеер.

    private ChannelViewModel? _vodChannel;
    private Dictionary<string, string> _vodVariantUrls = new();

    /// <summary>Доступные качества текущего VOD ("Авто", "1080p", ...) по убыванию.</summary>
    public IReadOnlyList<string> VodQualities { get; private set; } = Array.Empty<string>();

    /// <summary>Выбранное качество текущего VOD (null — варианты недоступны).</summary>
    public string? CurrentVodQuality { get; private set; }

    // ===================== Эпизоды VOD портала =====================
    // Список серий текущего сериала: стартует из PlayChannelAsync (flick) и
    // живёт в плеере — панели показывают комбобокс, выбор серии переключает
    // поток без повторного запроса к порталу (ссылки уже получены).

    private List<PortalEpisode> _vodEpisodes = new();

    /// <summary>Эпизоды текущего VOD-сериала (пусто — фильм/эфир).</summary>
    public IReadOnlyList<PortalEpisode> VodEpisodes { get; private set; } = Array.Empty<PortalEpisode>();

    /// <summary>Индекс играющего эпизода в VodEpisodes (-1 — нет списка).</summary>
    public int CurrentVodEpisodeIndex { get; private set; } = -1;

    /// <summary>Канал-сериал текущего VOD (для сезонных переключений).</summary>
    public ChannelViewModel? VodChannel => _vodChannel;

    /// <summary>
    /// Переключает серию текущего VOD-сериала: стартует поток выбранного
    /// эпизода (ссылки уже получены flick'ом, без запроса к порталу), список
    /// эпизодов сохраняется для комбобокса.
    /// </summary>
    public async Task PlayVodEpisodeAsync(int index)
    {
        if (!IsVodPlaying || _vodChannel == null ||
            index < 0 || index >= _vodEpisodes.Count)
        {
            return;
        }

        var episode = _vodEpisodes[index];
        _logger.LogInformation("VOD: серия {Current} → {Next} («{Title}»).",
            CurrentVodEpisodeIndex + 1, index + 1, episode.Title);
        await StartPlaybackAsync(_vodChannel, episode.StreamUrl, archiveEntry: null, isVod: true,
            vodVariants: episode.Variants.Count > 0 ? episode.Variants : null,
            vodQuality: CurrentVodQuality,
            vodEpisodes: _vodEpisodes, vodEpisodeIndex: index);
    }

    /// <summary>Изменилось состояние VOD (старт/стоп/смена качества) — обновить кнопки панелей.</summary>
    public event EventHandler? VodStateChanged;

    // ===================== Позиция VOD =====================
    // В отличие от HLS-timeshift архива (позиция по стенным часам, seek —
    // рестарт потока), VOD-поток перематывается самим медиа-движком:
    // позиция и длительность читаются из PlaybackSession, seek — прямой
    // player.Position = …

    private double _vodPositionSeconds;
    private double _vodDurationSeconds;

    /// <summary>Тянется ли ползунок перемотки VOD в представлении.</summary>
    public bool IsVodSeeking { get; set; }

    public double VodPositionSeconds
    {
        get => _vodPositionSeconds;
        private set => SetProperty(ref _vodPositionSeconds, value);
    }

    public double VodDurationSeconds
    {
        get => _vodDurationSeconds;
        private set => SetProperty(ref _vodDurationSeconds, value);
    }

    public string VodPositionText { get; private set; } = "00:00";
    public string VodDurationText { get; private set; } = "00:00";

    /// <summary>
    /// Обновляет позицию/длительность VOD из медиа-движка. Вызывается
    /// секундным таймером представления; вне VOD — тихий no-op.
    /// </summary>
    public void RefreshVodPosition()
    {
        if (!IsVodPlaying || Player?.PlaybackSession == null)
        {
            return;
        }

        try
        {
            VodDurationSeconds = Player.PlaybackSession.NaturalDuration.TotalSeconds;
            if (!IsVodSeeking)
            {
                VodPositionSeconds = Player.PlaybackSession.Position.TotalSeconds;
            }

            VodPositionText = FormatArchiveTime(VodPositionSeconds);
            VodDurationText = FormatArchiveTime(VodDurationSeconds);
            OnPropertyChanged(nameof(VodPositionText));
            OnPropertyChanged(nameof(VodDurationText));
        }
        catch (Exception ex)
        {
            // NaturalDuration недоступен, пока источник не открылся, — штатно.
            _logger.LogDebug(ex, "VOD: позиция недоступна.");
        }
    }

    /// <summary>Перемотка VOD к позиции (сек) — напрямую через медиа-движок.</summary>
    public void SeekVod(double positionSeconds)
    {
        if (!IsVodPlaying || Player?.PlaybackSession == null)
        {
            return;
        }

        var duration = Player.PlaybackSession.NaturalDuration.TotalSeconds;
        if (duration > 0)
        {
            positionSeconds = Math.Clamp(positionSeconds, 0.0, Math.Max(0.0, duration - 1));
        }

        try
        {
            Player.PlaybackSession.Position = TimeSpan.FromSeconds(positionSeconds);
            VodPositionSeconds = positionSeconds;
            _logger.LogInformation("VOD: перемотка на {Position}.", TimeSpan.FromSeconds(positionSeconds));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VOD: перемотка на {Position} не удалась.", positionSeconds);
        }
    }

    public EPGEntry? ArchiveEntry { get; private set; }

    // ===================== Позиция архивного воспроизведения =====================
    // HLS-timeshift не перематывается на лету: смена позиции = перезапуск
    // потока с новой точкой старта (ArchiveUrlBuilder). Чтобы слайдер
    // перемотки что-то показывал, позиция вычисляется по стенным часам от
    // момента старта (и суммарного времени пауз) — точности в секунду
    // хватает, медиа-движок сам держит фактический буфер.
    //
    // Модель полосы: ноль — НАЧАЛО ПЕРЕДАЧИ, максимум — её конец (полоса
    // всегда изображает всю передачу целиком). Точка старта показа после
    // перемотки отличается от начала передачи, поэтому она входит в позицию
    // слагаемым: позиция = (старт показа - начало передачи) + прошедшее
    // время. После перемотки на 15-ю минуту индикатор так и остаётся на
    // 15-й минуте, а не падает в ноль.

    private ChannelViewModel? _archiveChannel;
    private DateTime _archivePlayStartWallUtc;
    private DateTime _archiveStartPosition;
    private TimeSpan _archivePausedTotal;
    private DateTime? _archivePausedAtUtc;
    private double _archivePositionSeconds;
    private double _archiveDurationSeconds;

    /// <summary>
    /// Тянется ли прямо сейчас ползунок перемотки в представлении: пока
    /// true, RefreshArchivePosition не двигает Value слайдера из таймера
    /// (иначе палец «сбрасывало» бы ежесекундным обновлением).
    /// </summary>
    public bool IsArchiveSeeking { get; set; }

    /// <summary>Секунд от начала передачи до текущей позиции архивного показа.</summary>
    public double ArchivePositionSeconds
    {
        get => _archivePositionSeconds;
        private set => SetProperty(ref _archivePositionSeconds, value);
    }

    /// <summary>
    /// Длина слайдера — вся передача от начала до конца: полоса всегда
    /// изображает программу целиком. Идущая прямо сейчас передача просто
    /// ещё «не докрутилась»: позиция (и точка перемотки) не заходит дальше
    /// живого эфира, но масштаб полосы не меняется со временем.
    /// </summary>
    public double ArchiveDurationSeconds
    {
        get => _archiveDurationSeconds;
        private set => SetProperty(ref _archiveDurationSeconds, value);
    }

    /// <summary>Подпись позиции слайдера (h:mm:ss / mm:ss).</summary>
    public string ArchivePositionText { get; private set; } = "00:00";

    /// <summary>Подпись длительности слайдера.</summary>
    public string ArchiveDurationText { get; private set; } = "00:00";

    /// <summary>
    /// Пересчитывает позицию/длительность архива по часам. Вызывается
    /// секундным таймером представления; вне архива — тихий no-op.
    /// </summary>
    public void RefreshArchivePosition()
    {
        if (!IsArchivePlaying || ArchiveEntry == null)
        {
            return;
        }

        var wallElapsed = DateTime.UtcNow - _archivePlayStartWallUtc - _archivePausedTotal;
        if (_archivePausedAtUtc is { } pausedAt)
        {
            wallElapsed -= DateTime.UtcNow - pausedAt;
        }

        // Позиция — от НАЧАЛА ПЕРЕДАЧИ: точка старта показа (после
        // перемотки смещённая) входит слагаемым, поэтому после перемотки
        // индикатор остаётся на перемотанной минуте, а не падает в ноль.
        var position = (_archiveStartPosition - ArchiveEntry.StartTime).TotalSeconds + wallElapsed.TotalSeconds;
        var total = (ArchiveEntry.EndTime - ArchiveEntry.StartTime).TotalSeconds;
        var liveEdge = (DateTime.Now - ArchiveEntry.StartTime).TotalSeconds;

        ArchivePositionSeconds = Math.Clamp(position, 0.0, Math.Min(total, Math.Max(0.0, liveEdge)));
        ArchiveDurationSeconds = Math.Max(1.0, total);
        ArchivePositionText = FormatArchiveTime(ArchivePositionSeconds);
        ArchiveDurationText = FormatArchiveTime(ArchiveDurationSeconds);

        OnPropertyChanged(nameof(ArchivePositionText));
        OnPropertyChanged(nameof(ArchiveDurationText));
    }

    /// <summary>
    /// Перемотка архива к позиции (секунд от начала передачи): перезапускает
    /// поток с новой точки старта. Пока FFmpeg пересоздаёт источник, экран
    /// на долю секунды держит старый кадр — как при переключении канала.
    /// </summary>
    public async Task SeekArchiveAsync(double positionSeconds)
    {
        if (!IsArchivePlaying || ArchiveEntry == null ||
            _archiveChannel == null || string.IsNullOrWhiteSpace(_archiveChannel.StreamUrl))
        {
            return;
        }

        var start = ArchiveEntry.StartTime + TimeSpan.FromSeconds(Math.Max(0, positionSeconds));

        // В будущее уйти нельзя: точка старта минимум на несколько секунд
        // позади живого эфира, иначе провайдер отдаёт пустой плейлист.
        var liveEdge = DateTime.Now.AddSeconds(-5);
        if (start > liveEdge)
        {
            start = liveEdge;
        }

        var url = ArchiveUrlBuilder.BuildUrl(_archiveChannel.StreamUrl, start);
        _logger.LogInformation(
            "Перемотка архива: передача {Program} [{Start:HH:mm:ss}-{End:HH:mm:ss}], позиция {Position:F0} c, новая точка старта {SeekStart:HH:mm:ss}.",
            ArchiveEntry.ProgramName, ArchiveEntry.StartTime, ArchiveEntry.EndTime, positionSeconds, start);
        await StartPlaybackAsync(_archiveChannel, url, ArchiveEntry, archivePlayStart: start);
    }

    /// <summary>
    /// Формат подписи времени на полосе перемотки архива (h:mm:ss / mm:ss).
    /// Используется и представлением (подпись во время перетаскивания).
    /// </summary>
    internal static string FormatArchiveTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss")
            : t.ToString(@"mm\:ss");
    }

    /// <summary>Последняя громкость, выставленная пользователем (0..1).</summary>
    public double? LastUserVolume { get; set; }

    /// <summary>Плеер сменился (создан/остановлен) — представление переподключает элемент.</summary>
    public event EventHandler? PlayerChanged;

    /// <summary>Изменилось состояние архива (старт/конец архива, пауза) — обновить кнопки/баннеры.</summary>
    public event EventHandler? ArchiveStateChanged;

    public PlayerViewModel(IStreamService streamService, ISettingsService settingsService, ILogger<PlayerViewModel> logger)
    {
        _streamService = streamService;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>Прямой эфир канала.</summary>
    public async Task PlayLiveAsync(ChannelViewModel channel)
    {
        if (string.IsNullOrWhiteSpace(channel.StreamUrl))
        {
            StreamError = L.T("U_Kanala_Ne_Ukazan_URL_Potoka");
            return;
        }

        await StartPlaybackAsync(channel, channel.StreamUrl, archiveEntry: null);
    }

    /// <summary>
    /// Запуск потока (эфир или архивный timeshift) в новом плеере.
    /// archivePlayStart — фактическая точка старта архивного показа (после
    /// перемотки отличается от ArchiveEntry.StartTime).
    /// </summary>
    public async Task StartPlaybackAsync(ChannelViewModel channel, string streamUrl, EPGEntry? archiveEntry, DateTime? archivePlayStart = null, bool isVod = false, Dictionary<string, string>? vodVariants = null, string? vodQuality = null, TimeSpan? resumePosition = null, IReadOnlyList<PortalEpisode>? vodEpisodes = null, int vodEpisodeIndex = -1)
    {
        // Поколение запуска: при быстром зиппинге каналов несколько
        // StartPlaybackAsync конкурируют — каждый Stop() инкрементирует
        // счётчик, и запуск, чьё поколение устарело (пользователь уже
        // ушёл на другой канал), молча выбывает: не перезаписывает Player
        // и не оставляет второй поток качаться в фоне.
        Stop();
        var generation = _playbackGeneration;

        StreamError = null;
        IsBuffering = true;
        _lastStreamUrl = streamUrl;

            var startWait = System.Diagnostics.Stopwatch.StartNew();
            var streamSettings = await _settingsService.LoadAsync();
            var streamConfig = new PlaybackConfig(
                streamSettings.DecoderMode,
                streamSettings.AudioNormalization,
                streamSettings.ReadAheadSeconds,
                streamSettings.VodReadAheadSeconds,
                streamSettings.DiagnosticStreamProxy,
                streamSettings.VideoUpscaler,
                streamSettings.FrameServerRender);
            try
            {
                var player = await _streamService.CreatePlayerAsync(streamUrl, streamConfig, isVod);
            if (generation != _playbackGeneration)
            {
                // Пока качался этот канал, пользователь выбрал другой:
                // этот плеер уже никому не нужен — освобождаем молча.
                _logger.LogInformation(
                    "ЗАПУСК-ТАЙМИНГ: запуск «{Channel}» устарел (поколение {Generation}/{Current}) — освобождаем без воспроизведения.",
                    channel.Name, generation, _playbackGeneration);
                try
                {
                    player.Source = null;
                    player.Dispose();
                }
                catch
                {
                    // Освобождение проигравшего гонку не должно ломать победителя.
                }
                return;
            }
            _logger.LogInformation(
                "ЗАПУСК-ТАЙМИНГ: CreatePlayerAsync «{Channel}» занял {Ms:F0} мс.",
                channel.Name, startWait.Elapsed.TotalMilliseconds);
            player.MediaFailed += OnMediaFailed;

            // Громкость, выставленная пользователем, переносится на каждый
            // новый плеер — иначе при переключении канала она сбрасывалась бы.
            // В беззвучном режиме новый плеер стартует тоже без звука.
            if (IsMuted)
            {
                player.Volume = 0;
            }
            else if (LastUserVolume.HasValue)
            {
                player.Volume = LastUserVolume.Value;
            }

            Player = player;
            EnsureDisplayRequest(); // экран не гаснет, пока идёт воспроизведение
            CurrentPlayerChannelId = channel.Id;
            IsArchivePlaying = archiveEntry != null;
            IsVodPlaying = isVod && archiveEntry == null;

            // Варианты качества портала: набор переживает переключения
            // (SwitchVodQuality передаёт его заново), вне VOD — сбрасывается.
            if (IsVodPlaying)
            {
                _vodChannel = channel;
                if (vodVariants is { Count: > 0 })
                {
                    // Ключи — метки качеств ("Авто", "1080p"), как в VodQualities:
                    // CycleVodQuality ищет ссылку именно по метке. Сборка вручную,
                    // а не ToDictionary: если портал отдаст и "1080", и "1080p",
                    // обе метки склеятся в "1080p" — дубликат ключа не должен
                    // ронять запуск воспроизведения.
                    _vodVariantUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in vodVariants)
                    {
                        _vodVariantUrls[VodQualityLabel(kv.Key)] = kv.Value;
                    }
                }

                // Список эпизодов приходит при старте сериала; переключение
                // качества передаёт null — текущий список сохраняется.
                if (vodEpisodes is { Count: > 0 })
                {
                    _vodEpisodes = vodEpisodes.ToList();
                    VodEpisodes = _vodEpisodes;
                    CurrentVodEpisodeIndex = vodEpisodeIndex;
                }

                VodQualities = OrderVodQualities(_vodVariantUrls.Keys);
                CurrentVodQuality = VodQualities.Contains(vodQuality) ? vodQuality : null;

                // Возобновление позиции после смены качества: медиа-движок
                // применит seek после открытия источника; если не применит —
                // показ начнётся с начала (не критично).
                if (resumePosition is { } resume && resume > TimeSpan.Zero)
                {
                    try
                    {
                        player.Position = resume;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "VOD: не удалось перемотать на прежнюю позицию {Position}.", resume);
                    }
                }
            }
            else
            {
                _vodChannel = null;
                _vodVariantUrls = new Dictionary<string, string>();
                VodQualities = Array.Empty<string>();
                CurrentVodQuality = null;
                _vodEpisodes = new List<PortalEpisode>();
                VodEpisodes = Array.Empty<PortalEpisode>();
                CurrentVodEpisodeIndex = -1;
            }

            ArchiveEntry = archiveEntry;
            StreamId = streamUrl;
            channel.IsPlaying = true;

            // Отметка трекинга позиции архива: отсчёт стенных часов — от
            // момента старта показа, точка старта (после перемотки отличается
            // от начала передачи) войдёт в позицию слагаемым — см. блок
            // трекинга выше.
            if (archiveEntry != null)
            {
                _archiveChannel = channel;
                _archivePlayStartWallUtc = DateTime.UtcNow;
                _archiveStartPosition = archivePlayStart ?? archiveEntry.StartTime;
                _archivePausedTotal = TimeSpan.Zero;
                _archivePausedAtUtc = null;
                ArchivePositionSeconds = Math.Max(0,
                    (_archiveStartPosition - archiveEntry.StartTime).TotalSeconds);
                ArchiveDurationSeconds = Math.Max(1,
                    (archiveEntry.EndTime - archiveEntry.StartTime).TotalSeconds);
            }

            PlayerChanged?.Invoke(this, EventArgs.Empty);
            ArchiveStateChanged?.Invoke(this, EventArgs.Empty);
            VodStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartPlaybackAsync: канал {ChannelId}, url {Url}.", channel.Id, streamUrl);
            StreamError = string.Format(L.T("Ne_Udalos_Vosproizvesti_Potok_0"), ex.Message, ex.Message);
            channel.IsPlaying = false;
        }
        finally
        {
            IsBuffering = false;
        }
    }

    // ===================== Подавление гашения экрана =====================

    // DisplayRequest активен, пока есть живой плеер: экран и заставка не
    // гаснут при просмотре. Запрос/снятие считаются системой, поэтому
    // строго парные — флаг не даёт рассинхрониться при повторных вызовах.
    private bool _displayRequested;
    private readonly Windows.System.Display.DisplayRequest _displayRequest = new();

    // Поколение запуска воспроизведения: инкремент на каждый Stop() —
    // см. StartPlaybackAsync (гонка быстрого переключения каналов).
    private int _playbackGeneration;

    private void EnsureDisplayRequest()
    {
        if (_displayRequested)
        {
            return;
        }
        try
        {
            _displayRequest.RequestActive();
            _displayRequested = true;
            _logger.LogDebug("DisplayRequest: активен (экран не гаснет)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisplayRequest: RequestActive не сработал");
        }
    }

    private void ReleaseDisplayRequest()
    {
        if (!_displayRequested)
        {
            return;
        }
        try
        {
            _displayRequest.RequestRelease();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DisplayRequest: RequestRelease не сработал");
        }
        _displayRequested = false;
    }

    /// <summary>Полная остановка текущего плеера (смена канала, закрытие).</summary>
    public void Stop()
    {
        _playbackGeneration++;
        ReleaseDisplayRequest();
        if (Player == null)
        {
            return;
        }

        var player = Player;
        Player = null;
        CurrentPlayerChannelId = null;
        IsArchivePlaying = false;
        IsVodPlaying = false;
        _vodChannel = null;
        _vodVariantUrls = new Dictionary<string, string>();
        VodQualities = Array.Empty<string>();
        CurrentVodQuality = null;
        _vodEpisodes = new List<PortalEpisode>();
        VodEpisodes = Array.Empty<PortalEpisode>();
        CurrentVodEpisodeIndex = -1;
        ArchiveEntry = null;
        _archiveChannel = null;
        _archivePausedAtUtc = null;

        // Порядок критичен: СНАЧАЛА отвязать плеер от MediaPlayerElement
        // (PlayerChanged → SetMediaPlayer(null)) и только потом освобождать.
        // Dispose плеера, ещё подключённого к элементу, ронял процесс при
        // переключении каналов: медиа-движок продолжал тянуть кадры из
        // освобождённого объекта — нативный крах без записи в лог (из-за
        // TryEnqueue в подписке MainPage отвязка раньше успевала произойти
        // уже ПОСЛЕ Dispose).
        PlayerChanged?.Invoke(this, EventArgs.Empty);
        ArchiveStateChanged?.Invoke(this, EventArgs.Empty);
        VodStateChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            var teardown = System.Diagnostics.Stopwatch.StartNew();
            player.Pause();
            player.MediaFailed -= OnMediaFailed;
            player.Source = null;
            player.Dispose();
            // Нативный teardown медиа-конвейера идёт синхронно на UI-потоке:
            // если он начинает стоить заметно — это кандидату на вынос в пул.
            teardown.Stop();
            if (teardown.Elapsed.TotalMilliseconds > 100)
            {
                _logger.LogWarning(
                    "Stop: освобождение плеера заняло {Ms:F0} мс (нативный teardown на UI-потоке).",
                    teardown.Elapsed.TotalMilliseconds);
            }
            else
            {
                _logger.LogInformation("Stop: освобождение плеера заняло {Ms:F0} мс.", teardown.Elapsed.TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stop: не удалось освободить плеер.");
        }
    }

    /// <summary>
    /// Пауза/возобновление архивной передачи. Возвращает true, если действие
    /// выполнено (архив играет и канал совпадает).
    /// </summary>
    public bool ToggleArchivePause(ChannelViewModel? selectedChannel)
    {
        if (Player == null || selectedChannel == null || CurrentPlayerChannelId != selectedChannel.Id)
        {
            return false;
        }

        if (selectedChannel.IsPlaying)
        {
            // Начало паузы: стенные часы продолжают идти — запоминаем момент,
            // чтобы вычесть его из вычисляемой позиции архива при возобновлении.
            _archivePausedAtUtc = DateTime.UtcNow;
            Player.Pause();
            selectedChannel.IsPlaying = false;
        }
        else
        {
            if (_archivePausedAtUtc is { } pausedAt)
            {
                _archivePausedTotal += DateTime.UtcNow - pausedAt;
                _archivePausedAtUtc = null;
            }
            Player.Play();
            selectedChannel.IsPlaying = true;
        }

        ArchiveStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Сброс архивного состояния (например, поток упал).</summary>
    public void ResetArchiveState()
    {
        IsArchivePlaying = false;
        ArchiveEntry = null;
        ArchiveStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Подкладывает варианты качества в уже играющий VOD (фоновый flick для
    /// фильма, стартовавшего мгновенно по ссылке каталога). Кнопка качества
    /// появляется по событию VodStateChanged.
    /// </summary>
    public void SetVodVariants(Dictionary<string, string> variants)
    {
        if (!IsVodPlaying || variants.Count == 0)
        {
            return;
        }

        foreach (var pair in variants)
        {
            _vodVariantUrls[VodQualityLabel(pair.Key)] = pair.Value;
        }

        VodQualities = OrderVodQualities(_vodVariantUrls.Keys);
        VodStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Переключает качество VOD на выбранную метку ("Авто", "1080p", ...):
    /// рестарт потока с новой ссылкой и возобновлением позиции.
    /// </summary>
    public async Task SwitchVodQualityAsync(string quality)
    {
        if (!IsVodPlaying || _vodChannel == null || !_vodVariantUrls.TryGetValue(quality, out var url))
        {
            return;
        }

        var resume = Player?.Position ?? TimeSpan.Zero;
        _logger.LogInformation("VOD: качество {Current} → {Next} (позиция {Position}).",
            CurrentVodQuality ?? "?", quality, resume);
        await StartPlaybackAsync(_vodChannel, url, archiveEntry: null, isVod: true,
            vodVariants: _vodVariantUrls, vodQuality: quality, resumePosition: resume);
    }

    /// <summary>
    /// Ключ портала → метка качества: "auto" → "Авто", "1080" → "1080p".
    /// Идемпотентно: уже готовая метка ("1080p", "Авто") не меняется — при
    /// переключении качества словарь ссылок (с метками) передаётся в
    /// StartPlaybackAsync повторно и не должен обрабатываться дважды.
    /// </summary>
    private static string VodQualityLabel(string key)
    {
        if (key.Equals("auto", StringComparison.OrdinalIgnoreCase) || key == "Авто")
        {
            return "Авто";
        }

        if (key.EndsWith('p') && int.TryParse(key[..^1], out _))
        {
            return key;
        }

        return key + "p";
    }

    /// <summary>
    /// Подписи качеств по убыванию: "Авто" первым, дальше числовые от большего
    /// к меньшему ("1080p", "720p", "480p"), прочие ключи в конце как есть.
    /// </summary>
    private static IReadOnlyList<string> OrderVodQualities(IEnumerable<string> labels)
    {
        static string? NumericKey(string label) =>
            label.EndsWith('p') && int.TryParse(label[..^1], out var n) ? n.ToString() : null;

        var list = labels.Distinct().ToList();
        var auto = list.Where(l => l == "Авто").ToList();
        var numeric = list.Where(l => NumericKey(l) != null && l != "Авто")
            .OrderByDescending(l => int.Parse(l[..^1]))
            .ToList();
        var other = list.Where(l => l != "Авто" && NumericKey(l) == null)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return auto.Concat(numeric).Concat(other).ToList();
    }

    // ===================== Беззвучный режим =====================

    // Mute не трогает LastUserVolume: запомненная громкость переживает
    // mute и переключение каналов, а в настройках сохраняется именно она —
    // после перезапуска приложения звук просто включён.

    private bool _isMuted;
    private double? _volumeBeforeMute;

    /// <summary>
    /// Включён ли беззвучный режим. Представление реагирует на смену
    /// (кнопки M, слайдеры показывают ноль) через PropertyChanged.
    /// </summary>
    public bool IsMuted => _isMuted;

    /// <summary>
    /// Переключает беззвучный режим: громкость текущего плеера в 0 без
    /// потери запомненного значения; при снятии — восстановление. Новые
    /// плееры (переключение канала) создаются уже с учётом режима.
    /// </summary>
    public void ToggleMute()
    {
        if (_isMuted)
        {
            _isMuted = false;
            var restore = _volumeBeforeMute ?? LastUserVolume ?? 1.0;
            if (restore <= 0.001)
            {
                // Mute нажали при нулевой громкости — восстанавливать нечего.
                restore = 1.0;
            }
            _volumeBeforeMute = null;
            if (Player != null)
            {
                Player.Volume = restore;
            }
        }
        else
        {
            _volumeBeforeMute = LastUserVolume ?? Player?.Volume ?? 1.0;
            _isMuted = true;
            if (Player != null)
            {
                Player.Volume = 0;
            }
        }

        OnPropertyChanged(nameof(IsMuted));
    }

    /// <summary>
    /// Снимает mute без восстановления громкости — когда громность уже
    /// применена снаружи (пользователь двинул слайдер/колесо мыши).
    /// </summary>
    public void ClearMute()
    {
        if (!_isMuted)
        {
            return;
        }

        _isMuted = false;
        _volumeBeforeMute = null;
        OnPropertyChanged(nameof(IsMuted));
    }

    // Текущий пресет улучшения картинки (для отметки в меню кнопки).
    private string _videoUpscaler = VideoUpscaler.Off;

    public string VideoUpscalerMode
    {
        get => _videoUpscaler;
        set => SetProperty(ref _videoUpscaler, value);
    }

    /// <summary>
    /// Смена пресета улучшения картинки (кнопка «Качество картинки»):
    /// применяется живьём к играющему плееру и сохраняется в настройках —
    /// следующие каналы открываются уже с выбранным пресетом.
    /// </summary>
    public async Task SetVideoUpscalerAsync(string mode)
    {
        var normalized = VideoUpscaler.Normalize(mode);

        // Локальный файл: живая смена фильтров (SetFFmpegVideoFilters)
        // перестраивает граф и стопорит воспроизведение насмерть — файл
        // не сеть, рестарт мгновенный. Перезапускаем с новой настройкой
        // и той же позицией: CreatePlayerAsync читает VideoUpscaler из
        // настроек, поэтому пресет сохраняем ДО рестарта.
        if (VodChannel is { IsLocalFile: true } localChannel &&
            !string.IsNullOrWhiteSpace(localChannel.StreamUrl))
        {
            VideoUpscalerMode = normalized;
            try
            {
                var localSettings = await _settingsService.LoadAsync();
                localSettings.VideoUpscaler = normalized;
                await _settingsService.SaveAsync(localSettings);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось сохранить пресет улучшения картинки.");
            }

            var resume = VodPositionSeconds;
            if (resume > 0)
            {
                await StartPlaybackAsync(localChannel, localChannel.StreamUrl!, archiveEntry: null,
                    isVod: true, resumePosition: TimeSpan.FromSeconds(resume));
            }
            else
            {
                await StartPlaybackAsync(localChannel, localChannel.StreamUrl!, archiveEntry: null, isVod: true);
            }

            _logger.LogInformation(
                "Пресет улучшения картинки {Mode} применён рестартом локального файла (живая смена фильтров его стопорит).",
                normalized);
            return;
        }

        if (normalized == _videoUpscaler && normalized == VideoUpscaler.Off)
        {
            // Повторный выбор Off — экономим запись настроек.
            _streamService.ApplyVideoFilters(Player, normalized);
            return;
        }

        VideoUpscalerMode = normalized;
        _streamService.ApplyVideoFilters(Player, normalized);

        try
        {
            var settings = await _settingsService.LoadAsync();
            settings.VideoUpscaler = normalized;
            await _settingsService.SaveAsync(settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось сохранить пресет улучшения картинки.");
        }
    }

    private async void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        _logger.LogError(
            "MediaPlayer.MediaFailed: Status={Status}, Code=0x{Code:x}{Message}",
            args.Error, args.ExtendedErrorCode,
            string.IsNullOrEmpty(args.ErrorMessage) ? string.Empty : $", {args.ErrorMessage}");

        var errorMsg = args.ErrorMessage ?? args.Error.ToString();
        var diagnostic = await _streamService.DiagnoseStreamUrl(_lastStreamUrl);

        StreamError = $"{L.T("Oshibka_Vosproizvedeniya")}{errorMsg}\n\n{string.Format(L.T("Diagnostika_Prefiks_0"), diagnostic)}";
        IsBuffering = false;
        ResetArchiveState();
    }
}
