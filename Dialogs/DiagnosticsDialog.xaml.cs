using System;
using System.Threading.Tasks;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer.Dialogs
{


    public sealed partial class DiagnosticsDialog : UserControl
    {

        private readonly Action<bool> _setStatsOverlay;
        private readonly Action _saveSettingsDebounced;

        private ContentDialog? _hostDialog;

        public DiagnosticsDialog(Action<bool> setStatsOverlay, Action saveSettingsDebounced)
        {
            _setStatsOverlay = setStatsOverlay;
            _saveSettingsDebounced = saveSettingsDebounced;
            InitializeComponent();
        }

        public Task ShowAsync(XamlRoot xamlRoot, bool statsVisible, AppSettingsSnapshot settings)
        {
            TitleText.Text = L.T("Diagnostika_Lbl");

            // Unsubscribe before init: XAML attribute already subscribes, and re-Shown
            // dialogs must not re-fire handlers on the IsOn assignments below
            StatsToggle.Toggled -= StatsToggle_Toggled;
            ProxyToggle.Toggled -= ProxyToggle_Toggled;
            FileLogToggle.Toggled -= FileLogToggle_Toggled;
            TempDiagToggle.Toggled -= TempDiagToggle_Toggled;

            StatsToggle.IsOn = statsVisible;
            ProxyToggle.IsOn = settings.DiagnosticStreamProxy;
            FileLogToggle.IsOn = settings.FileLoggingEnabled;
            TempDiagToggle.IsOn = settings.TempDiagnosticsEnabled;

            StatsToggle.Toggled += StatsToggle_Toggled;
            ProxyToggle.Toggled += ProxyToggle_Toggled;
            FileLogToggle.Toggled += FileLogToggle_Toggled;
            TempDiagToggle.Toggled += TempDiagToggle_Toggled;

            StatsToggle.Header = L.T("Statistika_Potoka");
            ProxyToggle.Header = L.T("Diagnosticheskiy_Proksi");
            FileLogToggle.Header = L.T("Faylovyy_Log");
            TempDiagToggle.Header = L.T("Vremennaya_Diagnostika");

            StatsHint.Text = L.T("Statistika_Potoka_Hint");
            ProxyHint.Text = L.T("Proksi_Hint");
            FileLogHint.Text = L.T("Faylovyy_Log_Hint");
            TempDiagHint.Text = L.T("Vremennaya_Diagnostika_Hint");

            _hostDialog = new ThemedContentDialog
            {
                Content = this,
                XamlRoot = xamlRoot,
                CloseButtonText = L.T("Zakryt_Lbl"),
            };
            return DialogQueue.ShowAsync(_hostDialog);
        }

        private void StatsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _setStatsOverlay(StatsToggle.IsOn);
            _saveSettingsDebounced();
        }

        private void ProxyToggle_Toggled(object sender, RoutedEventArgs e)
        {
            App.Services.GetRequiredService<MainPageViewModel>().AppSettings.DiagnosticStreamProxy = ProxyToggle.IsOn;
            _saveSettingsDebounced();
        }

        private void FileLogToggle_Toggled(object sender, RoutedEventArgs e)
        {
            var vm = App.Services.GetRequiredService<MainPageViewModel>();
            vm.AppSettings.FileLoggingEnabled = FileLogToggle.IsOn;
            App.SetFileLoggingEnabled(FileLogToggle.IsOn);
            _saveSettingsDebounced();
        }

        private void TempDiagToggle_Toggled(object sender, RoutedEventArgs e)
        {
            var vm = App.Services.GetRequiredService<MainPageViewModel>();
            vm.AppSettings.TempDiagnosticsEnabled = TempDiagToggle.IsOn;
            App.TempDiagnosticsEnabled = TempDiagToggle.IsOn;
            _saveSettingsDebounced();
        }


        public sealed record AppSettingsSnapshot(
            bool DiagnosticStreamProxy,
            bool FileLoggingEnabled,
            bool TempDiagnosticsEnabled);
    }
}
