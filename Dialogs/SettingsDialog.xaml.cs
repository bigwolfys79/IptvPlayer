using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage.Pickers;

namespace IptvPlayer.Dialogs;

/// <summary>
/// Диалог настроек. До выноса строился целиком кодом в
/// MainPage.SettingsButton_Click (~600 строк); теперь разметка — в XAML,
/// логика — здесь, разбита по зонам на partial-файлы: ядро (загрузка
/// контролов) — SettingsDialog.xaml.cs, обработчики и «Готово» —
/// SettingsDialog.Handlers.cs, плейлист/сброс — SettingsDialog.Playlist.cs,
/// экспорт/импорт — SettingsDialog.ImportExport.cs. MainPage передаёт
/// ViewModel, сервисы и три колбэка: тема и язык применяются к окну
/// немедленно (компетенция представления), всё остальное диалог делает сам
/// через ViewModel. Поведение сохранено: «Готово» сохраняет всё; обновление
/// EPG при изменённых источниках уходит фоном; «Сбросить» — с двухшаговым
/// подтверждением.
/// </summary>
public sealed partial class SettingsDialog : UserControl
{
    private readonly MainPageViewModel _viewModel;
    private readonly ISettingsService _settingsService;
    private readonly IM3UParserService _m3uParserService;
    private readonly IChannelRepository _channelRepository;
    private readonly IPlaylistCacheService _playlistCacheService;
    private readonly IStreamService _streamService;
    private readonly ILogger<SettingsDialog> _logger;
    private readonly Action<string> _applyTheme;
    private readonly Action _applyLanguage;
    private readonly Action<bool> _applyStatsOverlay;

    /// <summary>Настройки, загруженные при открытии диалога (для плейлиста и «Сбросить»).</summary>
    private AppSettings _currentSettings = new();

    /// <summary>
    /// Снимок источников на момент открытия диалога: по «Готово» отличаем
    /// реальное изменение списка от простого закрытия без правок. Копируем
    /// именно значения (Url, IsEnabled), а не ссылки — чекбоксы включения
    /// мутируют те же объекты EPGSource, что лежат в currentSettings.
    /// </summary>
    private List<(string Url, bool IsEnabled)> _initialEpgSources = new();

    /// <summary>Рабочая копия источников: редактируется в диалоге, персистится по «Готово».</summary>
    public ObservableCollection<EPGSource> EpgSources { get; } = new();

    private bool _resetArmed;

    public SettingsDialog(
        MainPageViewModel viewModel,
        ISettingsService settingsService,
        IM3UParserService m3uParserService,
        IChannelRepository channelRepository,
        IPlaylistCacheService playlistCacheService,
        IStreamService streamService,
        ILogger<SettingsDialog> logger,
        Action<string> applyTheme,
        Action applyLanguage,
        Action<bool> applyStatsOverlay)
    {
        _viewModel = viewModel;
        _settingsService = settingsService;
        _m3uParserService = m3uParserService;
        _channelRepository = channelRepository;
        _playlistCacheService = playlistCacheService;
        _streamService = streamService;
        _logger = logger;
        _applyTheme = applyTheme;
        _applyLanguage = applyLanguage;
        _applyStatsOverlay = applyStatsOverlay;
        InitializeComponent();
    }

    /// <summary>Наполняет контролы текущими настройками и показывает ContentDialog.</summary>
    public async Task ShowAsync(XamlRoot xamlRoot)
    {
        await LoadAsync();

        var dialog = new ContentDialog
        {
            Title = L.T("Настройки", "Settings"),
            Content = this,
            PrimaryButtonText = L.T("Готово", "Done"),
            XamlRoot = xamlRoot
        };

        // «Готово» закрывает диалог и сохраняет всё. Deferral держим только
        // на быструю запись JSON: обновление EPG (если источники менялись)
        // уже ушло фоном внутри SaveSettingsAsync, и диалог закрывается сразу.
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                await SaveSettingsAsync();
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    private async Task LoadAsync()
    {
        _currentSettings = await _settingsService.LoadAsync();

        TitleText.Text = L.T("Настройки IptvPlayer", "IptvPlayer Settings");
        LanguageHeader.Text = L.T("Язык интерфейса", "Interface language");
        ThemeHeader.Text = L.T("Тема интерфейса", "Interface theme");
        EpgSourcesHeader.Text = L.T("Источники EPG", "EPG sources");
        BufferHeader.Text = L.T("Буферизация", "Buffering");
        BufferHint.Text = L.T("Применится при следующем переключении канала", "Applied on next channel switch");
        DecoderHeader.Text = L.T("Декодирование видео", "Video decoding");
        DecoderHint.Text = L.T("Применится при следующем переключении канала", "Applied on next channel switch");
        RemindersHeader.Text = L.T("Напоминания EPG", "EPG reminders");
        PlaylistHeader.Text = L.T("Плейлист", "Playlist");
        EpgRefreshHeader.Text = L.T("EPG (программа передач)", "EPG (TV guide)");
        M3uHeader.Text = L.T("Файлы M3U", "M3U files");
        AddPlaylistButton.Content = L.T("Добавить источник плейлиста", "Add playlist source");
        ResetButton.Content = L.T("Сбросить", "Reset");
        AboutButton.Content = L.T("О приложении", "About");
        EpgUrlBox.PlaceholderText = L.T("URL XMLTV", "XMLTV URL");
        PlaylistUrlBox.PlaceholderText = L.T("URL плейлиста M3U/M3U8", "M3U/M3U8 playlist URL");
        LanguageCombo.PlaceholderText = L.T("Язык", "Language");
        PlaylistRefreshCombo.PlaceholderText = L.T("Частота обновления плейлиста", "Playlist refresh rate");
        EpgRefreshCombo.PlaceholderText = L.T("Частота обновления EPG", "EPG refresh rate");

        // Язык: сначала выбранное значение, потом подписка — чтобы открытие
        // диалога (и перезагрузка после импорта) не вызывали ложное
        // ApplyLanguage. Items.Clear и отписка делают LoadAsync повторно
        // вызываемой — без них после импорта элементы бы продублировались.
        LanguageCombo.SelectionChanged -= LanguageCombo_SelectionChanged;
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add("Русский");
        LanguageCombo.Items.Add("English");
        LanguageCombo.SelectedIndex = L.IsRussian ? 0 : 1;
        LanguageCombo.SelectionChanged += LanguageCombo_SelectionChanged;

        // Тема: применяется сразу — видно до нажатия «Готово».
        ThemeRadio.SelectionChanged -= ThemeRadio_SelectionChanged;
        ThemeRadio.Items.Clear();
        ThemeRadio.Items.Add(new RadioButton { Content = L.T("Светлая", "Light"), Tag = "Light" });
        ThemeRadio.Items.Add(new RadioButton { Content = L.T("Тёмная", "Dark"), Tag = "Dark" });
        ThemeRadio.Items.Add(new RadioButton { Content = L.T("Системная", "System"), Tag = "Default" });
        ThemeRadio.SelectedIndex = _currentSettings.Theme switch
        {
            "Light" => 0,
            "Dark" => 1,
            _ => 2
        };
        ThemeRadio.SelectionChanged += ThemeRadio_SelectionChanged;

        // Источники EPG: те же объекты, что в currentSettings — TwoWay-привязка
        // чекбоксов мутирует их, как и раньше (поверхностная копия списка).
        EpgSources.Clear();
        foreach (var source in _currentSettings.EpgSources)
        {
            EpgSources.Add(source);
        }
        _initialEpgSources = _currentSettings.EpgSources
            .Select(s => (s.Url, s.IsEnabled))
            .ToList();

        // Буфер видео.
        BufferSlider.ValueChanged -= BufferSlider_ValueChanged;
        BufferSlider.Value = Math.Clamp(_currentSettings.ReadAheadSeconds, 5, 60);
        BufferSlider.ValueChanged += BufferSlider_ValueChanged;
        UpdateBufferLabel();

        // Качество видео.
        QualityHeader.Text = L.T("Качество видео", "Video quality");
        QualityHint.Text = L.T(
            "Максимальное качество потока или ограничение разрешения. Применится при следующем переключении канала.",
            "Maximum stream quality or resolution limit. Applies on next channel switch.");
        QualityCombo.Items.Clear();
        foreach (var (label, height) in new[]
                 {
                     (L.T("Авто (максимальное)", "Auto (maximum)"), 0),
                     ("480p", 480),
                     ("720p HD", 720),
                     ("1080p Full HD", 1080),
                     ("2160p 4K UHD", 2160),
                 })
        {
            QualityCombo.Items.Add(new ComboBoxItem { Content = label, Tag = height });
            if (height == _currentSettings.PreferredQuality)
            {
                QualityCombo.SelectedIndex = QualityCombo.Items.Count - 1;
            }
        }
        if (QualityCombo.SelectedIndex < 0)
        {
            QualityCombo.SelectedIndex = 0;
        }

        // Режим декодирования.
        var hwRadio = new RadioButton
        {
            Content = L.T("Аппаратное (GPU, с откатом на процессор)", "Hardware (GPU, CPU fallback)"),
            Tag = "Hardware"
        };
        var swRadio = new RadioButton
        {
            Content = L.T("Программное (процессор)", "Software (CPU)"),
            Tag = "Software"
        };
        ToolTipService.SetToolTip(hwRadio, "Декодирование видеокартой; при проблемах с потоком автоматически переключается на процессор");
        ToolTipService.SetToolTip(swRadio, "Гарантированно плавно при запасе CPU; занимает несколько процентов процессора");
        DecoderRadio.Items.Clear();
        DecoderRadio.Items.Add(hwRadio);
        DecoderRadio.Items.Add(swRadio);
        DecoderRadio.SelectedIndex = string.Equals(_currentSettings.DecoderMode, "Hardware", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

        // Нормализация громкости: часть каналов кодируется в разы тише
        // остальных, а слайдер громкости ограничен 100% — тихие каналы
        // вытягиваются FFmpeg-фильтром до общей громкости.
        AudioNormHeader.Text = L.T("Нормализация громкости", "Volume normalization");
        AudioNormHint.Text = L.T(
            "Подтягивает тихие каналы к общему уровню. Применяется к играющему каналу сразу.",
            "Raises quiet channels to a common level. Applies to the playing channel immediately.");
        var normOff = new RadioButton { Content = L.T("Выключена", "Off"), Tag = "Off" };
        var normDynamic = new RadioButton
        {
            Content = L.T("Динамическая (усиливает тихие каналы)", "Dynamic (boosts quiet channels)"),
            Tag = "Dynamic"
        };
        var normLoudness = new RadioButton
        {
            Content = L.T("Постоянная громкость (EBU R128)", "Uniform loudness (EBU R128)"),
            Tag = "Loudness"
        };
        ToolTipService.SetToolTip(normDynamic, "Фильтр dynaudnorm: плавно поднимает тихий звук без искажений; громкие каналы почти не меняются");
        ToolTipService.SetToolTip(normLoudness, "Фильтр loudnorm: все каналы к единому уровню −16 LUFS (громкие станут тише); добавляет ~3 с задержки от эфира");
        AudioNormRadio.Items.Clear();
        AudioNormRadio.Items.Add(normOff);
        AudioNormRadio.Items.Add(normDynamic);
        AudioNormRadio.Items.Add(normLoudness);
        AudioNormRadio.SelectedIndex = _currentSettings.AudioNormalization switch
        {
            "Off" => 0,
            "Loudness" => 2,
            _ => 1
        };

        // За сколько минут напоминать о передачах.
        ReminderMinutesCombo.Items.Clear();
        foreach (var minutes in new[] { 1, 5, 10, 15, 30 })
        {
            ReminderMinutesCombo.Items.Add(new ComboBoxItem
            {
                Content = L.T($"За {minutes} мин до начала", $"{minutes} min before start"),
                Tag = minutes
            });
            if (minutes == _currentSettings.ReminderMinutes)
            {
                ReminderMinutesCombo.SelectedIndex = ReminderMinutesCombo.Items.Count - 1;
            }
        }

        // Действие таймера сна по истечении: остановить воспроизведение,
        // закрыть приложение или выключить компьютер (shutdown /s /t 0).
        SleepTimerActionHeader.Text = L.T("Таймер сна: по истечении", "Sleep timer: when it ends");
        SleepTimerActionHint.Text = L.T(
            "Применяется к уже взведённому таймеру, действие видно при его установке.",
            "Applies to an armed timer immediately; the action is shown when setting it.");
        SleepTimerActionCombo.Items.Clear();
        foreach (var (label, action) in new[]
                 {
                     (L.T("Остановить воспроизведение", "Stop playback"), "Stop"),
                     (L.T("Закрыть программу", "Close the app"), "Exit"),
                     (L.T("Выключить компьютер", "Shut down the PC"), "Shutdown"),
                 })
        {
            SleepTimerActionCombo.Items.Add(new ComboBoxItem { Content = label, Tag = action });
            if (action == _currentSettings.SleepTimerAction)
            {
                SleepTimerActionCombo.SelectedIndex = SleepTimerActionCombo.Items.Count - 1;
            }
        }
        if (SleepTimerActionCombo.SelectedIndex < 0)
        {
            SleepTimerActionCombo.SelectedIndex = 0;
        }

        // Периодичность обновления плейлиста.
        PlaylistRefreshCombo.Items.Clear();
        foreach (var (label, days) in new[]
                 {
                     (L.T("Каждый день", "Daily"), 1),
                     (L.T("Каждые 3 дня", "Every 3 days"), 3),
                     (L.T("Каждую неделю", "Weekly"), 7),
                     (L.T("Никогда (только при добавлении)", "Never (only when added)"), 0),
                 })
        {
            PlaylistRefreshCombo.Items.Add(new ComboBoxItem { Content = label, Tag = days });
            if (days == _currentSettings.PlaylistRefreshDays)
            {
                PlaylistRefreshCombo.SelectedIndex = PlaylistRefreshCombo.Items.Count - 1;
            }
        }

        // Периодичность обновления EPG.
        EpgRefreshCombo.Items.Clear();
        foreach (var (label, days) in new[]
                 {
                     (L.T("Каждый день", "Daily"), 1),
                     (L.T("Каждые 3 дня", "Every 3 days"), 3),
                     (L.T("Каждую неделю", "Weekly"), 7),
                     (L.T("Только вручную", "Manual only"), 0),
                 })
        {
            EpgRefreshCombo.Items.Add(new ComboBoxItem { Content = label, Tag = days });
            if (days == _currentSettings.EpgRefreshDays)
            {
                EpgRefreshCombo.SelectedIndex = EpgRefreshCombo.Items.Count - 1;
            }
        }

        PlaylistUrlBox.Text = _currentSettings.PlaylistUrl ?? string.Empty;

        // «О программе».
        AboutText.Text = $"IptvPlayer {GetAppVersion()}\n\n" +
                         "IPTV-плеер для плейлистов M3U/M3U8 с программой передач.\n\n" +
                         "Воспроизведение: FFmpeg (демуксинг, декодирование HEVC/AC-3 и др.) поверх Windows App SDK.\n" +
                         "EPG: XMLTV (epg.one), сопоставление каналов — по таблице epg.one/setup-playlist.\n\n" +
                         $"Настройки и кэш: %LocalAppData%\\IptvPlayer\n" +
                         $"Лог: {App.LogDirectory}";

        // Файловый лог (Serilog): применяется сразу, как тема и язык —
        // переключение через LoggingLevelSwitch не требует перезапуска и не
        // теряет события. Вывод в Debug (окно Output) остаётся всегда.
        DiagnosticsHeader.Text = L.T("Диагностика", "Diagnostics");
        FileLoggingHint.Text = L.T(
            $"Запись событий в {App.LogDirectory}. Выключение действует сразу и не требует перезапуска.",
            $"Writes events to {App.LogDirectory}. Turning it off takes effect immediately, no restart required.");
        FileLoggingToggle.Toggled -= FileLoggingToggle_Toggled;
        FileLoggingToggle.IsOn = _currentSettings.FileLoggingEnabled;
        FileLoggingToggle.Header = L.T("Файловый лог", "File log");
        FileLoggingToggle.OnContent = L.T("Вкл", "On");
        FileLoggingToggle.OffContent = L.T("Выкл", "Off");
        FileLoggingToggle.Toggled += FileLoggingToggle_Toggled;

        // Оверлей статистики потока (Ctrl+J): включается/выключается на лету,
        // состояние переживает перезапуск — удобно для диагностики на чужой
        // машине («включи и пришли скриншот»).
        StatsOverlayHint.Text = L.T(
            "Оверлей статистики поверх видео: кодеки, разрешение, битрейты, фактический декодер (GPU/CPU), заполнение буфера и простои. Также переключается клавишами Ctrl+J.",
            "Stream statistics overlay over the video: codecs, resolution, bitrates, actual decoder (GPU/CPU), buffer fill and stalls. Also toggled with Ctrl+J.");
        StatsOverlayToggle.Toggled -= StatsOverlayToggle_Toggled;
        StatsOverlayToggle.IsOn = _currentSettings.StatsOverlayVisible;
        StatsOverlayToggle.Header = L.T("Статистика потока (Ctrl+J)", "Stream statistics (Ctrl+J)");
        StatsOverlayToggle.OnContent = L.T("Вкл", "On");
        StatsOverlayToggle.OffContent = L.T("Выкл", "Off");
        StatsOverlayToggle.Toggled += StatsOverlayToggle_Toggled;

        // ===================== Экспорт / импорт =====================

        ImportExportHeader.Text = L.T("Экспорт / импорт настроек", "Settings export / import");
        ImportExportHint.Text = L.T(
            "Экспорт сохраняет все настройки (источники EPG, плейлист, избранное, плеер, интерфейс) в JSON-файл. Импорт заменяет текущие настройки, кроме положения окна и взведённого таймера сна; каналы подгрузятся по URL плейлиста.",
            "Export saves all settings (EPG sources, playlist, favorites, player, interface) to a JSON file. Import replaces current settings except window placement and the armed sleep timer; channels load from the playlist URL.");
        ExportButton.Content = L.T("Экспортировать...", "Export...");
        ImportButton.Content = L.T("Импортировать...", "Import...");
        ImportExportStatusText.Visibility = Visibility.Collapsed;
    }

    internal static string GetAppVersion()
    {
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            // Unpackaged-сборка (Inno Setup) — берём версию сборки.
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0";
        }
    }

    private void UpdateBufferLabel()
    {
        BufferValueText.Text = L.T(
            $"Буфер: {BufferSlider.Value:F0} c (задержка от эфира ~{BufferSlider.Value:F0} c)",
            $"Buffer: {BufferSlider.Value:F0} s (live delay ~{BufferSlider.Value:F0} s)");
    }
}