using System;
using System.Threading.Tasks;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IptvPlayer.Dialogs
{
    /// <summary>
    /// Раздел интерфейса настроек: язык, тема оформления и действие таймера
    /// сна. Сохраняет в каноническую копию AppSettings (ViewModel.AppSettings),
    /// как SettingsDialog; тема и язык применяются к окну сразу после
    /// сохранения через колбэки, переданные MainPage.
    /// </summary>
    public sealed partial class InterfaceSettingsDialog : UserControl
    {
        private readonly MainPageViewModel _viewModel;
        private readonly ISettingsService _settingsService;
        private readonly Action<string> _applyTheme;

        // Контейнер-ContentDialog создаётся в ShowAsync; кнопки внутри
        // UserControl закрывают его через эту ссылку (искать родителя по
        // визуальному дереву нельзя — им оказывается ContentPresenter
        // шаблона диалога, а не сам ContentDialog).
        private ContentDialog? _hostDialog;

        public InterfaceSettingsDialog(
            MainPageViewModel viewModel,
            ISettingsService settingsService,
            Action<string> applyTheme)
        {
            _viewModel = viewModel;
            _settingsService = settingsService;
            _applyTheme = applyTheme;
            InitializeComponent();
        }

        public async Task ShowAsync(XamlRoot xamlRoot)
        {
            await LoadAsync();
            // Заголовок показывает сам ContentDialog — внутренний TitleText
            // не нужен, иначе заголовок читается дважды.
            TitleText.Visibility = Visibility.Collapsed;

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = L.T("Nastroyki_Interfeysa_Lbl"),
                Content = this
            };
            _hostDialog = dialog;
            await dialog.ShowAsync();
        }

        private async Task LoadAsync()
        {
            var settings = await _settingsService.LoadAsync();

            TitleText.Text = L.T("Nastroyki_Interfeysa_Lbl");
            CancelButton.Content = L.T("Otmena_Lbl");
            SaveButton.Content = L.T("Sokhranit_Lbl");

            // Язык: локализатор поддерживает ru/en.
            LanguageHeader.Text = L.T("YAzyk_Interfeysa_Lbl");
            LanguageHint.Text = L.T("Osnovnye_Teksty_Interfeysa_Perevodyatsya_Na_Letu_Lbl");
            LanguageCombo.Items.Clear();
            LanguageCombo.Items.Add("Русский");
            LanguageCombo.Items.Add("English");
            LanguageCombo.SelectedIndex = L.IsRussian ? 0 : 1;

            // Тема: применяется после сохранения.
            ThemeHeader.Text = L.T("Tema_Interfeysa_Lbl");
            ThemeHint.Text = L.T("Primenyaetsya_Srazu_Posle_Sokhraneniya_Bez_Perezapuska_Lbl");
            ThemeRadio.Items.Clear();
            ThemeRadio.Items.Add(new RadioButton { Content = L.T("Svetlaya"), Tag = "Light" });
            ThemeRadio.Items.Add(new RadioButton { Content = L.T("Temnaya"), Tag = "Dark" });
            ThemeRadio.Items.Add(new RadioButton { Content = L.T("Sistemnaya"), Tag = "Default" });
            ThemeRadio.SelectedIndex = settings.Theme switch
            {
                "Light" => 0,
                "Dark" => 1,
                _ => 2
            };

            // Действие таймера сна по истечении: остановить воспроизведение,
            // закрыть приложение или выключить компьютер (shutdown /s /t 0).
            SleepTimerHeader.Text = L.T("Taymer_Sna_Po_Istechenii_Lbl");
            SleepTimerHint.Text = L.T("Primenyaetsya_K_Uzhe_Vzvedennomu_Taymeru_Deystvie_Lbl");
            SleepTimerActionCombo.Items.Clear();
            foreach (var (label, action) in new[]
                     {
                         (L.T("Ostanovit_Vosproizvedenie"), "Stop"),
                         (L.T("Zakryt_Programmu"), "Exit"),
                         (L.T("Vyklyuchit_Kompyuter"), "Shutdown"),
                     })
            {
                SleepTimerActionCombo.Items.Add(new ComboBoxItem { Content = label, Tag = action });
                if (action == settings.SleepTimerAction)
                {
                    SleepTimerActionCombo.SelectedIndex = SleepTimerActionCombo.Items.Count - 1;
                }
            }
            if (SleepTimerActionCombo.SelectedIndex < 0)
            {
                SleepTimerActionCombo.SelectedIndex = 0;
            }

            // Трей: иконка живёт в трее только пока окно скрыто. Кнопка
            // «Свернуть» и крестик прячут окно в трей (звук продолжает
            // играть); полный выход — через меню иконки.
            MinimizeToTrayToggle.Toggled -= MinimizeToTrayToggle_Toggled;
            MinimizeToTrayToggle.IsOn = settings.MinimizeToTray;
            MinimizeToTrayToggle.Header = L.T("Svorachivat_V_Trey_Pri_Svorachivanii");
            MinimizeToTrayToggle.OnContent = L.T("Vkl");
            MinimizeToTrayToggle.OffContent = L.T("Vykl");
            MinimizeToTrayToggle.Toggled += MinimizeToTrayToggle_Toggled;
            MinimizeToTrayHint.Text = L.T("Knopka_Svernut_Pryachet_Okno_V_Trey");

            CloseToTrayToggle.Toggled -= CloseToTrayToggle_Toggled;
            CloseToTrayToggle.IsOn = settings.CloseToTray;
            CloseToTrayToggle.Header = L.T("Svorachivat_V_Trey_Pri_Zakrytii");
            CloseToTrayToggle.OnContent = L.T("Vkl");
            CloseToTrayToggle.OffContent = L.T("Vykl");
            CloseToTrayToggle.Toggled += CloseToTrayToggle_Toggled;
            CloseToTrayHint.Text = L.T("Krestik_Okna_Pryachet_Ego_V_Trey");

            // Файловый лог (Serilog): применяется сразу через
            // LoggingLevelSwitch, без перезапуска. Вывод в Debug (окно
            // Output студии) остаётся всегда.
            DiagnosticsHeader.Text = L.T("Diagnostika");

            // Полуавтоматическое обновление: фоновая проверка при запуске,
            // скачивание и диалог установки (без установки при записях).
            AutoUpdateToggle.Toggled -= AutoUpdateToggle_Toggled;
            AutoUpdateToggle.IsOn = settings.AutoUpdateEnabled;
            AutoUpdateToggle.Header = L.T("Proveryat_Obnovleniya_Avtomaticheski");
            AutoUpdateToggle.OnContent = L.T("Vkl");
            AutoUpdateToggle.OffContent = L.T("Vykl");
            AutoUpdateToggle.Toggled += AutoUpdateToggle_Toggled;
            AutoUpdateHint.Text = L.T("Posle_Zapuska_Ne_Chashche_Raza_V");

            FileLoggingToggle.Toggled -= FileLoggingToggle_Toggled;
            FileLoggingToggle.IsOn = settings.FileLoggingEnabled;
            FileLoggingToggle.Header = L.T("Faylovyy_Log");
            FileLoggingToggle.OnContent = L.T("Vkl");
            FileLoggingToggle.OffContent = L.T("Vykl");
            FileLoggingToggle.Toggled += FileLoggingToggle_Toggled;
            FileLoggingHint.Text = string.Format(L.T("Zapis_Sobytiy_V_0_Vyklyuchenie_Deystvuet"), App.LogDirectory, App.LogDirectory);
        }

        /// <summary>Автообновление — применяется сразу, персистится в настройках.</summary>
        private async void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _viewModel.AppSettings.AutoUpdateEnabled = AutoUpdateToggle.IsOn;
            await _settingsService.SaveAsync(_viewModel.AppSettings);
        }

        /// <summary>Включение/выключение файлового лога — действует сразу.</summary>
        private void FileLoggingToggle_Toggled(object sender, RoutedEventArgs e)
        {
            var enabled = FileLoggingToggle.IsOn;
            _viewModel.AppSettings.FileLoggingEnabled = enabled;
            App.SetFileLoggingEnabled(enabled);
        }

        /// <summary>Сворачивание в трей — применяется сразу, персистится в настройках.</summary>
        private async void CloseToTrayToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _viewModel.AppSettings.CloseToTray = CloseToTrayToggle.IsOn;
            await _settingsService.SaveAsync(_viewModel.AppSettings);
        }

        /// <summary>Сворачивание по кнопке «Свернуть» — применяется сразу.</summary>
        private async void MinimizeToTrayToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _viewModel.AppSettings.MinimizeToTray = MinimizeToTrayToggle.IsOn;
            await _settingsService.SaveAsync(_viewModel.AppSettings);
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Пишем в каноническую копию AppSettings, а не в загруженную при
            // открытии диалога: избранное/напоминания могли измениться после
            // открытия — устаревшая копия затёрла бы их.
            var appSettings = _viewModel.AppSettings;

            var theme = (ThemeRadio.SelectedItem as RadioButton)?.Tag as string;
            if (string.IsNullOrEmpty(theme))
            {
                theme = "Default";
            }
            appSettings.Theme = theme;
            appSettings.Language = LanguageCombo.SelectedIndex == 1 ? "en" : "ru";

            if (SleepTimerActionCombo.SelectedItem is ComboBoxItem { Tag: string sleepAction })
            {
                appSettings.SleepTimerAction = sleepAction;
            }

            await _settingsService.SaveAsync(appSettings);

            // Тема применяется к окну немедленно — компетенция
            // представления, поэтому MainPage передал колбэк. Язык
            // применяется при следующем запуске (MRT фиксирует тексты
            // при разборе XAML, на лету их не поменять).
            _applyTheme(theme);

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
