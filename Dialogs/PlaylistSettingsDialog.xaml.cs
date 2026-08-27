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

        /// <summary>
        /// Ссылка плейлиста для показа в списке: учётные данные (username,
        /// password, token) в query-строке маскируются «***» — это фактически
        /// пароль от подписки. Структура URL (хост, путь, имена параметров)
        /// остаётся видимой — по ней плейлист опознаётся.
        /// </summary>
        public string DisplayUrl => Services.SecretProtector.Mask(Playlist.Url);
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
                Title = L.T("Плейлист", "Playlist"),
                Content = this
            };
            _hostDialog = dialog;
            await dialog.ShowAsync();
        }

        private async Task LoadAsync()
        {
            var settings = await _settingsService.LoadAsync();

            TitleText.Text = L.T("Плейлист", "Playlist");
            PlaylistHeader.Text = L.T("Плейлисты", "Playlists");
            PlaylistHint.Text = L.T(
                "Каналы в списке — из активного плейлиста. Переключайте плейлист в меню настроек (Настройки → Сменить плейлист).",
                "The channel list shows the active playlist. Switch playlists in the settings menu (Settings → Switch playlist).");
            AddPlaylistHeader.Text = L.T("Добавить плейлист", "Add playlist");
            PlaylistNameBox.PlaceholderText = L.T("Имя (необязательно)", "Name (optional)");
            PlaylistUrlBox.PlaceholderText = L.T("URL плейлиста M3U/M3U8", "M3U/M3U8 playlist URL");
            PortalKeyBox.PlaceholderText = L.T("Ключ портала (portal::[key:...])", "Portal key (portal::[key:...])");
            PortalKeyBox.Header = L.T("Ключ портала", "Portal key");
            AddPlaylistButton.Content = L.T("Добавить", "Add");
            AddPlaylistFileButton.Content = L.T("Выбрать файл...", "Pick file...");
            PlaylistRefreshHeader.Text = L.T("Частота обновления плейлистов", "Playlist refresh rate");
            PlaylistRefreshHint.Text = L.T(
                "Как часто при запуске перекачивать активный плейлист.",
                "How often to re-download the active playlist on startup.");
            CloseButton.Content = L.T("Готово", "Done");

            PlaylistUrlBox.Text = string.Empty;
            PlaylistNameBox.Text = string.Empty;
            PortalKeyBox.Text = string.Empty;
            PlaylistTypeCombo.Items.Clear();
            PlaylistTypeCombo.Items.Add(new ComboBoxItem { Content = L.T("Плейлист M3U/M3U8", "M3U/M3U8 playlist"), Tag = "m3u" });
            PlaylistTypeCombo.Items.Add(new ComboBoxItem { Content = L.T("Видео-портал", "Video portal"), Tag = "portal" });
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
                         (L.T("Каждый день", "Daily"), 1),
                         (L.T("Каждые 3 дня", "Every 3 days"), 3),
                         (L.T("Каждую неделю", "Weekly"), 7),
                         (L.T("Никогда (только при добавлении)", "Never (only when added)"), 0),
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
                SetPlaylistStatus(L.T("Введите URL источника EPG.", "Enter an EPG source URL."));
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
                SetPlaylistStatus(L.T(
                    $"Плейлист переименован в «{item.Playlist.Name}».",
                    $"Playlist renamed to \"{item.Playlist.Name}\"."));
            }

            _renamingPlaylist = null;
            RebuildPlaylistItems();
        }

        private async void AddPlaylistButton_Click(object sender, RoutedEventArgs e)
        {
            var url = PlaylistUrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                SetPlaylistStatus(L.T("Введите URL плейлиста.", "Enter a playlist URL."));
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
                    SetPlaylistStatus(L.T("Введите ключ портала.", "Enter the portal key."));
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
                ? L.T("Строка портала (portal::[key:...]URL) или базовый URL API", "Portal string (portal::[key:...]URL) or API base URL")
                : L.T("URL плейлиста M3U/M3U8", "M3U/M3U8 playlist URL");
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
                ? L.T("Загрузка каталога портала...", "Loading portal catalog...")
                : L.T("Загрузка и разбор плейлиста...", "Loading and parsing playlist..."));

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
                SetPlaylistStatus(L.T(
                    $"Плейлист «{playlist.Name}» добавлен.",
                    $"Playlist \"{playlist.Name}\" added."));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось добавить плейлист {Url}.", urlOrPath);
                SetPlaylistStatus($"Не удалось загрузить плейлист: {ex.Message}");
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
                SetPlaylistStatus(L.T(
                    $"Переключение на «{item.Playlist.Name}»...",
                    $"Switching to \"{item.Playlist.Name}\"..."));
                await _switchPlaylist(item.Playlist);
                RebuildPlaylistItems();
                SetPlaylistStatus(L.T(
                    $"Активен плейлист «{item.Playlist.Name}».",
                    $"Playlist \"{item.Playlist.Name}\" is now active."));
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
                    ToolTipService.SetToolTip(button, L.T("Точно удалить? Нажмите ещё раз", "Really remove? Click again"));
                }
                SetPlaylistStatus(L.T(
                    $"Повторное нажатие удалит плейлист «{item.Playlist.Name}» вместе с его локальным кэшем.",
                    $"Clicking again will remove playlist \"{item.Playlist.Name}\" and its local cache."));
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
            SetPlaylistStatus(L.T(
                $"Плейлист «{playlist.Name}» удалён.",
                $"Playlist \"{playlist.Name}\" removed."));
        }

        /// <summary>Возвращает взведённую кнопку удаления в обычный вид.</summary>
        private void DisarmRemove()
        {
            if (_removeArmedButton != null)
            {
                _removeArmedButton.Content = "✕";
                ToolTipService.SetToolTip(_removeArmedButton, L.T("Удалить", "Remove"));
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
    }
}
