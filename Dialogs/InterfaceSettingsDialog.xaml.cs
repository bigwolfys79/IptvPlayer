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
        private readonly Action _applyLanguage;

        // Контейнер-ContentDialog создаётся в ShowAsync; кнопки внутри
        // UserControl закрывают его через эту ссылку (искать родителя по
        // визуальному дереву нельзя — им оказывается ContentPresenter
        // шаблона диалога, а не сам ContentDialog).
        private ContentDialog? _hostDialog;

        public InterfaceSettingsDialog(
            MainPageViewModel viewModel,
            ISettingsService settingsService,
            Action<string> applyTheme,
            Action applyLanguage)
        {
            _viewModel = viewModel;
            _settingsService = settingsService;
            _applyTheme = applyTheme;
            _applyLanguage = applyLanguage;
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
                Title = L.T("Настройки интерфейса", "Interface settings"),
                Content = this
            };
            _hostDialog = dialog;
            await dialog.ShowAsync();
        }

        private async Task LoadAsync()
        {
            var settings = await _settingsService.LoadAsync();

            TitleText.Text = L.T("Настройки интерфейса", "Interface settings");
            CancelButton.Content = L.T("Отмена", "Cancel");
            SaveButton.Content = L.T("Сохранить", "Save");

            // Язык: локализатор поддерживает ru/en.
            LanguageHeader.Text = L.T("Язык интерфейса", "Interface language");
            LanguageHint.Text = L.T(
                "Основные тексты интерфейса переводятся на лету.",
                "Main interface texts are translated on the fly.");
            LanguageCombo.Items.Clear();
            LanguageCombo.Items.Add("Русский");
            LanguageCombo.Items.Add("English");
            LanguageCombo.SelectedIndex = L.IsRussian ? 0 : 1;

            // Тема: применяется после сохранения.
            ThemeHeader.Text = L.T("Тема интерфейса", "Interface theme");
            ThemeHint.Text = L.T(
                "Применяется сразу после сохранения, без перезапуска.",
                "Applied right after saving, no restart needed.");
            ThemeRadio.Items.Clear();
            ThemeRadio.Items.Add(new RadioButton { Content = L.T("Светлая", "Light"), Tag = "Light" });
            ThemeRadio.Items.Add(new RadioButton { Content = L.T("Тёмная", "Dark"), Tag = "Dark" });
            ThemeRadio.Items.Add(new RadioButton { Content = L.T("Системная", "System"), Tag = "Default" });
            ThemeRadio.SelectedIndex = settings.Theme switch
            {
                "Light" => 0,
                "Dark" => 1,
                _ => 2
            };

            // Действие таймера сна по истечении: остановить воспроизведение,
            // закрыть приложение или выключить компьютер (shutdown /s /t 0).
            SleepTimerHeader.Text = L.T("Таймер сна: по истечении", "Sleep timer: when it ends");
            SleepTimerHint.Text = L.T(
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
            MinimizeToTrayToggle.Header = L.T("Сворачивать в трей при сворачивании", "Minimize to tray on minimize");
            MinimizeToTrayToggle.OnContent = L.T("Вкл", "On");
            MinimizeToTrayToggle.OffContent = L.T("Выкл", "Off");
            MinimizeToTrayToggle.Toggled += MinimizeToTrayToggle_Toggled;
            MinimizeToTrayHint.Text = L.T(
                "Кнопка «Свернуть» прячет окно в трей вместо панели задач.",
                "The minimize button hides the window to the tray instead of the taskbar.");

            CloseToTrayToggle.Toggled -= CloseToTrayToggle_Toggled;
            CloseToTrayToggle.IsOn = settings.CloseToTray;
            CloseToTrayToggle.Header = L.T("Сворачивать в трей при закрытии", "Minimize to tray on close");
            CloseToTrayToggle.OnContent = L.T("Вкл", "On");
            CloseToTrayToggle.OffContent = L.T("Выкл", "Off");
            CloseToTrayToggle.Toggled += CloseToTrayToggle_Toggled;
            CloseToTrayHint.Text = L.T(
                "Крестик окна прячет его в трей — воспроизведение продолжается. Полный выход — правый клик по иконке в трее → «Выход».",
                "The close button hides the window to the tray — playback continues. To quit fully, right-click the tray icon → \"Exit\".");

            // Файловый лог (Serilog): применяется сразу через
            // LoggingLevelSwitch, без перезапуска. Вывод в Debug (окно
            // Output студии) остаётся всегда.
            DiagnosticsHeader.Text = L.T("Диагностика", "Diagnostics");

            // Полуавтоматическое обновление: фоновая проверка при запуске,
            // скачивание и диалог установки (без установки при записях).
            AutoUpdateToggle.Toggled -= AutoUpdateToggle_Toggled;
            AutoUpdateToggle.IsOn = settings.AutoUpdateEnabled;
            AutoUpdateToggle.Header = L.T("Проверять обновления автоматически", "Check for updates automatically");
            AutoUpdateToggle.OnContent = L.T("Вкл", "On");
            AutoUpdateToggle.OffContent = L.T("Выкл", "Off");
            AutoUpdateToggle.Toggled += AutoUpdateToggle_Toggled;
            AutoUpdateHint.Text = L.T(
                "После запуска (не чаще раза в сутки) приложение само проверит GitHub Releases, скачает установщик и предложит установить. Пока идут записи, установка не запускается.",
                "After startup (at most once a day) the app checks GitHub Releases by itself, downloads the installer and offers to install. Installation never starts while recordings are running.");

            FileLoggingToggle.Toggled -= FileLoggingToggle_Toggled;
            FileLoggingToggle.IsOn = settings.FileLoggingEnabled;
            FileLoggingToggle.Header = L.T("Файловый лог", "File log");
            FileLoggingToggle.OnContent = L.T("Вкл", "On");
            FileLoggingToggle.OffContent = L.T("Выкл", "Off");
            FileLoggingToggle.Toggled += FileLoggingToggle_Toggled;
            FileLoggingHint.Text = L.T(
                $"Запись событий в {App.LogDirectory}. Выключение действует сразу, перезапуск не нужен.",
                $"Writes events to {App.LogDirectory}. Turning it off takes effect immediately, no restart required.");
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

            // Тема и язык применяются к окну немедленно — компетенция
            // представления, поэтому MainPage передал колбэки.
            L.SetLanguage(appSettings.Language);
            _applyTheme(theme);
            _applyLanguage();

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
