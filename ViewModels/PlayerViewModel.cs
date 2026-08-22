using System;
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
    private readonly ILogger<PlayerViewModel> _logger;

    // Свойства ниже — не [ObservableProperty], а ручные: сгенерированные
    // генератором свойства не создают WinRT-проекторов, и в WinUI/AOT-сценариях
    // (маршализация через ABI) это предупреждение MVVMTK0045. Ручное
    // свойство поверх поля даёт ту же семантику INotifyPropertyChanged.
    private string _streamId = string.Empty;

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

    public PlayerViewModel(IStreamService streamService, ILogger<PlayerViewModel> logger)
    {
        _streamService = streamService;
        _logger = logger;
    }

    /// <summary>Прямой эфир канала.</summary>
    public async Task PlayLiveAsync(ChannelViewModel channel)
    {
        if (string.IsNullOrWhiteSpace(channel.StreamUrl))
        {
            StreamError = L.T("У канала не указан URL потока.", "Channel has no stream URL.");
            return;
        }

        await StartPlaybackAsync(channel, channel.StreamUrl, archiveEntry: null);
    }

    /// <summary>
    /// Запуск потока (эфир или архивный timeshift) в новом плеере.
    /// archivePlayStart — фактическая точка старта архивного показа (после
    /// перемотки отличается от ArchiveEntry.StartTime).
    /// </summary>
    public async Task StartPlaybackAsync(ChannelViewModel channel, string streamUrl, EPGEntry? archiveEntry, DateTime? archivePlayStart = null)
    {
        Stop();

        StreamError = null;
        IsBuffering = true;

        try
        {
            var player = await _streamService.CreatePlayerAsync(streamUrl);
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
            CurrentPlayerChannelId = channel.Id;
            IsArchivePlaying = archiveEntry != null;
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartPlaybackAsync: канал {ChannelId}, url {Url}.", channel.Id, streamUrl);
            StreamError = L.T($"Не удалось воспроизвести поток: {ex.Message}", $"Cannot play stream: {ex.Message}");
            channel.IsPlaying = false;
        }
        finally
        {
            IsBuffering = false;
        }
    }

    /// <summary>Полная остановка текущего плеера (смена канала, закрытие).</summary>
    public void Stop()
    {
        if (Player == null)
        {
            return;
        }

        var player = Player;
        Player = null;
        CurrentPlayerChannelId = null;
        IsArchivePlaying = false;
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

        try
        {
            player.Pause();
            player.MediaFailed -= OnMediaFailed;
            player.Source = null;
            player.Dispose();
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

    /// <summary>Применяет громкость к текущему плееру (слайдер/колесо мыши).</summary>
    public void SetVolume(double value)
    {
        LastUserVolume = value;
        if (Player != null)
        {
            Player.Volume = value;
        }
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

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        _logger.LogError(
            "MediaPlayer.MediaFailed: Status={Status}, Code=0x{Code:x}{Message}",
            args.Error, args.ExtendedErrorCode,
            string.IsNullOrEmpty(args.ErrorMessage) ? string.Empty : $", {args.ErrorMessage}");

        StreamError = L.T("Ошибка воспроизведения: ", "Playback error: ") + args.ErrorMessage;
        IsBuffering = false;
        ResetArchiveState();
    }
}
