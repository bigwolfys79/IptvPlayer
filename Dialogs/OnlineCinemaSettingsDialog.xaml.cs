using System;
using System.Threading.Tasks;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer.Dialogs
{
    // Online-cinema settings: master switch, background collection/list refresh
    // intervals and the parsing-method gates (curl → HttpClient → WebView2).
    // Toggles save instantly, the numeric fields apply on Save
    public sealed partial class OnlineCinemaSettingsDialog : UserControl
    {
        private readonly MainPageViewModel _viewModel;
        private readonly ISettingsService _settingsService;

        private ContentDialog? _hostDialog;

        public OnlineCinemaSettingsDialog(MainPageViewModel viewModel, ISettingsService settingsService)
        {
            _viewModel = viewModel;
            _settingsService = settingsService;
            InitializeComponent();
        }

        public async Task ShowAsync(XamlRoot xamlRoot)
        {
            await LoadAsync();

            TitleText.Visibility = Visibility.Collapsed;

            var dialog = new ThemedContentDialog
            {
                XamlRoot = xamlRoot,
                Title = L.T("OnlineCinema_Settings_Lbl"),
                Content = this
            };
            _hostDialog = dialog;
            await DialogQueue.ShowAsync(dialog);
        }

        private async Task LoadAsync()
        {
            var settings = await _settingsService.LoadAsync();

            CancelButton.Content = L.T("Otmena_Lbl");
            SaveButton.Content = L.T("Sokhranit_Lbl");

            CinemaEnabledToggle.Toggled -= CinemaEnabledToggle_Toggled;
            CinemaEnabledToggle.IsOn = settings.OnlineCinemaEnabled;
            CinemaEnabledToggle.Header = L.T("OnlineCinema_Toggle");
            CinemaEnabledToggle.OnContent = L.T("Vkl");
            CinemaEnabledToggle.OffContent = L.T("Vykl");
            CinemaEnabledToggle.Toggled += CinemaEnabledToggle_Toggled;
            CinemaEnabledHint.Text = L.T("OnlineCinema_Toggle_Hint");

            BackgroundCollectToggle.Toggled -= BackgroundCollectToggle_Toggled;
            BackgroundCollectToggle.IsOn = settings.OnlineCinemaBackgroundCollectEnabled;
            BackgroundCollectToggle.Header = L.T("OnlineCinema_Background_Collect");
            BackgroundCollectToggle.OnContent = L.T("Vkl");
            BackgroundCollectToggle.OffContent = L.T("Vykl");
            BackgroundCollectToggle.Toggled += BackgroundCollectToggle_Toggled;
            BackgroundCollectHint.Text = L.T("OnlineCinema_Background_Collect_Hint");

            RefreshHeader.Text = L.T("OnlineCinema_List_Refresh");
            RefreshHint.Text = L.T("OnlineCinema_List_Refresh_Hint");
            RefreshBox.Value = settings.OnlineCinemaListRefreshMinutes;

            CollectHeader.Text = L.T("OnlineCinema_Collect_Interval");
            CollectHint.Text = L.T("OnlineCinema_Collect_Interval_Hint");
            CollectBox.Value = settings.OnlineCinemaCollectIntervalSeconds;

            ParsingHeader.Text = L.T("OnlineCinema_Parsing_Header");
            ParsingHint.Text = L.T("OnlineCinema_Parsing_Hint");

            UseCurlToggle.Toggled -= MethodToggle_Toggled;
            UseCurlToggle.IsOn = settings.OnlineCinemaUseCurl;
            UseCurlToggle.Header = L.T("OnlineCinema_Use_Curl");
            UseCurlToggle.OnContent = L.T("Vkl");
            UseCurlToggle.OffContent = L.T("Vykl");
            UseCurlToggle.Toggled += MethodToggle_Toggled;
            UseCurlHint.Text = L.T("OnlineCinema_Use_Curl_Hint");

            UseHttpClientToggle.Toggled -= MethodToggle_Toggled;
            UseHttpClientToggle.IsOn = settings.OnlineCinemaUseHttpClient;
            UseHttpClientToggle.Header = L.T("OnlineCinema_Use_HttpClient");
            UseHttpClientToggle.OnContent = L.T("Vkl");
            UseHttpClientToggle.OffContent = L.T("Vykl");
            UseHttpClientToggle.Toggled += MethodToggle_Toggled;
            UseHttpClientHint.Text = L.T("OnlineCinema_Use_HttpClient_Hint");

            UseWebView2Toggle.Toggled -= MethodToggle_Toggled;
            UseWebView2Toggle.IsOn = settings.OnlineCinemaUseWebView2;
            UseWebView2Toggle.Header = L.T("OnlineCinema_Use_WebView2");
            UseWebView2Toggle.OnContent = L.T("Vkl");
            UseWebView2Toggle.OffContent = L.T("Vykl");
            UseWebView2Toggle.Toggled += MethodToggle_Toggled;
            UseWebView2Hint.Text = L.T("OnlineCinema_Use_WebView2_Hint");
        }

        private async void CinemaEnabledToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _viewModel.AppSettings.OnlineCinemaEnabled = CinemaEnabledToggle.IsOn;
            await _settingsService.SaveAsync(_viewModel.AppSettings);
        }

        // Background collect gate applies instantly — the collect timer reads
        // it on every tick, the on-open refresh checks it before starting
        private async void BackgroundCollectToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _viewModel.AppSettings.OnlineCinemaBackgroundCollectEnabled = BackgroundCollectToggle.IsOn;
            await _settingsService.SaveAsync(_viewModel.AppSettings);
        }

        // Parsing-method gates apply instantly — the services read the shared
        // AppSettings instance on every request
        private async void MethodToggle_Toggled(object sender, RoutedEventArgs e)
        {
            _viewModel.AppSettings.OnlineCinemaUseCurl = UseCurlToggle.IsOn;
            _viewModel.AppSettings.OnlineCinemaUseHttpClient = UseHttpClientToggle.IsOn;
            _viewModel.AppSettings.OnlineCinemaUseWebView2 = UseWebView2Toggle.IsOn;
            await _settingsService.SaveAsync(_viewModel.AppSettings);
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var appSettings = _viewModel.AppSettings;
            appSettings.OnlineCinemaListRefreshMinutes =
                (int)Math.Clamp(RefreshBox.Value, 0, 1440);
            appSettings.OnlineCinemaCollectIntervalSeconds =
                (int)Math.Clamp(CollectBox.Value, 30, 7200);

            await _settingsService.SaveAsync(appSettings);
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
