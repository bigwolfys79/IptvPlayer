using System;
using System.Threading.Tasks;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace IptvPlayer.Dialogs
{
    /// <summary>
    /// Раздел воспроизведения настроек: декодер, буферизация, качество,
    /// нормализация громкости. Сохраняет в каноническую копию
    /// AppSettings (ViewModel.AppSettings), как SettingsDialog. Декодер,
    /// буфер и качество применятся при следующем переключении
    /// канала; нормализация громкости — к играющему каналу сразу.
    /// </summary>
    public sealed partial class PlaybackSettingsDialog : UserControl
    {
        private static readonly string[] DecoderModes = { "Hardware", "Software" };
        private static readonly string[] AudioNormModes = { "Off", "Dynamic", "Loudness" };

        private readonly MainPageViewModel _viewModel;
        private readonly ISettingsService _settingsService;
        private readonly IStreamService _streamService;

        // Контейнер-ContentDialog создаётся в ShowAsync; кнопки внутри
        // UserControl закрывают его через эту ссылку (искать родителя по
        // визуальному дереву нельзя — им оказывается ContentPresenter
        // шаблона диалога, а не сам ContentDialog).
        private ContentDialog? _hostDialog;

        public PlaybackSettingsDialog(
            MainPageViewModel viewModel,
            ISettingsService settingsService,
            IStreamService streamService)
        {
            _viewModel = viewModel;
            _settingsService = settingsService;
            _streamService = streamService;
            InitializeComponent();

            // Подписка здесь, а не в XAML: при разборе XAML установка
            // Minimum="5" принудительно меняет Value (0 → 5), и ValueChanged
            // стреляет ещё внутри InitializeComponent — до создания подписи
            // BufferValueText ниже по разметке (NRE, диалог не открывался).
            BufferSlider.ValueChanged += BufferSlider_ValueChanged;
        }

        public async Task ShowAsync(XamlRoot xamlRoot)
        {
            await LoadAsync();
            // Заголовок показывает сам ContentDialog — внутренний TitleText
            // не нужен, иначе «Настройки воспроизведения» читается дважды.
            TitleText.Visibility = Visibility.Collapsed;

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = L.T("Настройки воспроизведения", "Playback settings"),
                Content = this
            };
            _hostDialog = dialog;
            await dialog.ShowAsync();
        }

        private async Task LoadAsync()
        {
            var settings = await _settingsService.LoadAsync();

            TitleText.Text = L.T("Настройки воспроизведения", "Playback settings");
            CancelButton.Content = L.T("Отмена", "Cancel");
            SaveButton.Content = L.T("Сохранить", "Save");

            // Декодер: аппаратный (с откатом на процессор) или программный.
            DecoderHeader.Text = L.T("Декодирование видео", "Video decoding");
            DecoderHint.Text = L.T("Применится при следующем переключении канала.", "Applied on next channel switch.");
            DecoderRadio.Items.Clear();
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
            DecoderRadio.Items.Add(hwRadio);
            DecoderRadio.Items.Add(swRadio);
            DecoderRadio.SelectedIndex =
                string.Equals(settings.DecoderMode, "Hardware", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

            // Буфер видео.
            BufferHeader.Text = L.T("Буферизация", "Buffering");
            BufferHint.Text = L.T("Применится при следующем переключении канала.", "Applied on next channel switch.");
            BufferSlider.Value = Math.Clamp(settings.ReadAheadSeconds, 5, 60);
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
                if (height == settings.PreferredQuality)
                {
                    QualityCombo.SelectedIndex = QualityCombo.Items.Count - 1;
                }
            }
            if (QualityCombo.SelectedIndex < 0)
            {
                QualityCombo.SelectedIndex = 0;
            }

            // Нормализация громкости: часть каналов кодируется в разы тише
            // остальных, а слайдер громкости ограничен 100% — тихие каналы
            // вытягиваются FFmpeg-фильтром до общей громкости.
            AudioHeader.Text = L.T("Звук", "Audio");
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
            AudioNormRadio.SelectedIndex = settings.AudioNormalization switch
            {
                "Off" => 0,
                "Loudness" => 2,
                _ => 1
            };
        }

        private void BufferSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            UpdateBufferLabel();
        }

        private void UpdateBufferLabel()
        {
            BufferValueText.Text = L.T(
                $"Буфер: {BufferSlider.Value:F0} c (задержка от эфира ~{BufferSlider.Value:F0} c)",
                $"Buffer: {BufferSlider.Value:F0} s (live delay ~{BufferSlider.Value:F0} s)");
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Пишем в каноническую копию AppSettings, а не в загруженную при
            // открытии диалога: избранное/напоминания могли измениться после
            // открытия — устаревшая копия затёрла бы их.
            var appSettings = _viewModel.AppSettings;

            if (DecoderRadio.SelectedIndex >= 0)
            {
                appSettings.DecoderMode = DecoderModes[DecoderRadio.SelectedIndex];
            }

            appSettings.ReadAheadSeconds = (int)Math.Clamp(BufferSlider.Value, 5, 60);

            if (QualityCombo.SelectedItem is ComboBoxItem { Tag: int quality })
            {
                appSettings.PreferredQuality = quality;
            }

            var audioNorm = AudioNormRadio.SelectedIndex >= 0
                ? AudioNormModes[AudioNormRadio.SelectedIndex]
                : null;
            if (!string.IsNullOrEmpty(audioNorm))
            {
                appSettings.AudioNormalization = audioNorm;
            }

            await _settingsService.SaveAsync(appSettings);

            // Переключение аудио фильтров слышно сразу — фильтры заменяются
            // в графе играющего канала, без пересоздания плеера. Следующие
            // каналы получат их ещё при создании (StreamService.CreatePlayerAsync).
            _streamService.ApplyAudioFilters(_viewModel.Player.Player, audioNorm);

            CloseDialog();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CloseDialog();
        }

        private void CloseDialog()
        {
            _hostDialog?.Hide();
        }
    }
}
