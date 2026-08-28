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
    /// <summary>
    /// Элемент списка плейлистов в диалоге: обёртка над PlaylistSource с
    /// вычисленной видимостью маркера активного и кнопки «Активировать»
    /// (только у неактивного). Список пересобирается целиком после каждого
    /// действия, поэтому уведомления об изменении не нужны.
    /// </summary>
    public class PlaylistListItem
    {
        public PlaylistSource Playlist { get; set; } = new();

        public bool IsActive { get; set; }

        /// <summary>Открыто ли поле переименования этого плейлиста (одновременно — только у одного).</summary>
        public bool IsEditing { get; set; }

        /// <summary>Раскрыта ли секция источников EPG этого плейлиста.</summary>
        public bool IsEpgExpanded { get; set; }

        private Visibility ToVisibility(bool visible) =>
            visible ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ActiveMarkVisibility => ToVisibility(IsActive);

        public Visibility ActivateButtonVisibility => ToVisibility(!IsActive);

        public Visibility ViewVisibility => ToVisibility(!IsEditing);

        public Visibility EditVisibility => ToVisibility(IsEditing);

        public Visibility EpgSectionVisibility => ToVisibility(IsEpgExpanded);
    }

    /// <summary>
    /// Управление плейлистами: список источников (активный отмечен, кнопка
    /// «Активировать» переключает на него список каналов через колбэк из
    /// MainPage), добавление нового (имя необязательное — по умолчанию хост
    /// URL) и удаление с подтверждением. Первый добавленный плейлист
    /// активируется сразу (сценарий первого запуска); следующие — каналы
    /// подгрузятся при активации. Частота обновления — общая, сохраняется
    /// по кнопке «Готово».
    /// </summary>
    public sealed partial class PlaylistSettingsDialog : UserControl
    {
        private readonly MainPageViewModel _viewModel;
        private readonly ISettingsService _settingsService;
        private readonly IChannelRepository _channelRepository;
        private readonly IPlaylistCacheService _playlistCacheService;
        private readonly ILogger<PlaylistSettingsDialog> _logger;
        private readonly Func<PlaylistSource, Task> _switchPlaylist;

        // Контейнер-ContentDialog создаётся в ShowAsync; кнопки внутри
        // UserControl закрывают его через эту ссылку (искать родителя по
        // визуальному дереву нельзя — им оказывается ContentPresenter
        // шаблона диалога, а не сам ContentDialog).
        private ContentDialog? _hostDialog;

        // Плейлист, у которого открыто поле переименования (одновременно — один).
        private PlaylistSource? _renamingPlaylist;

        // Плейлист с раскрытой секцией источников EPG (одновременно — один).
        private PlaylistSource? _epgExpandedPlaylist;

        public ObservableCollection<PlaylistListItem> PlaylistItems { get; } = new();

        // Двухшаговое подтверждение удаления: плейлист удаляется безвозвратно
        // (вместе с локальным кэшем), случайный клик недопустим.
        private PlaylistSource? _removeArmedPlaylist;
        private Button? _removeArmedButton;

        public PlaylistSettingsDialog(
            MainPageViewModel viewModel,
            ISettingsService settingsService,
            IM3UParserService m3uParserService,
            IChannelRepository channelRepository,
            IPlaylistCacheService playlistCacheService,
            ILogger<PlaylistSettingsDialog> logger,
            Func<PlaylistSource, Task> switchPlaylist)
        {
            _viewModel = viewModel;
            _settingsService = settingsService;
            _channelRepository = channelRepository;
            _playlistCacheService = playlistCacheService;
            _logger = logger;
            _switchPlaylist = switchPlaylist;
            InitializeComponent();
        }

        public async Task ShowAsync(XamlRoot xamlRoot)
        {
            await LoadAsync();
            // Заголовок показывает сам ContentDialog — внутренний TitleText
            // не нужен, иначе «Плейлист» читается дважды.
            TitleText.Visibility = Visibility.Collapsed;

            var dialog = new ContentDialog
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
            DisarmRemove();

            RebuildPlaylistItems();

            // Периодичность обновления плейлистов: 1/3/7 дней или «никогда»
            // (только при добавлении источника).
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

            DisarmRemove();
            _renamingPlaylist = ReferenceEquals(_renamingPlaylist, item.Playlist) ? null : item.Playlist;
            RebuildPlaylistItems();

            // Фокус в поле имени после пересборки: FindName ищет внутри шаблона
            // последнего созданного элемента — поле есть только у редактируемой
            // карточки, поэтому имя уникально в пределах диалога.
            if (_renamingPlaylist != null && PlaylistsList.FindName("NameEditBox") is TextBox box)
            {
                box.Text = _renamingPlaylist.Name;
                box.SelectAll();
                _ = box.Focus(FocusState.Programmatic);
            }
        }

        // ===================== Источники EPG плейлиста =====================

        /// <summary>
        /// Источник EPG, с которым работает обработчик: сам EPGSource (чекбокс/
        /// удаление в строке) и владеющий плейлист — ItemsControl строки
        /// вложен в карточку, его DataContext наследуется вниз до строки.
        /// </summary>
        private static (PlaylistSource Playlist, EPGSource Source)? FindEpgSourceOwner(object sender)
        {
            if (sender is not FrameworkElement element)
            {
                return null;
            }

            var source = element.DataContext as EPGSource;
            var node = element;
            while (node != null && node.DataContext is not PlaylistListItem)
            {
                node = node.Parent as FrameworkElement;
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

            // Поле URL — сосед кнопки «+» по строке добавления.
            var box = (sender as FrameworkElement)?.Parent is StackPanel row
                ? row.Children.OfType<TextBox>().FirstOrDefault()
                : null;
            var url = box?.Text?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                SetPlaylistStatus(L.T("Vvedite_URL_Istochnika_EPG"));
                return;
            }

            item.Playlist.EpgSources.Add(new EPGSource { Url = url, IsEnabled = true });
            box!.Text = string.Empty;
            await PlaylistEpgSourcesChangedAsync(item.Playlist);
        }

        private async void PlaylistEpgUrlBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter &&
                sender is TextBox { Text: { Length: > 0 } } box &&
                box.DataContext is PlaylistListItem item)
            {
                item.Playlist.EpgSources.Add(new EPGSource { Url = box.Text.Trim(), IsEnabled = true });
                box!.Text = string.Empty;
                await PlaylistEpgSourcesChangedAsync(item.Playlist);
                e.Handled = true;
            }
        }

        private async void PlaylistEpgSourceRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (FindEpgSourceOwner(sender) is { } owner)
            {
                owner.Playlist.EpgSources.Remove(owner.Source);
                await PlaylistEpgSourcesChangedAsync(owner.Playlist);
            }
        }

        private async void PlaylistEpgSource_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (FindEpgSourceOwner(sender) is { } owner)
            {
                await PlaylistEpgSourcesChangedAsync(owner.Playlist);
            }
        }

        /// <summary>
        /// Сохраняет настройки после изменения источников EPG плейлиста; если
        /// это активный плейлист — перечитывает EPG фоном (дисковый кэш
        /// источников не чистится, перекачки фида не будет).
        /// </summary>
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
            // Поле ввода — сосед кнопки ✓ по панели редактирования карточки
            // (имена внутри DataTemplate не видны через FindName страницы).
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

        /// <summary>
        /// Сохраняет введённое имя плейлиста: пустое имя не заменяет существующее
        /// (кнопка ✓ просто закрывает редактирование).
        /// </summary>
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
                // Строка портала часто поставляется комбинированной:
                // "portal::[key:KEY]https://host/api/v1/" — её вставляют в поле
                // URL целиком. Вычленяем ключ и URL из неё.
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

        /// <summary>
        /// Вид полей, зависящих от типа источника: у портала вместо URL M3U —
        /// базовый адрес API и ключ доступа, выбор локального файла не нужен.
        /// </summary>
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
            // Пикеры WinUI 3 требуют HWND-владельца (InitializeWithWindow),
            // иначе PickSingleFileAsync падает с «Invalid window handle»
            // (особенно в unpackaged-сборке) — как в экспорте/импорте настроек.
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
                return; // Пользователь отменил выбор.
            }

            await AddPlaylistAsync(file.Path);
        }

        /// <summary>
        /// Добавляет источник по URL или пути к локальному файлу M3U/M3U8
        /// (type "m3u") либо видео-портал по базовому URL API и ключу
        /// (type "portal"): создаёт PlaylistSource, активирует сразу, если он
        /// первый, и переключает на него список каналов (SwitchPlaylistAsync
        /// сам скачает и разберёт источник и сохранит кэш).
        /// </summary>
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

                // Первый плейлист активируется сразу — это сценарий первого
                // запуска: до этого момента список каналов пуст, и диалог
                // должен привести приложение в рабочее состояние без лишних
                // кликов. Первый показ нового плейлиста — скачивание (кэша
                // этого плейлиста ещё нет), SwitchPlaylistAsync сделает всё сам.
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

            // Двухшаговое подтверждение: первый клик взводит кнопку, второй —
            // удаляет (ContentDialog из диалога не показать).
            if (!ReferenceEquals(_removeArmedPlaylist, item.Playlist))
            {
                DisarmRemove();
                _removeArmedPlaylist = item.Playlist;
                if (sender is Button button)
                {
                    _removeArmedButton = button;
                    button.Content = "?";
                    ToolTipService.SetToolTip(button, L.T("Tochno_Udalit_Nazhmite_Eshche_Raz"));
                }
                SetPlaylistStatus(string.Format(L.T("Povtornoe_Nazhatie_Udalit_Pleylist_0_Vmeste"), item.Playlist.Name, item.Playlist.Name));
                return;
            }

            DisarmRemove();
            var playlist = item.Playlist;
            _viewModel.AppSettings.Playlists.Remove(playlist);
            await _playlistCacheService.DeleteAsync(playlist.Id);

            var wasActive = playlist.Id == _viewModel.AppSettings.ActivePlaylistId;
            if (wasActive)
            {
                var next = _viewModel.AppSettings.Playlists.FirstOrDefault();
                if (next != null)
                {
                    // Плейлистов ещё несколько — переключаем список каналов на
                    // первый оставшийся.
                    await _switchPlaylist(next);
                }
                else
                {
                    // Удалён единственный плейлист — остаёмся без каналов, как
                    // после «Сбросить»: чистим список и репозиторий.
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

        /// <summary>Возвращает взведённую кнопку удаления в обычный вид.</summary>
        private void DisarmRemove()
        {
            if (_removeArmedButton != null)
            {
                _removeArmedButton.Content = "✕";
                ToolTipService.SetToolTip(_removeArmedButton, L.T("Udalit_Lbl"));
                _removeArmedButton = null;
            }
            _removeArmedPlaylist = null;
        }

        private void SetPlaylistStatus(string text)
        {
            PlaylistStatusText.Text = text;
            PlaylistStatusText.Visibility = Visibility.Visible;
        }

        private async void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Частота обновления — по «Готово», как раньше по «Сохранить».
            if (PlaylistRefreshCombo.SelectedItem is ComboBoxItem { Tag: int refreshDays })
            {
                _viewModel.AppSettings.PlaylistRefreshDays = refreshDays;
            }

            await _settingsService.SaveAsync(_viewModel.AppSettings);
            _hostDialog?.Hide();
        }

        // ===================== Экспорт / импорт настроек =====================

        private readonly Services.SettingsTransferService _transferService = new();

        /// <summary>
        /// Диалог пароля поверх «Плейлистов»: два ContentDialog одновременно
        /// показать нельзя, поэтому хост прячется. При отмене хост возвращается
        /// здесь; при успехе вызывающий код сам показывает его (или следующий
        /// диалог) в конце своей цепочки.
        /// confirm=true — с повтором пароля (экспорт).
        /// </summary>
        private async Task<string?> PromptPasswordAsync(string title, string hint, bool confirm)
        {
            // XamlRoot берём ДО Hide: после скрытия хост-диалога этот
            // UserControl выгружается из дерева и его XamlRoot становится
            // null — ContentDialog без XamlRoot падает COMException'ом.
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
                var dialog = new ContentDialog
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

        /// <summary>
        /// Скрывает хост-диалог «Плейлисты» и выжидает такт диспетчера:
        /// следующий ContentDialog нельзя открыть, пока предыдущий не успел
        /// закрыться («Only one ContentDialog», COMException 0x80000019).
        /// </summary>
        private async Task HideHostAsync()
        {
            if (_hostDialog == null)
            {
                return;
            }

            _hostDialog.Hide();
            await Task.Delay(50);
        }

        /// <summary>Показывает хост-диалог «Плейлисты» снова (после вложенного диалога).</summary>
        private async Task ReshowHostAsync()
        {
            if (_hostDialog != null)
            {
                await _hostDialog.ShowAsync();
            }
        }

        private async Task ShowTransferErrorAsync(string message)
        {
            var dialog = new ContentDialog
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
                // До выбора файла исключения улетали в App.UnhandledException
                // и выглядели для пользователя как «кнопка не работает».
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
            // Расширение с одной точкой: WinRT-пикеры отвергают составные
            // расширения вроде ".iptvplayer.json" (ArgumentException).
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
                return; // Отмена выбора файла.
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
            // Составное расширение ".iptvplayer.json" пикер не принимает.
            picker.FileTypeFilter.Add(".iptvplayer");
            // Файл мог быть переименован вручную — пустим и .json.
            picker.FileTypeFilter.Add(".json");
            if (App.MainWindow is { } window)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(
                    picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            }

            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return; // Отмена выбора файла.
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

            // Хост уже скрыт PromptPasswordAsync; XamlRoot хоста ещё жив.
            var modeDialog = new ContentDialog
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

            // После «заменить всё» активный плейлист новый — переключаем
            // список каналов; при «добавить» текущий не трогаем.
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
