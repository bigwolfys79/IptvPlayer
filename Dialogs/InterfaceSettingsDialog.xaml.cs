using System;
using System.Threading.Tasks;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IptvPlayer.Dialogs
{


    public sealed partial class InterfaceSettingsDialog : UserControl
    {
        private readonly MainPageViewModel _viewModel;
        private readonly ISettingsService _settingsService;
        private readonly Action<string> _applyTheme;

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


            TitleText.Visibility = Visibility.Collapsed;

            var dialog = new ThemedContentDialog
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


            LanguageHeader.Text = L.T("YAzyk_Interfeysa_Lbl");
            LanguageHint.Text = L.T("Osnovnye_Teksty_Interfeysa_Perevodyatsya_Na_Letu_Lbl");
            LanguageCombo.Items.Clear();
            LanguageCombo.Items.Add("Русский");
            LanguageCombo.Items.Add("English");
            LanguageCombo.SelectedIndex = L.IsRussian ? 0 : 1;


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

            AutoUpdateToggle.Toggled -= AutoUpdateToggle_Toggled;
            AutoUpdateToggle.IsOn = settings.AutoUpdateEnabled;
            AutoUpdateToggle.Header = L.T("Proveryat_Obnovleniya_Avtomaticheski");
            AutoUpdateToggle.OnContent = L.T("Vkl");
            AutoUpdateToggle.OffContent = L.T("Vykl");
            AutoUpdateToggle.Toggled += AutoUpdateToggle_Toggled;
            AutoUpdateHint.Text = L.T("Posle_Zapuska_Ne_Chashche_Raza_V");


            ShowHubOnStartupToggle.Toggled -= ShowHubOnStartupToggle_Toggled;
            ShowHubOnStartupToggle.IsOn = settings.ShowHubOnStartup;
            ShowHubOnStartupToggle.Header = L.T("Pokazyvat_Glavnoe_Menyu_Pri_Zapuske");
            ShowHubOnStartupToggle.OnContent = L.T("Vkl");
            ShowHubOnStartupToggle.OffContent = L.T("Vykl");
            ShowHubOnStartupToggle.Toggled += ShowHubOnStartupToggle_Toggled;
            ShowHubOnStartupHint.Text = L.T("Pri_Vyklyuchenii_Srazu_Otkryvaetsya_Poslednij_Kanal");

        }


        private async void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _viewModel.AppSettings.AutoUpdateEnabled = AutoUpdateToggle.IsOn;
            await _settingsService.SaveAsync(_viewModel.AppSettings);
        }


        private async void ShowHubOnStartupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _viewModel.AppSettings.ShowHubOnStartup = ShowHubOnStartupToggle.IsOn;
            await _settingsService.SaveAsync(_viewModel.AppSettings);
        }


        private async void CloseToTrayToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _viewModel.AppSettings.CloseToTray = CloseToTrayToggle.IsOn;
            await _settingsService.SaveAsync(_viewModel.AppSettings);
        }


        private async void MinimizeToTrayToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _viewModel.AppSettings.MinimizeToTray = MinimizeToTrayToggle.IsOn;
            await _settingsService.SaveAsync(_viewModel.AppSettings);
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {

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

            var languageChanged = !string.Equals(L.Lang, appSettings.Language, StringComparison.OrdinalIgnoreCase);

            // Apply language now that the string cache is cleared on switch
            L.SetLanguage(appSettings.Language);

            _applyTheme(theme);

            CloseDialog();

            if (languageChanged)
            {
                ReloadCurrentPage();
            }
        }

        // Fresh page instance resolves x:Uid strings in the new language
        private static void ReloadCurrentPage()
        {
            try
            {
                if (App.MainWindow is not MainWindow window || window.AppFrame.Content is not { } content)
                {
                    return;
                }

                var frame = window.AppFrame;
                var type = content.GetType();
                frame.Navigate(type);

                // Drop the stale pre-switch instance from the back stack
                if (frame.BackStack.Count > 0 && frame.BackStack[^1].SourcePageType == type)
                {
                    frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Не удалось перезагрузить страницу после смены языка.");
            }
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
