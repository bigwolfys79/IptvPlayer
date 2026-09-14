using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer.Dialogs
{


    public class PlaylistListItem
    {
        public PlaylistSource Playlist { get; set; } = new();

        public bool IsActive { get; set; }


        public bool IsEditing { get; set; }


        public bool IsEpgExpanded { get; set; }

        private Visibility ToVisibility(bool visible) =>
            visible ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ActiveMarkVisibility => ToVisibility(IsActive);

        public Visibility ActivateButtonVisibility => ToVisibility(!IsActive);

        public Visibility ViewVisibility => ToVisibility(!IsEditing);

        public Visibility EditVisibility => ToVisibility(IsEditing);

        public Visibility EpgSectionVisibility => ToVisibility(IsEpgExpanded);
    }


    public sealed partial class PlaylistSettingsDialog : UserControl
    {
        private readonly MainPageViewModel _viewModel;
        private readonly ISettingsService _settingsService;
        private readonly IChannelRepository _channelRepository;
        private readonly IPlaylistCacheService _playlistCacheService;
        private readonly ILogger<PlaylistSettingsDialog> _logger;
        private readonly Func<PlaylistSource, Task> _switchPlaylist;

        private ContentDialog? _hostDialog;


        private PlaylistSource? _renamingPlaylist;


        private PlaylistSource? _epgExpandedPlaylist;

        public ObservableCollection<PlaylistListItem> PlaylistItems { get; } = new();

        public PlaylistSettingsDialog(
            MainPageViewModel viewModel,
            ISettingsService settingsService,
            IM3UParserService m3uParserService,
            IChannelRepository channelRepository,
            IPlaylistCacheService playlistCacheService,
            ILogger<PlaylistSettingsDialog> logger,
            Func<PlaylistSource, Task> switchPlaylist,
            IXmlTvService xmlTvService,
            Func<Task>? reloadActivePlaylist = null)
        {
            _viewModel = viewModel;
            _settingsService = settingsService;
            _channelRepository = channelRepository;
            _playlistCacheService = playlistCacheService;
            _logger = logger;
            _switchPlaylist = switchPlaylist;
            _xmlTvService = xmlTvService;
            _reloadActivePlaylist = reloadActivePlaylist;
            InitializeComponent();
        }

        private readonly Func<Task>? _reloadActivePlaylist;

        private readonly IXmlTvService _xmlTvService;

        public async Task ShowAsync(XamlRoot xamlRoot)
        {
            await LoadAsync();


            TitleText.Visibility = Visibility.Collapsed;

            var dialog = new ThemedContentDialog
            {
                XamlRoot = xamlRoot,
                Title = L.T("Pleylist_Lbl"),
                Content = this
            };
            _hostDialog = dialog;
            await dialog.ShowAsync();
        }

        private async Task LoadAsync()
        {
            var settings = await _settingsService.LoadAsync();

            TitleText.Text = L.T("Pleylist_Lbl");
            PlaylistHeader.Text = L.T("Pleylisty_Lbl");
            PlaylistHint.Text = L.T("Kanaly_V_Spiske_Iz_Aktivnogo_Pleylista");
            AddPlaylistHeader.Text = L.T("Dobavit_Pleylist_Lbl");
            PlaylistNameBox.PlaceholderText = L.T("Imya_Neobyazatelno_Lbl");
            PlaylistUrlBox.PlaceholderText = L.T("URL_Pleylista_M3U_M3U8_Lbl");
            PortalKeyBox.PlaceholderText = L.T("Klyuch_Portala_Portal_Key");
            PortalKeyBox.Header = L.T("Klyuch_Portala_Lbl");
            AddPlaylistButton.Content = L.T("Dobavit_Lbl");
            AddPlaylistFileButton.Content = L.T("Vybrat_Fayl_Lbl");
            PlaylistRefreshHeader.Text = L.T("CHastota_Obnovleniya_Pleylistov_Lbl");
            PlaylistRefreshHint.Text = L.T("Kak_Chasto_Pri_Zapuske_Perekachivat_Aktivnyy_Lbl");
            CloseButton.Content = L.T("Gotovo_Lbl");
            TransferHeader.Text = L.T("Perenos_Nastroek_Lbl");
            RestoreHeader.Text = L.T("Vosstanovlenie_Kanalov");
            RestoreHint.Text = L.T("Vosstanovlenie_Kanalov_Hint");
            RestoreChannelsButton.Content = L.T("Vosstanovit_Udalennye_Kanaly");
            ExportSettingsButton.Content = L.T("Eksportirovat_Lbl");
            ImportSettingsButton.Content = L.T("Importirovat_Lbl");

            PlaylistUrlBox.Text = string.Empty;
            PlaylistNameBox.Text = string.Empty;
            PortalKeyBox.Text = string.Empty;
            PlaylistTypeCombo.Items.Clear();
            PlaylistTypeCombo.Items.Add(new ComboBoxItem { Content = L.T("Pleylist_M3U_M3U8"), Tag = "m3u" });
            PlaylistTypeCombo.Items.Add(new ComboBoxItem { Content = L.T("Video_Portal"), Tag = "portal" });
            PlaylistTypeCombo.SelectedIndex = 0;
            UpdatePlaylistTypeUi();
            PlaylistStatusText.Visibility = Visibility.Collapsed;

            RebuildPlaylistItems();

            PlaylistRefreshCombo.Items.Clear();
            foreach (var (label, days) in new[]
                     {
                         (L.T("Kazhdyy_Den"), 1),
                         (L.T("Kazhdye_3_Dnya"), 3),
                         (L.T("Kazhduyu_Nedelyu"), 7),
                         (L.T("Nikogda_Tolko_Pri_Dobavlenii"), 0),
                     })
            {
                PlaylistRefreshCombo.Items.Add(new ComboBoxItem { Content = label, Tag = days });
                if (days == settings.PlaylistRefreshDays)
                {
                    PlaylistRefreshCombo.SelectedIndex = PlaylistRefreshCombo.Items.Count - 1;
                }
            }
            if (PlaylistRefreshCombo.SelectedIndex < 0)
            {
                PlaylistRefreshCombo.SelectedIndex = 0;
            }
        }

        private void RebuildPlaylistItems()
        {
            var activeId = _viewModel.AppSettings.ActivePlaylistId;
            PlaylistItems.Clear();
            foreach (var playlist in _viewModel.AppSettings.Playlists)
            {
                PlaylistItems.Add(new PlaylistListItem
                {
                    Playlist = playlist,
                    IsActive = playlist.Id == activeId,
                    IsEditing = ReferenceEquals(playlist, _renamingPlaylist),
                    IsEpgExpanded = ReferenceEquals(playlist, _epgExpandedPlaylist)
                });
            }
        }

        private void RenamePlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: PlaylistListItem item })
            {
                return;
            }

            _renamingPlaylist = ReferenceEquals(_renamingPlaylist, item.Playlist) ? null : item.Playlist;
            RebuildPlaylistItems();

            if (_renamingPlaylist != null && PlaylistsList.FindName("NameEditBox") is TextBox box)
            {
                box.Text = _renamingPlaylist.Name;
                box.SelectAll();
                _ = box.Focus(FocusState.Programmatic);
            }
        }


        private static (PlaylistSource Playlist, EPGSource Source)? FindEpgSourceOwner(object sender)
        {
            if (sender is not FrameworkElement element)
            {
                return null;
            }

            var source = element.DataContext as EPGSource;
            var node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element) as FrameworkElement;
            while (node != null && node.DataContext is not PlaylistListItem)
            {
                node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node) as FrameworkElement;
            }

            return node?.DataContext is PlaylistListItem item && source != null
                ? (item.Playlist, source)
                : null;
        }

        private void EpgSectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: PlaylistListItem item })
            {
                _epgExpandedPlaylist = ReferenceEquals(_epgExpandedPlaylist, item.Playlist) ? null : item.Playlist;
                RebuildPlaylistItems();
            }
        }

        private async void PlaylistEpgSourceAddButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: PlaylistListItem item })
            {
                return;
            }


            var box = (sender as FrameworkElement)?.Parent is StackPanel row
                ? row.Children.OfType<TextBox>().FirstOrDefault()
                : null;
            await AddEpgSourceAsync(item.Playlist, box?.Text?.Trim(), box);
        }

        private async void PlaylistEpgUrlBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter &&
                sender is TextBox { Text: { Length: > 0 } } box &&
                box.DataContext is PlaylistListItem item)
            {
                await AddEpgSourceAsync(item.Playlist, box.Text.Trim(), box);
                e.Handled = true;
            }
        }


        private async Task AddEpgSourceAsync(PlaylistSource playlist, string? url, TextBox? box)
        {
            if (string.IsNullOrEmpty(url))
            {
                SetPlaylistStatus(L.T("Vvedite_URL_Istochnika_EPG"));
                return;
            }

            if (playlist.EpgSources.Any(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                SetPlaylistStatus(L.T("Istochnik_EPG_Uzhe_Dobavlen"));
                return;
            }

            SetPlaylistStatus(L.T("Proverka_Istochnika_EPG"));
            var error = await _xmlTvService.ValidateEpgSourceAsync(url);
            if (error != null)
            {
                SetPlaylistStatus(string.Format(L.T("Epg_Istochnik_Oshibka_0"), error));
                return;
            }

            playlist.EpgSources.Add(new EPGSource { Url = url, IsEnabled = true });
            if (box != null)
            {
                box.Text = string.Empty;
            }
            SetPlaylistStatus(string.Format(L.T("Epg_Istochnik_Dobavlen_0"), url));
            await PlaylistEpgSourcesChangedAsync(playlist);
        }


        private void EpgSourceStatusText_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBlock text || text.DataContext is not EPGSource source)
            {
                return;
            }

            if (!string.IsNullOrEmpty(source.LastError))
            {
                text.Text = "⚠ " + string.Format(L.T("Epg_Istochnik_Oshibka_0"), source.LastError);
            }
            else if (source.LastSuccessAt is { } ok)
            {
                text.Text = "✓ " + string.Format(L.T("Epg_Istochnik_Uspeh_0"), ok.LocalDateTime);
            }
            else
            {
                text.Text = string.Empty;
            }
        }

        private async void PlaylistEpgSourceRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (FindEpgSourceOwner(sender) is not { } owner)
            {
                return;
            }

            var confirmed = await ConfirmAsync(
                L.T("Udalit_Istochnik_EPG_Lbl"),
                string.Format(L.T("Udalit_Istochnik_EPG_Vopros_0"), owner.Source.Url),
                L.T("Udalit_Lbl"));
            if (!confirmed)
            {
                return;
            }

            owner.Playlist.EpgSources.Remove(owner.Source);
            await PlaylistEpgSourcesChangedAsync(owner.Playlist);
        }

        private async void PlaylistEpgSource_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (FindEpgSourceOwner(sender) is { } owner)
            {
                await PlaylistEpgSourcesChangedAsync(owner.Playlist);
            }
        }


        private async Task PlaylistEpgSourcesChangedAsync(PlaylistSource playlist)
        {
            await _settingsService.SaveAsync(_viewModel.AppSettings);
            RebuildPlaylistItems();

            if (playlist.Id == _viewModel.AppSettings.ActivePlaylistId)
            {
                _ = ReloadActivePlaylistEpgAsync();
            }
        }

        private async Task ReloadActivePlaylistEpgAsync()
        {
            try
            {
                await _viewModel.EpgViewModel.ReloadForPlaylistAsync();
                _viewModel.ApplyReminderFlags();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Перезагрузка EPG после изменения источников плейлиста.");
            }
        }

        private async void SavePlaylistNameButton_Click(object sender, RoutedEventArgs e)
        {


            var box = (sender as FrameworkElement)?.Parent is StackPanel panel
                ? panel.Children.OfType<TextBox>().FirstOrDefault()
                : null;

            if (sender is FrameworkElement { DataContext: PlaylistListItem item } &&
                item.IsEditing)
            {
                await SavePlaylistNameAsync(item, box?.Text);
            }
        }

        private async void NameEditBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter &&
                sender is TextBox enterBox &&
                enterBox.DataContext is PlaylistListItem enterItem &&
                enterItem.IsEditing)
            {
                await SavePlaylistNameAsync(enterItem, enterBox.Text);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape && _renamingPlaylist != null)
            {
                _renamingPlaylist = null;
                RebuildPlaylistItems();
                e.Handled = true;
            }
        }


        private async Task SavePlaylistNameAsync(PlaylistListItem item, string? enteredName)
        {
            var newName = enteredName?.Trim();
            if (!string.IsNullOrEmpty(newName) && !string.Equals(newName, item.Playlist.Name, StringComparison.Ordinal))
            {
                item.Playlist.Name = newName;
                await _settingsService.SaveAsync(_viewModel.AppSettings);
                SetPlaylistStatus(string.Format(L.T("Pleylist_Pereimenovan_V_0"), item.Playlist.Name, item.Playlist.Name));
            }

            _renamingPlaylist = null;
            RebuildPlaylistItems();
        }

        private async void AddPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            var url = PlaylistUrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                SetPlaylistStatus(L.T("Vvedite_URL_Pleylista"));
                return;
            }

            var isPortal = IsPortalTypeSelected;
            var portalKey = PortalKeyBox.Text.Trim();
            if (isPortal)
            {

                var match = System.Text.RegularExpressions.Regex.Match(
                    url,
                    @"^portal::\[key:([^\]]+)\]\s*(https?://.+)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    portalKey = match.Groups[1].Value;
                    url = match.Groups[2].Value.TrimEnd('/');
                }

                if (string.IsNullOrEmpty(portalKey))
                {
                    SetPlaylistStatus(L.T("Vvedite_Klyuch_Portala"));
                    return;
                }
            }

            await AddPlaylistAsync(url, isPortal ? "portal" : "m3u", portalKey);
        }

        private bool IsPortalTypeSelected =>
            PlaylistTypeCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
            tag == "portal";


        private void UpdatePlaylistTypeUi()
        {
            var isPortal = IsPortalTypeSelected;
            PortalKeyBox.Visibility = isPortal ? Visibility.Visible : Visibility.Collapsed;
            AddPlaylistFileButton.Visibility = isPortal ? Visibility.Collapsed : Visibility.Visible;
            PlaylistUrlBox.PlaceholderText = isPortal
                ? L.T("Stroka_Portala_Portal_Key_URL_Ili")
                : L.T("URL_Pleylista_M3U_M3U8_Lbl");
        }

        private void PlaylistTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdatePlaylistTypeUi();
        }

        private async void AddPlaylistFileButton_Click(object sender, RoutedEventArgs e)
        {

            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".m3u");
            picker.FileTypeFilter.Add(".m3u8");
            if (App.MainWindow is { } window)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            }

            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            await AddPlaylistAsync(file.Path);
        }


        private async Task AddPlaylistAsync(string urlOrPath, string type = "m3u", string? portalKey = null)
        {
            var name = PlaylistNameBox.Text.Trim();
            var isPortal = type == "portal";
            AddPlaylistButton.IsEnabled = false;
            AddPlaylistFileButton.IsEnabled = false;
            SetPlaylistStatus(isPortal
                ? L.T("Zagruzka_Kataloga_Portala")
                : L.T("Zagruzka_I_Razbor_Pleylista"));

            try
            {
                var playlist = new PlaylistSource
                {
                    Id = _viewModel.AppSettings.Playlists.Count == 0
                        ? 1
                        : _viewModel.AppSettings.Playlists.Max(p => p.Id) + 1,
                    Name = string.IsNullOrEmpty(name) ? MainPage.DefaultPlaylistName(urlOrPath) : name,
                    Url = urlOrPath,
                    Type = type,
                    PortalKey = isPortal ? portalKey : null
                };

                if (_viewModel.AppSettings.Playlists.Count == 0)
                {
                    _viewModel.AppSettings.ActivePlaylistId = playlist.Id;
                }
                _viewModel.AppSettings.Playlists.Add(playlist);
                await _settingsService.SaveAsync(_viewModel.AppSettings);

                if (PlaylistItems.Count == 0)
                {
                    await _switchPlaylist(playlist);
                }

                RebuildPlaylistItems();
                PlaylistUrlBox.Text = string.Empty;
                PlaylistNameBox.Text = string.Empty;
                SetPlaylistStatus(string.Format(L.T("Pleylist_0_Dobavlen"), playlist.Name, playlist.Name));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось добавить плейлист {Url}.", urlOrPath);
                SetPlaylistStatus(string.Format(L.T("Ne_Udalos_Zagruzit_Pleylist_0"), ex.Message));
            }
            finally
            {
                AddPlaylistButton.IsEnabled = true;
                AddPlaylistFileButton.IsEnabled = true;
            }
        }

        private async void ActivatePlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: PlaylistListItem { IsActive: false } item })
            {
                SetPlaylistStatus(string.Format(L.T("Pereklyuchenie_Na_0"), item.Playlist.Name, item.Playlist.Name));
                await _switchPlaylist(item.Playlist);
                RebuildPlaylistItems();
                SetPlaylistStatus(string.Format(L.T("Aktiven_Pleylist_0"), item.Playlist.Name, item.Playlist.Name));
            }
        }

        private async void RemovePlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: PlaylistListItem item })
            {
                return;
            }

            var confirmed = await ConfirmAsync(
                L.T("Udalit_Lbl"),
                string.Format(L.T("Udalit_Pleylist_Vopros_0"), item.Playlist.Name),
                L.T("Udalit_Lbl"));
            if (!confirmed)
            {
                return;
            }

            var playlist = item.Playlist;
            _viewModel.AppSettings.Playlists.Remove(playlist);
            await _playlistCacheService.DeleteAsync(playlist.Id);

            var wasActive = playlist.Id == _viewModel.AppSettings.ActivePlaylistId;
            if (wasActive)
            {
                var next = _viewModel.AppSettings.Playlists.FirstOrDefault();
                if (next != null)
                {


                    await _switchPlaylist(next);
                }
                else
                {


                    _viewModel.AppSettings.ActivePlaylistId = 0;
                    _viewModel.Player.Stop();
                    _viewModel.SelectedChannel = null;
                    await _channelRepository.Clear();
                    _viewModel.Channels.Clear();
                    _viewModel.EpgViewModel.SetChannels(new System.Collections.Generic.List<ChannelViewModel>());
                    _viewModel.UpdateChannelCountText();
                    _viewModel.RefreshGroups();
                    _viewModel.FilterChannels();
                }
            }

            await _settingsService.SaveAsync(_viewModel.AppSettings);
            RebuildPlaylistItems();
            SetPlaylistStatus(string.Format(L.T("Pleylist_0_Udalen"), playlist.Name, playlist.Name));
        }

        private void SetPlaylistStatus(string text)
        {
            PlaylistStatusText.Text = text;
            PlaylistStatusText.Visibility = Visibility.Visible;
        }


        public class ChannelOverrideListItem
        {
            public PlaylistDatabaseService.ChannelOverride Override { get; set; } = null!;

            public string ChannelName => Override.ChannelName;

            public string ActionText => Override.IsDeleted
                ? L.T("Pravka_Kanal_Udalen")
                : string.Format(L.T("Pravka_Kanal_Perenesen"), Override.OriginalGroup ?? "—", Override.NewGroup ?? "—");

            public bool IsChecked { get; set; } = true;
        }


        private async void RestoreChannelsButton_Click(object sender, RoutedEventArgs e)
        {
            var playlist = _viewModel.AppSettings.Playlists
                .FirstOrDefault(p => p.Id == _viewModel.AppSettings.ActivePlaylistId);
            if (playlist == null)
            {
                SetPlaylistStatus(L.T("Net_Aktivnogo_Pleylista"));
                return;
            }

            var overrides = await _playlistCacheService.GetChannelOverridesAsync(playlist.Id);
            if (overrides.Count == 0)
            {
                SetPlaylistStatus(L.T("Net_Udalennyh_Ili_Perenesennyh"));
                return;
            }

            if (overrides.Any(o => ParentalControlService.IsPinRequiredForGroup(
                    _viewModel.AppSettings, o.OriginalGroup ?? o.NewGroup)))
            {
                await HideHostAsync();
                var pinDialog = new ThemedContentDialog
                {
                    XamlRoot = _hostDialog?.XamlRoot ?? XamlRoot,
                    Title = L.T("Roditelskiy_Kontrol_Lbl"),
                    Content = new PasswordBox { PlaceholderText = L.T("Vvod_Pin_Pole"), Width = 280 },
                    PrimaryButtonText = L.T("OK"),
                    CloseButtonText = L.T("Otmena_Lbl")
                };
                var pinResult = await pinDialog.ShowAsync();
                var pin = (pinDialog.Content as PasswordBox)?.Password;
                if (pinResult != ContentDialogResult.Primary ||
                    !ParentalControlService.VerifyPin(_viewModel.AppSettings, pin))
                {
                    _ = ReshowHostAsync();
                    SetPlaylistStatus(L.T("Nevernyy_Pin"));
                    return;
                }
                _ = ReshowHostAsync();
            }

            var items = overrides.Select(o => new ChannelOverrideListItem { Override = o }).ToList();
            var list = new StackPanel { Spacing = 4 };
            foreach (var item in items)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var checkBox = new CheckBox { IsChecked = item.IsChecked, VerticalAlignment = VerticalAlignment.Center };
                checkBox.Checked += (_, _) => item.IsChecked = true;
                checkBox.Unchecked += (_, _) => item.IsChecked = false;
                row.Children.Add(checkBox);
                row.Children.Add(new TextBlock
                {
                    Text = $"{item.ChannelName} — {item.ActionText}",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                });
                list.Children.Add(row);
            }

            var scroll = new ScrollViewer { MaxHeight = 320, Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
            panel.Children.Add(scroll);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var restoreButton = new Button
            {
                Content = L.T("Vosstanovit_Vybrannye"),
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"]
            };
            var clearAllButton = new Button { Content = L.T("Ochistit_Vse") };
            buttons.Children.Add(restoreButton);
            buttons.Children.Add(clearAllButton);
            panel.Children.Add(buttons);

            await HideHostAsync();

            var root = _hostDialog?.XamlRoot ?? XamlRoot;
            restoreButton.Click += async (_, _) =>
            {
                var selected = items.Where(i => i.IsChecked).Select(i => i.Override.StreamUrl).ToList();
                if (selected.Count == 0)
                {
                    return;
                }

                await _playlistCacheService.DeleteChannelOverridesAsync(playlist.Id, selected);
                SetPlaylistStatus(string.Format(L.T("Vosstanovleno_Kanalov_0"), selected.Count));
                await FinishRestoreAsync(playlist, selected.Count > 0);
            };
            clearAllButton.Click += async (_, _) =>
            {
                await _playlistCacheService.DeleteChannelOverridesAsync(
                    playlist.Id, overrides.Select(o => o.StreamUrl).ToList());
                SetPlaylistStatus(L.T("Vse_Pravki_Ochishcheny"));
                await FinishRestoreAsync(playlist, true);
            };

            var dialog = new ThemedContentDialog
            {
                XamlRoot = root,
                Title = L.T("Vosstanovit_Udalennye_Kanaly_Lbl"),
                Content = panel,
                CloseButtonText = L.T("Zakryt")
            };
            await dialog.ShowAsync();
        }


        private async Task FinishRestoreAsync(PlaylistSource playlist, bool reload)
        {
            if (reload && playlist.Id == _viewModel.AppSettings.ActivePlaylistId && _reloadActivePlaylist != null)
            {
                await _reloadActivePlaylist();
            }

            _ = ReshowHostAsync();
        }

        private async void CloseButton_Click(object sender, RoutedEventArgs e)
        {

            if (PlaylistRefreshCombo.SelectedItem is ComboBoxItem { Tag: int refreshDays })
            {
                _viewModel.AppSettings.PlaylistRefreshDays = refreshDays;
            }

            await _settingsService.SaveAsync(_viewModel.AppSettings);
            _hostDialog?.Hide();
        }

        private readonly Services.SettingsTransferService _transferService = new();


        private async Task<string?> PromptPasswordAsync(string title, string hint, bool confirm)
        {

            var root = _hostDialog?.XamlRoot ?? XamlRoot;
            await HideHostAsync();

            var box = new PasswordBox { Header = hint, PlaceholderText = "••••••••" };
            PasswordBox? repeat = null;
            var panel = new StackPanel { Spacing = 10, MinWidth = 300 };
            panel.Children.Add(box);
            if (confirm)
            {
                repeat = new PasswordBox { Header = L.T("Povtorite_Parol") };
                panel.Children.Add(repeat);
            }

            while (true)
            {
                var dialog = new ThemedContentDialog
                {
                    XamlRoot = root,
                    Title = title,
                    Content = panel,
                    PrimaryButtonText = L.T("OK"),
                    CloseButtonText = L.T("Otmena_Lbl"),
                    DefaultButton = ContentDialogButton.Primary
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    _ = ReshowHostAsync();
                    return null;
                }

                if (box.Password.Length < 4)
                {
                    await ShowTransferErrorAsync(L.T("Parol_Minimum_4_Simvola"));
                    continue;
                }

                if (repeat != null && box.Password != repeat.Password)
                {
                    await ShowTransferErrorAsync(L.T("Paroli_Ne_Sovpadayut"));
                    continue;
                }

                break;
            }

            return box.Password;
        }


        private async Task HideHostAsync()
        {
            if (_hostDialog == null)
            {
                return;
            }

            _hostDialog.Hide();
            await Task.Delay(50);
        }


        private async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
        {

            var root = _hostDialog?.XamlRoot ?? XamlRoot;
            await HideHostAsync();

            try
            {
                var dialog = new ThemedContentDialog
                {
                    XamlRoot = root,
                    Title = title,
                    Content = message,
                    PrimaryButtonText = confirmLabel,
                    CloseButtonText = L.T("Otmena_Lbl"),
                    DefaultButton = ContentDialogButton.Close
                };
                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            finally
            {
                _ = ReshowHostAsync();
            }
        }


        private async Task ReshowHostAsync()
        {
            if (_hostDialog != null)
            {
                await _hostDialog.ShowAsync();
            }
        }

        private async Task ShowTransferErrorAsync(string message)
        {
            var dialog = new ThemedContentDialog
            {
                XamlRoot = _hostDialog?.XamlRoot ?? XamlRoot,
                Title = L.T("Perenos_Nastroek_Lbl"),
                Content = message,
                CloseButtonText = L.T("Ponyatno")
            };
            await dialog.ShowAsync();
        }

        private async void ExportSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ExportSettingsAsync();
            }
            catch (Exception ex)
            {


                _logger.LogError(ex, "Экспорт настроек: сбой до открытия пикера.");
                await ShowTransferErrorAsync(string.Format(L.T("Ne_Udalos_Otkryt_Dialog_Eksporta_0"), ex.Message, ex.Message));
            }
        }

        private async Task ExportSettingsAsync()
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = $"iptvplayer-settings-{DateTime.Now:yyyyMMdd}",
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary
            };


            picker.FileTypeChoices.Add(
                "IptvPlayer export (*.iptvplayer)",
                new System.Collections.Generic.List<string> { ".iptvplayer" });
            if (App.MainWindow is { } window)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            }

            var file = await picker.PickSaveFileAsync();
            if (file == null)
            {
                return;
            }

            var password = await PromptPasswordAsync(
                L.T("Parol_Eksporta"),
                L.T("Fayl_Budet_Soderzhat_Ssylki_I_Klyuchi"),
                confirm: true);
            if (password == null)
            {
                return;
            }

            try
            {
                await _transferService.ExportAsync(_viewModel.AppSettings, file.Path, password);
                SetPlaylistStatus(string.Format(L.T("Nastroyki_Eksportirovany_0"), file.Name, file.Name));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Экспорт настроек в {Path}.", file.Path);
                await ShowTransferErrorAsync(string.Format(L.T("Ne_Udalos_Eksportirovat_Nastroyki_0"), ex.Message, ex.Message));
            }
            finally
            {
                _ = ReshowHostAsync();
            }
        }

        private async void ImportSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ImportSettingsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Импорт настроек: сбой до выбора файла.");
                await ShowTransferErrorAsync(string.Format(L.T("Ne_Udalos_Otkryt_Dialog_Importa_0"), ex.Message, ex.Message));
            }
        }

        private async Task ImportSettingsAsync()
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();

            picker.FileTypeFilter.Add(".iptvplayer");

            picker.FileTypeFilter.Add(".json");
            if (App.MainWindow is { } window)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            }

            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            var password = await PromptPasswordAsync(
                L.T("Parol_Fayla"),
                L.T("Parol_Zadannyy_Pri_Eksporte"),
                confirm: false);
            if (password == null)
            {
                return;
            }

            Models.AppSettings imported;
            try
            {
                imported = await _transferService.ImportAsync(file.Path, password);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Импорт настроек из {Path}.", file.Path);
                await ShowTransferErrorAsync(string.Format(L.T("Ne_Udalos_Prochitat_Fayl_0"), ex.Message, ex.Message));
                _ = ReshowHostAsync();
                return;
            }

            if (imported.Playlists.Count == 0)
            {
                await ShowTransferErrorAsync(L.T("V_Fayle_Eksporta_Net_Pleylistov"));
                _ = ReshowHostAsync();
                return;
            }


            var modeDialog = new ThemedContentDialog
            {
                XamlRoot = _hostDialog?.XamlRoot ?? XamlRoot,
                Title = L.T("Import_Nastroek"),
                Content = L.T("Zamenit_Vse_Nastroyki_Ili_Dobavit_Tolko"),
                PrimaryButtonText = L.T("Zamenit_Vse"),
                SecondaryButtonText = L.T("Dobavit_Pleylisty"),
                CloseButtonText = L.T("Otmena_Lbl")
            };
            var result = await modeDialog.ShowAsync();
            if (result == ContentDialogResult.None)
            {
                _ = ReshowHostAsync();
                return;
            }

            var mode = result == ContentDialogResult.Primary
                ? Services.SettingsTransferService.ImportMode.ReplaceAll
                : Services.SettingsTransferService.ImportMode.PlaylistsOnly;

            var count = Services.SettingsTransferService.Apply(
                _viewModel.AppSettings, imported, mode);

            await _settingsService.SaveAsync(_viewModel.AppSettings);

            if (mode == Services.SettingsTransferService.ImportMode.ReplaceAll &&
                _viewModel.AppSettings.Playlists.FirstOrDefault() is { } active)
            {
                await _switchPlaylist(active);
            }
            else if (_viewModel.AppSettings.ActivePlaylistId == 0 &&
                     _viewModel.AppSettings.Playlists.FirstOrDefault() is { } first)
            {
                await _switchPlaylist(first);
            }

            RebuildPlaylistItems();
            SetPlaylistStatus(mode == Services.SettingsTransferService.ImportMode.ReplaceAll
                ? string.Format(L.T("Nastroyki_Zameneny_Pleylistov_0"), count, count)
                : string.Format(L.T("Dobavleno_Pleylistov_0"), count, count));
            _ = ReshowHostAsync();
        }
    }
}
