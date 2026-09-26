using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace IptvPlayer.ViewModels;


public partial class MainPageViewModel : ObservableObject
{
    private static string AllGroupsOption => L.T("Vse_Gruppy");
    private static string AllGenresOption => L.T("Vse_Zhanry");
    private static string AllYearsOption => L.T("Vse_Gody");
    private static string FavoritesOption => L.T("Izbrannoe");

    private readonly ISettingsService _settingsService;
    private readonly IVideoPortalService _videoPortalService;
    private readonly Services.VodResumeStore _vodResumeStore;


    private readonly Dictionary<string, VodResumePosition> _vodResumePositions = new();
    private readonly ILogger<MainPageViewModel> _logger;

    private EpgViewModel _epgViewModel;

    public EpgViewModel EpgViewModel
    {
        get => _epgViewModel;
        set => SetProperty(ref _epgViewModel, value);
    }


    public PlayerViewModel Player { get; }


    public RecordingService Recording { get; }

    private ObservableCollection<ChannelViewModel> _channels = new();

    public ObservableCollection<ChannelViewModel> Channels
    {
        get => _channels;
        set
        {
            if (SetProperty(ref _channels, value))
            {
                OnChannelsChanged(value);
            }
        }
    }

    private ObservableCollection<ChannelViewModel> _displayedChannels = new();

    public ObservableCollection<ChannelViewModel> DisplayedChannels
    {
        get => _displayedChannels;
        set => SetProperty(ref _displayedChannels, value);
    }

    private ChannelViewModel? _selectedChannel;

    public ChannelViewModel? SelectedChannel
    {
        get => _selectedChannel;
        set => SetProperty(ref _selectedChannel, value);
    }

    private bool _isEpgVisible;

    public bool IsEpgVisible
    {
        get => _isEpgVisible;
        set => SetProperty(ref _isEpgVisible, value);
    }

    private bool _isRecording;

    public bool IsRecording
    {
        get => _isRecording;
        set => SetProperty(ref _isRecording, value);
    }

    private string _searchQuery = string.Empty;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                OnSearchQueryChanged(value);
            }
        }
    }

    private string _selectedGroup = AllGroupsOption;

    public string SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                OnSelectedGroupChanged(value);
            }
        }
    }

    private ObservableCollection<string> _groups = new();

    public ObservableCollection<string> Groups
    {
        get => _groups;
        set => SetProperty(ref _groups, value);
    }

    private string _selectedGenre = AllGenresOption;

    public string SelectedGenre
    {
        get => _selectedGenre;
        set
        {
            if (SetProperty(ref _selectedGenre, value))
            {
                OnSelectedGenreChanged(value);
            }
        }
    }

    private ObservableCollection<string> _genres = new();

    public ObservableCollection<string> Genres
    {
        get => _genres;
        set => SetProperty(ref _genres, value);
    }

    public Visibility IsGenreFilterVisible => Genres.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    private string _selectedYear = "";

    public string SelectedYear
    {
        get => _selectedYear;
        set
        {
            if (SetProperty(ref _selectedYear, value))
            {
                OnSelectedYearChanged(value);
            }
        }
    }

    private ObservableCollection<string> _years = new();

    public ObservableCollection<string> Years
    {
        get => _years;
        set => SetProperty(ref _years, value);
    }

    public Visibility IsYearFilterVisible => Years.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    private static string AllContentTypesOption => L.T("Vse_Tipy");

    private string _selectedContentType = AllContentTypesOption;


    public string SelectedContentType
    {
        get => _selectedContentType;
        set
        {
            if (SetProperty(ref _selectedContentType, value))
            {
                OnSelectedContentTypeChanged(value);
            }
        }
    }

    private ObservableCollection<string> _contentTypes = new();


    public ObservableCollection<string> ContentTypes
    {
        get => _contentTypes;
        set => SetProperty(ref _contentTypes, value);
    }


    public Visibility IsContentTypeFilterVisible => _isPortalSource && ContentTypes.Count > 1
        ? Visibility.Visible
        : Visibility.Collapsed;


    public Visibility IsGroupFilterVisible => !_isPortalSource && !_isVodSource || _isOnlineCinemaSource
        ? Visibility.Visible
        : Visibility.Collapsed;

    private bool _isFilterLoading;


    public bool IsFilterLoading
    {
        get => _isFilterLoading;
        set => SetProperty(ref _isFilterLoading, value);
    }

    private bool _isPlaylistLoading;


    public bool IsPlaylistLoading
    {
        get => _isPlaylistLoading;
        set => SetProperty(ref _isPlaylistLoading, value);
    }

    private string _playlistLoadingText = "";


    public string PlaylistLoadingText
    {
        get => _playlistLoadingText;
        set => SetProperty(ref _playlistLoadingText, value);
    }

    private string _channelCountText = "";

    public string ChannelCountText
    {
        get => _channelCountText;
        set => SetProperty(ref _channelCountText, value);
    }

    private string? _recordError;

    public string? RecordError
    {
        get => _recordError;
        set => SetProperty(ref _recordError, value);
    }


    public AppSettings AppSettings { get; set; } = new();

    private DateTime? _sleepTimerEndTime;


    public DateTime? SleepTimerEndTime
    {
        get => _sleepTimerEndTime;
        private set
        {
            if (SetProperty(ref _sleepTimerEndTime, value))
            {
                OnPropertyChanged(nameof(IsSleepTimerActive));
                SleepTimerChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }


    public bool IsSleepTimerActive => _sleepTimerEndTime != null;


    public string? SleepTimerRemainingText
    {
        get
        {
            if (_sleepTimerEndTime == null) return null;
            var remaining = _sleepTimerEndTime.Value - DateTime.UtcNow;
            if (remaining.TotalSeconds <= 0) return "00:00";
            return $"{(int)remaining.TotalMinutes:D2}:{remaining.Seconds:D2}";
        }
    }


    public void StartSleepTimer(int minutes)
    {
        if (minutes <= 0)
        {
            StopSleepTimer();
            return;
        }

        SleepTimerEndTime = DateTime.UtcNow.AddMinutes(minutes);
        _logger.LogInformation("Таймер сна запущен на {Minutes} мин, сработает в {EndTime:HH:mm:ss}.", minutes, SleepTimerEndTime.Value.ToLocalTime());
    }


    public void StopSleepTimer()
    {
        if (_sleepTimerEndTime == null) return;
        SleepTimerEndTime = null;
        _logger.LogInformation("Таймер сна остановлен.");
    }


    public void CheckSleepTimer()
    {
        if (_sleepTimerEndTime == null) return;

        OnPropertyChanged(nameof(SleepTimerRemainingText));

        if (DateTime.UtcNow >= _sleepTimerEndTime.Value)
        {
            _logger.LogInformation("Таймер сна сработал — остановка воспроизведения.");
            SleepTimerEndTime = null;
            SleepTimerExpired?.Invoke(this, EventArgs.Empty);
        }
    }


    public event EventHandler? RecordingChanged;


    public event EventHandler? EpgVisibilityChanged;


    public event EventHandler? SettingsSaveRequested;


    public event EventHandler<ProgramReminder>? ReminderToastRequested;


    public event EventHandler? FilterChanged;


    public event EventHandler? ScrollToProgramRequested;


    public event EventHandler<string>? ArchivePlayErrorRequested;


    public event EventHandler? SleepTimerExpired;


    public event EventHandler? SleepTimerChanged;

    public MainPageViewModel(
        EpgViewModel epgViewModel,
        ISettingsService settingsService,
        IVideoPortalService videoPortalService,
        PlayerViewModel player,
        RecordingService recording,
        Services.VodResumeStore vodResumeStore,
        Services.ICatalogDatabaseService catalogDatabase,
        Services.OnlineCinemaCatalogService onlineCinemaCatalog,
        Services.IOnlineCinemaStreamResolver onlineCinemaResolver,
        ILogger<MainPageViewModel> logger)
    {
        _epgViewModel = epgViewModel;
        _settingsService = settingsService;
        _videoPortalService = videoPortalService;
        _vodResumeStore = vodResumeStore;
        _onlineCinemaDb = catalogDatabase;
        _onlineCinemaCatalog = onlineCinemaCatalog;
        _onlineCinemaResolver = onlineCinemaResolver;
        _logger = logger;
        Player = player;
        Recording = recording;
        _selectedChannel = new ChannelViewModel();

        _onlineCinemaCatalog.Progress += text => PlaylistLoadingText = text;

        Recording.RecordingsChanged += (s, e) =>
        {
            IsRecording = Recording.IsRecordingStream(SelectedChannel?.StreamUrl);
            RecordingChanged?.Invoke(this, EventArgs.Empty);
        };

        _epgViewModel.EpgReloaded += (_, _) =>
        {
            var selected = SelectedChannel;
            FilterChannels();
            SelectedChannel = selected;
        };
    }

    private bool _isSelectingChannel;

    private System.Threading.CancellationTokenSource? _searchDebounceCts;

    private void OnSearchQueryChanged(string value)
    {
        // No Dispose: token may still be registered in the pending Task.Delay
        _searchDebounceCts?.Cancel();
        _searchDebounceCts = new System.Threading.CancellationTokenSource();
        var token = _searchDebounceCts.Token;
        _ = DebouncedFilterAsync(token);
    }

    private async Task DebouncedFilterAsync(System.Threading.CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        FilterChannels();
        await TriggerOnlineCinemaSiteSearchAsync(ct);
    }

    // Online cinema: when local filtering found nothing, ask the site's quick
    // search — hits are added to the catalog and re-filtering picks them up
    private async Task TriggerOnlineCinemaSiteSearchAsync(System.Threading.CancellationToken ct)
    {
        var query = SearchQuery?.Trim() ?? string.Empty;
        if (!_isOnlineCinemaSource || query.Length < 2 || DisplayedChannels.Count > 0 ||
            _siteSearchHits.ContainsKey(query))
        {
            return;
        }

        await SearchOnlineCinemaSiteAsync(query, ct);
    }

    private void OnSelectedGroupChanged(string value) => FilterChannels();

    private void OnSelectedContentTypeChanged(string value)
    {
        if (_suppressFilterLoad) return;
        if (_isPortalSource && PortalSource != null)
        {
            _ = LoadFilteredFromServerAsync();
        }
        else
        {
            FilterChannels();
        }
    }

    private void OnSelectedGenreChanged(string value)
    {
        if (_suppressFilterLoad) return;
        if (_isPortalSource && PortalSource != null)
        {
            _ = LoadFilteredFromServerAsync();
        }
        else
        {
            FilterChannels();
        }
    }

    private void OnSelectedYearChanged(string value)
    {
        if (_suppressFilterLoad) return;
        if (_isPortalSource && PortalSource != null)
        {
            _ = LoadFilteredFromServerAsync();
            return;
        }

        // Online cinema: the site's first page differs per year — refresh it
        if (_isOnlineCinemaSource && int.TryParse(value, out var cinemaYear))
        {
            _ = SyncOnlineCinemaYearAsync(cinemaYear);
        }

        FilterChannels();
    }

    private void OnChannelsChanged(ObservableCollection<ChannelViewModel> value)
    {
        RefreshGroups();
        FilterChannels();
        UpdateChannelCountText();
        _portalSeasonGroups = null;
    }

    private Dictionary<string, List<ChannelViewModel>>? _portalSeasonGroups;


    public List<ChannelViewModel> GetPortalSeasonSiblings(ChannelViewModel channel)
    {
        if (string.IsNullOrEmpty(channel.PortalRequest))
        {
            return new List<ChannelViewModel> { channel };
        }

        _portalSeasonGroups ??= BuildPortalSeasonGroups();
        if (ParsePortalSeasonName(channel.Name).BaseName is not { } baseName ||
            !_portalSeasonGroups.TryGetValue(baseName, out var group))
        {
            return new List<ChannelViewModel> { channel };
        }

        return group;
    }

    private Dictionary<string, List<ChannelViewModel>> BuildPortalSeasonGroups()
    {
        var groups = new Dictionary<string, List<ChannelViewModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var channel in Channels)
        {
            if (string.IsNullOrEmpty(channel.PortalRequest))
            {
                continue;
            }

            if (ParsePortalSeasonName(channel.Name).BaseName is not { } baseName)
            {
                continue;
            }

            if (!groups.TryGetValue(baseName, out var list))
            {
                groups[baseName] = list = new List<ChannelViewModel>();
            }

            list.Add(channel);
        }

        foreach (var key in groups.Keys.ToList())
        {
            groups[key].Sort((a, b) => SeasonSortKey(a.Name).CompareTo(SeasonSortKey(b.Name)));
        }

        return groups;
    }


    internal static (string? BaseName, (int From, int To)? Season) ParsePortalSeasonName(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            name,
            @"^(.*?)[\s.]*Сезон\s+(\d+)(?:\s*[-–]\s*(\d+))?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return (null, null);
        }

        var baseName = match.Groups[1].Value.Trim().TrimEnd('.').Trim();
        if (baseName.Length == 0)
        {
            return (null, null);
        }

        var from = int.Parse(match.Groups[2].Value);
        var to = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : from;
        return (baseName, (from, to));
    }

    private static (int From, int To) SeasonSortKey(string name) =>
        ParsePortalSeasonName(name).Season ?? (int.MaxValue, int.MaxValue);


    public event Func<ChannelViewModel, Task<int?>>? ParentalUnlockRequested;


    public Task<bool> CanPlayChannelAsync(ChannelViewModel channel)
        => EnsureChannelAllowedAsync(channel);


    private async Task<bool> EnsureChannelAllowedAsync(ChannelViewModel channel)
    {


        if (ParentalControlService.IsDailyLimitReached(AppSettings, DateTime.Now))
        {
            _logger.LogInformation(
                "Дневной лимит просмотра исчерпан — канал {Channel} не запущен.", channel.Name);
            DailyLimitBlocked?.Invoke(this, EventArgs.Empty);
            return false;
        }

        ParentalControlService.ClearChannelUnlock(AppSettings);

        if (ParentalControlService.IsChannelAccessible(AppSettings, channel.Name, channel.Group))
        {
            return true;
        }

        var handler = ParentalUnlockRequested;
        if (handler == null)
        {
            return false;
        }

        var result = await handler(channel);
        if (result == null)
        {
            _logger.LogInformation("Ввод PIN отменён — канал {Channel} не запущен.", channel.Name);
            return false;
        }

        _logger.LogInformation(
            "PIN принят: результат {Result}, запуск канала {Channel}.",
            result switch { -1 => "только этот канал", 0 => "до выключения", var n => $"{n} мин" },
            channel.Name);

        if (result == -1)
        {


            ParentalControlService.UnlockForChannel(AppSettings, channel.Name, channel.Group);
            return true;
        }

        ParentalControlService.Unlock(AppSettings, result == 0 ? null : result);
        SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }


    public event EventHandler? DailyLimitReached;


    public event EventHandler? DailyLimitBlocked;


    private bool _dailyLimitAnnounced;


    private int _unsavedWatchedSeconds;


    public void AddPlaybackWatchTime(int seconds)
    {
        var settings = AppSettings;
        if (!settings.ParentalControlEnabled || settings.ParentalDailyLimitMinutes <= 0)
        {
            return;
        }

        var localNow = DateTime.Now;
        ParentalControlService.AddWatchedSeconds(settings, seconds, localNow);
        _unsavedWatchedSeconds += seconds;
        if (_unsavedWatchedSeconds >= 60)
        {
            _unsavedWatchedSeconds = 0;
            SettingsSaveRequested?.Invoke(this, EventArgs.Empty);
        }

        var reached = ParentalControlService.IsDailyLimitReached(settings, DateTime.Now);
        if (reached && !_dailyLimitAnnounced)
        {
            _dailyLimitAnnounced = true;
            _logger.LogInformation("Дневной лимит просмотра ({Minutes} мин) исчерпан — остановка воспроизведения.",
                settings.ParentalDailyLimitMinutes);
            DailyLimitReached?.Invoke(this, EventArgs.Empty);
        }
        else if (!reached)
        {

            _dailyLimitAnnounced = false;
        }
    }


    public void FilterChannels()
    {
        var query = SearchQuery?.Trim() ?? string.Empty;

        IEnumerable<ChannelViewModel> filtered = Channels;

        if (!string.IsNullOrEmpty(query))
        {
            var matching = filtered.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
            // Online cinema: site-search hits for this query stay visible even
            // when the query doesn't appear in the title text
            if (_siteSearchHits.TryGetValue(query, out var hits))
            {
                filtered = matching.Union(filtered.Where(c => hits.Contains(c.PageUrl ?? string.Empty)));
            }
            else
            {
                filtered = matching;
            }
        }

        if (_isPortalSource)
        {

        }
        else
        {

            var selectedGroup = SelectedGroup;
            if (!string.IsNullOrEmpty(selectedGroup) && selectedGroup == FavoritesOption)
            {
                filtered = filtered.Where(c => c.IsFavorite);
            }
            else if (!string.IsNullOrEmpty(selectedGroup) && selectedGroup != AllGroupsOption)
            {
                filtered = filtered.Where(c => string.Equals(c.Group?.Trim(), selectedGroup, StringComparison.OrdinalIgnoreCase));
            }

            var selectedGenre = SelectedGenre;
            if (!string.IsNullOrEmpty(selectedGenre) && selectedGenre != AllGenresOption)
            {
                // Genre holds a comma-separated list — an item shows up under each of its genres
                filtered = filtered.Where(c =>
                    !string.IsNullOrEmpty(c.Genre) &&
                    c.Genre.Split(',').Any(g => string.Equals(g.Trim(), selectedGenre, StringComparison.OrdinalIgnoreCase)));
            }

            if (!string.IsNullOrEmpty(SelectedYear) && SelectedYear != AllYearsOption)
            {
                if (int.TryParse(SelectedYear, out var year))
                {
                    filtered = filtered.Where(c => c.Year == year);
                }
                else if (SelectedYear.Contains('-'))
                {
                    var parts = SelectedYear.Split('-', '–');
                    if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out var from) && int.TryParse(parts[1].Trim(), out var to))
                    {
                        filtered = filtered.Where(c => c.Year >= from && c.Year <= to);
                    }
                }
            }
        }


        filtered = filtered.OrderByDescending(c => c.IsFavorite);

        DisplayedChannels = new ObservableCollection<ChannelViewModel>(filtered);

        FilterChanged?.Invoke(this, EventArgs.Empty);
    }


    public void RefreshGroups(string? previouslySelected = null, bool keepFilters = false)
    {
        var groups = Channels
            .Select(c => c.Group)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Groups.Clear();
        Groups.Add(AllGroupsOption);
        Groups.Add(FavoritesOption);
        foreach (var group in groups)
        {
            Groups.Add(group);
        }

        SelectedGroup = (previouslySelected != null && Groups.Contains(previouslySelected))
            ? previouslySelected
            : AllGroupsOption;

        if (!_isPortalSource)
        {
            // Genre holds a comma-separated list — the filter lists individual genres
            var genres = Channels
                .Where(c => !string.IsNullOrWhiteSpace(c.Genre))
                .SelectMany(c => c.Genre!.Split(','))
                .Select(g => g.Trim())
                .Where(g => g.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Genres.Clear();
            Genres.Add(AllGenresOption);
            foreach (var genre in genres)
            {
                Genres.Add(genre);
            }


            if (!_isLoadingFiltered && (!keepFilters || !Genres.Contains(SelectedGenre)))
            {
                SelectedGenre = AllGenresOption;
            }
        }
        else if (!_isLoadingFiltered && (!keepFilters || !Genres.Contains(SelectedGenre)))
        {
            SelectedGenre = AllGenresOption;
        }

        OnPropertyChanged(nameof(IsGenreFilterVisible));

        if (!_isPortalSource)
        {
            var years = Channels
                .Where(c => c.Year > 0)
                .Select(c => c.Year)
                .Distinct()
                .OrderByDescending(y => y)
                .Select(y => y.ToString())
                .ToList();

            Years.Clear();
            Years.Add(AllYearsOption);
            foreach (var year in years)
            {
                Years.Add(year);
            }

            if (!_isLoadingFiltered && (!keepFilters || !Years.Contains(SelectedYear)))
            {
                SelectedYear = AllYearsOption;
            }
        }
        else if (!_isLoadingFiltered && (!keepFilters || !Years.Contains(SelectedYear)))
        {
            SelectedYear = AllYearsOption;
        }
        OnPropertyChanged(nameof(IsYearFilterVisible));
    }

    public void UpdateChannelCountText()
    {
        ChannelCountText = string.Format(L.T("Kanalov_0"), Channels.Count, Channels.Count);
    }


    public ChannelHistory ChannelHistory { get; } = new();

    private bool _navigatingBack;

    [RelayCommand]
    private async Task GoToPreviousChannelAsync()
    {
        var previous = ChannelHistory.Pop();
        if (previous == null)
        {
            return;
        }

        _navigatingBack = true;
        try
        {
            await SelectAndPlayChannelAsync(previous);
        }
        finally
        {
            _navigatingBack = false;
        }
    }


    [RelayCommand]
    private async Task SelectAndPlayChannelAsync(ChannelViewModel channel)
    {
        // Reentrancy guard: double click must not open two PIN dialogs or duplicate side effects
        if (_isSelectingChannel)
        {
            return;
        }

        _isSelectingChannel = true;
        try
        {
            await SelectAndPlayChannelCoreAsync(channel);
        }
        finally
        {
            _isSelectingChannel = false;
        }
    }

    private async Task SelectAndPlayChannelCoreAsync(ChannelViewModel channel)
    {
        if (!await EnsureChannelAllowedAsync(channel))
        {
            return;
        }


        if (!_navigatingBack &&
            Player.CurrentPlayerChannelId is int previousId &&
            previousId != channel.Id &&
            Channels.FirstOrDefault(c => c.Id == previousId) is { } previous)
        {
            ChannelHistory.Record(previous);
        }

        if (Player.CurrentPlayerChannelId != null &&
            (Player.CurrentPlayerChannelId != channel.Id || Player.IsArchivePlaying))
        {
            Player.Stop();
        }

        SelectedChannel = channel;

        var activePlaylist = AppSettings.Playlists.FirstOrDefault(p => p.Id == AppSettings.ActivePlaylistId);
        if (activePlaylist != null)
        {
            activePlaylist.LastWatchedChannel = channel.Name;
        }
        AppSettings.LastWatchedChannel = channel.Name;
        SettingsSaveRequested?.Invoke(this, EventArgs.Empty);


        await PlayChannelAsync(channel);

        await EpgViewModel.LoadEPGForChannelAsync(channel.Id);
        ApplyReminderFlags();
        ScrollToProgramRequested?.Invoke(this, EventArgs.Empty);
    }


    [RelayCommand]
    private async Task PlayArchiveEntryAsync(EPGEntry entry)
    {
        var channel = SelectedChannel;
        if (channel == null || string.IsNullOrWhiteSpace(channel.StreamUrl))
        {
            ArchivePlayErrorRequested?.Invoke(this, L.T("U_Kanala_Net_URL_Potoka_Arkhiv"));
            return;
        }

        if (!await EnsureChannelAllowedAsync(channel))
        {
            return;
        }

        if (entry.StartTime > DateTime.Now)
        {
            ArchivePlayErrorRequested?.Invoke(this, L.T("Eta_Peredacha_Eshche_Ne_Nachalas"));
            return;
        }

        var archiveUrl = ArchiveUrlBuilder.BuildUrl(channel.StreamUrl, entry.StartTime);
        await Player.StartPlaybackAsync(channel, archiveUrl, entry);


        if (IsEpgVisible)
        {
            IsEpgVisible = false;
            EpgVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }


    public async Task<bool> PlayChannelAsync(ChannelViewModel channel)
        => await PlayChannelAsync(channel, interactive: true);


    [RelayCommand]
    private void ToggleEpg()
    {
        IsEpgVisible = !IsEpgVisible;
        EpgVisibilityChanged?.Invoke(this, EventArgs.Empty);
    }


    public async Task SaveSettingsAsync()
    {
        try
        {


            await _settingsService.SaveAsync(AppSettings).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SaveSettingsAsync: не удалось сохранить настройки.");
        }
    }
}
