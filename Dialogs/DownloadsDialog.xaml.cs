using System;
using System.IO;
using System.Threading.Tasks;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer.Dialogs
{
    // Downloads menu: every online-cinema download with its progress and
    // pause/resume/cancel + play-file/show-in-folder buttons. Lives as an
    // item in the player settings menu; the manager is a singleton, so the
    // list keeps updating while downloads continue behind the dialog
    public sealed partial class DownloadsDialog : UserControl
    {
        private readonly OnlineCinemaDownloadManager _manager;
        private readonly MainPageViewModel _viewModel;

        private ContentDialog? _hostDialog;

        public OnlineCinemaDownloadManager Manager => _manager;

        public DownloadsDialog(OnlineCinemaDownloadManager manager, MainPageViewModel viewModel)
        {
            _manager = manager;
            _viewModel = viewModel;
            InitializeComponent();
        }

        public async Task ShowAsync(XamlRoot xamlRoot)
        {
            Localize();
            UpdateEmptyState();

            _manager.Items.CollectionChanged += (_, _) => UpdateEmptyState();

            var dialog = new ThemedContentDialog
            {
                XamlRoot = xamlRoot,
                Title = L.T("OnlineCinema_Zagruzki_Text"),
                Content = this,
                CloseButtonText = L.T("Zakryt_Lbl")
            };
            _hostDialog = dialog;
            await DialogQueue.ShowAsync(dialog);
        }

        private void Localize()
        {
            EmptyText.Text = L.T("DownloadsDialog_Pusto");
            ClearButton.Content = L.T("OnlineCinema_Ochistit_Spisok");
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            // Drops only the finished rows — the files stay on disk
            _manager.ClearCompleted();
        }

        // Play the downloaded file inside the app (switches playback, the
        // previous film stops) — same flow as opening a local video
        private async void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not Services.OnlineCinemaDownloadItem item ||
                !File.Exists(item.TargetPath))
            {
                return;
            }

            _hostDialog?.Hide();

            var file = LocalVideoFileService.FromPath(item.TargetPath);
            var channel = LocalVideoFileService.CreateChannel(file);
            _viewModel.SelectedChannel = channel;
            var resume = await _viewModel.OfferLocalFileResumeAsync(file.Path, file.Title);
            await _viewModel.Player.StartPlaybackAsync(channel, channel.StreamUrl!, archiveEntry: null,
                isVod: true, resumePosition: resume);
        }

        private void UpdateEmptyState()
        {
            DispatcherQueue.TryEnqueue(() =>
                EmptyText.Visibility = _manager.Items.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed);
        }
    }
}
