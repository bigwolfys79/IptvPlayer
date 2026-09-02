using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer;

/// <summary>
/// Settings dialog handlers.
/// </summary>
public sealed partial class MainPage : Page
{
    private async void PlaybackSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.PlaybackSettingsDialog(
            ViewModel,
            _settingsService,
            _streamService);
        await dialog.ShowAsync(((MenuFlyoutItem)sender).XamlRoot);
    }

    private async void DiagnosticsMenu_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.DiagnosticsDialog(
            show => SetStatsOverlayVisible(show),
            () =>
            {
                _settingsSaveDebounceTimer.Stop();
                _settingsSaveDebounceTimer.Start();
            });
        var settings = ViewModel.AppSettings;
        await dialog.ShowAsync(
            ((MenuFlyoutItem)sender).XamlRoot,
            statsVisible: StatsOverlay.Visibility == Visibility.Visible,
            new Dialogs.DiagnosticsDialog.AppSettingsSnapshot(
                settings.DiagnosticStreamProxy,
                settings.FileLoggingEnabled,
                settings.TempDiagnosticsEnabled));
    }

    private async void InterfaceSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.InterfaceSettingsDialog(
            ViewModel,
            _settingsService,
            ApplyTheme);
        await dialog.ShowAsync(((MenuFlyoutItem)sender).XamlRoot);
    }

    private async void EpgSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.EpgSettingsDialog(
            ViewModel,
            _settingsService);
        await dialog.ShowAsync(((MenuFlyoutItem)sender).XamlRoot);
    }

    private async void PlaylistSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.PlaylistSettingsDialog(
            ViewModel,
            _settingsService,
            _m3uParserService,
            _channelRepository,
            _playlistCacheService,
            App.Services.GetRequiredService<ILogger<Dialogs.PlaylistSettingsDialog>>(),
            SwitchPlaylistAsync);
        await dialog.ShowAsync(((MenuFlyoutItem)sender).XamlRoot);

        // Список/имена плейлистов могли измениться в диалоге — обновляем подменю.
        UpdatePlaylistMenu();
    }

    private async void RecordingSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.RecordingSettingsDialog(ViewModel, _settingsService);
        await dialog.ShowAsync(((MenuFlyoutItem)sender).XamlRoot);
    }

    private async void ParentalSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.ParentalControlDialog(
            ViewModel,
            _settingsService,
            App.Services.GetRequiredService<ILogger<Dialogs.ParentalControlDialog>>());
        await dialog.ShowAsync(((MenuFlyoutItem)sender).XamlRoot);
    }

    private async void LicenseMenu_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.LicenseStatusDialog();
        await dialog.ShowAsync(((FrameworkElement)sender).XamlRoot);
    }

    private async void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        // Установка — тот же сценарий, что при автообновлении (OfferUpdateInstallAsync):
        // согласие пользователя, откладывание при активных записях, тихая установка.
        var dialog = new Dialogs.AboutDialog(_updateService, OfferUpdateInstallAsync);
        await dialog.ShowAsync(((FrameworkElement)sender).XamlRoot);
    }
}
