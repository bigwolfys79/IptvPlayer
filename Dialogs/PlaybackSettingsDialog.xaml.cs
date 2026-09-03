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
            VodBufferSlider.ValueChanged += BufferSlider_ValueChanged;
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
                Title = L.T("Nastroyki_Vosproizvedeniya_Lbl"),
                Content = this
            };
            _hostDialog = dialog;
            await dialog.ShowAsync();
        }

        private async Task LoadAsync()
        {
            var settings = await _settingsService.LoadAsync();

            TitleText.Text = L.T("Nastroyki_Vosproizvedeniya_Lbl");
            CancelButton.Content = L.T("Otmena_Lbl");
            SaveButton.Content = L.T("Sokhranit_Lbl");

            // Декодер: аппаратный (с откатом на процессор) или программный.
            DecoderHeader.Text = L.T("Dekodirovanie_Video_Lbl");
            DecoderHint.Text = L.T("Primenitsya_Pri_Sleduyushchem_Pereklyuchenii_Kanala_Lbl");
            DecoderRadio.Items.Clear();
            var hwRadio = new RadioButton
            {
                Content = L.T("Apparatnoe_GPU_S_Otkatom_Na_Protsessor"),
                Tag = "Hardware"
            };
            var swRadio = new RadioButton
            {
                Content = L.T("Programmnoe_Protsessor"),
                Tag = "Software"
            };
            ToolTipService.SetToolTip(hwRadio, L.T("Tip_HardwareDecoder"));
            ToolTipService.SetToolTip(swRadio, L.T("Tip_SoftwareDecoder"));
            DecoderRadio.Items.Add(hwRadio);
            DecoderRadio.Items.Add(swRadio);
            DecoderRadio.SelectedIndex =
                string.Equals(settings.DecoderMode, "Hardware", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

            // Буфер видео.
            BufferHeader.Text = L.T("Bufer_TV_Kanalov_Pryamoy_Efir");
            BufferHint.Text = L.T("Glubina_Bufera_Dlya_TV_Kanalov_Pryamogo");
            BufferSlider.Value = Math.Clamp(settings.ReadAheadSeconds, 5, 60);
            VodBufferHeader.Text = L.T("Bufer_Videoteki_Filmy_Portala");
            VodBufferHint.Text = L.T("Tot_Zhe_Bufer_No_Dlya_Filmov");
            VodBufferSlider.Value = Math.Clamp(settings.VodReadAheadSeconds, 2, 15);
            UpdateBufferLabel();

            // Качество видео.
            QualityHeader.Text = L.T("Kachestvo_Video_Lbl");
            QualityHint.Text = L.T("Maksimalnoe_Kachestvo_Potoka_Ili_Ogranichenie_Razresheniya_Lbl");
            QualityCombo.Items.Clear();
            foreach (var (label, height) in new[]
                     {
                         (L.T("Avto_Maksimalnoe"), 0),
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
            AudioHeader.Text = L.T("Zvuk_Lbl");
            AudioNormHeader.Text = L.T("Normalizatsiya_Gromkosti_Lbl");
            AudioNormHint.Text = L.T("Podtyagivaet_Tikhie_Kanaly_K_Obshchemu_Urovnyu_Lbl");
            var normOff = new RadioButton { Content = L.T("Vyklyuchena"), Tag = "Off" };
            var normDynamic = new RadioButton
            {
                Content = L.T("Dinamicheskaya_Usilivaet_Tikhie_Kanaly"),
                Tag = "Dynamic"
            };
            var normLoudness = new RadioButton
            {
                Content = L.T("Postoyannaya_Gromkost_EBU_R128"),
                Tag = "Loudness"
            };
            ToolTipService.SetToolTip(normDynamic, L.T("Tip_Dynaudnorm"));
            ToolTipService.SetToolTip(normLoudness, L.T("Tip_Loudnorm"));
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
            BufferValueText.Text = string.Format(L.T("TV_Bufer_0_C_Zaderzhka_Ot"), $"{BufferSlider.Value:F0}", $"{BufferSlider.Value:F0}", $"{BufferSlider.Value:F0}", $"{BufferSlider.Value:F0}");
            VodBufferValueText.Text = string.Format(L.T("Videoteka_Bufer_0_C"), $"{VodBufferSlider.Value:F0}", $"{VodBufferSlider.Value:F0}");
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
            appSettings.VodReadAheadSeconds = (int)Math.Clamp(VodBufferSlider.Value, 2, 15);

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
            // Loudness разрешён только для VOD/файлов: на живом эфире
            // StreamService сам подменит его на Dynamic.
            _streamService.ApplyAudioFilters(_viewModel.Player.Player, audioNorm,
                _viewModel.Player.IsVodPlaying);

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
