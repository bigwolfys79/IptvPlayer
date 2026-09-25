using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using IptvPlayer.Converters;
using IptvPlayer.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IptvPlayer.Services;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.ViewModels;

public partial class EpgViewModel : ObservableObject
{
    private static readonly TimeSpan BackwardSpan = TimeSpan.FromHours(72);
    private static readonly TimeSpan ForwardSpan = TimeSpan.FromHours(48);
    private const double PixelsPerHour = 100;

    private readonly IEPGService _epgService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<EpgViewModel> _logger;
    private DateTime _windowStart;
    private List<int> _timeScaleHours = new();
    private bool _isLoading;
    private readonly System.Threading.SemaphoreSlim _loadLock = new(1, 1);

    public EpgViewModel(IEPGService epgService, ISettingsService settingsService, ILogger<EpgViewModel> logger)
    {
        _epgService = epgService;
        _settingsService = settingsService;
        _logger = logger;
        ResetWindowToNow();
        LoadEpgSourcesFromSettings();
    }

    private ObservableCollection<ChannelViewModel> _channels = new();

    public ObservableCollection<ChannelViewModel> Channels
    {
        get => _channels;
        set => SetProperty(ref _channels, value);
    }

    private ObservableCollection<ChannelViewModel> _filteredChannels = new();

    public ObservableCollection<ChannelViewModel> FilteredChannels
    {
        get => _filteredChannels;
        set => SetProperty(ref _filteredChannels, value);
    }

    private string _epgSource = string.Empty;

    public string EpgSource
    {
        get => _epgSource;
        set => SetProperty(ref _epgSource, value);
    }

    private List<string> _ePGSources = new();

    public List<string> EPGSources
    {
        get => _ePGSources;
        set => SetProperty(ref _ePGSources, value);
    }

    private string? _selectedEPGSource;

    public string? SelectedEPGSource
    {
        get => _selectedEPGSource;
        set => SetProperty(ref _selectedEPGSource, value);
    }


    public DateTime WindowStart
    {
        get => _windowStart;
        private set
        {
            if (_windowStart != value)
            {
                _windowStart = value;
                EpgTimelineScale.WindowStart = value;
                EpgTimelineScale.PixelsPerHour = PixelsPerHour;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WindowEnd));
                RecalculateTimeScaleHours();
                RebindTimelineEntries();
            }
        }
    }

    public DateTime WindowEnd => WindowStart + BackwardSpan + ForwardSpan;


    public List<int> TimeScaleHours
    {
        get => _timeScaleHours;
        private set
        {
            if (_timeScaleHours != value)
            {
                _timeScaleHours = value;
                OnPropertyChanged();
            }
        }
    }


    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading != value)
            {
                _isLoading = value;
                OnPropertyChanged();
            }
        }
    }

    private int _activeLoadOperations;

    private void BeginLoadOperation()
    {
        _activeLoadOperations++;
        IsLoading = true;
    }

    private void EndLoadOperation()
    {
        if (_activeLoadOperations > 0)
        {
            _activeLoadOperations--;
        }

        if (_activeLoadOperations == 0)
        {
            IsLoading = false;
        }
    }


    public event EventHandler? EpgReloaded;

    private async void LoadEpgSourcesFromSettings()
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            var activeSources = settings.GetActiveEpgSources();
            var enabledSources = activeSources.Where(s => s.IsEnabled).ToList();

            // All sources disabled is a user decision — do not re-enable anything here

            EPGSources = enabledSources.Select(s => s.Url).ToList();
            if (EPGSources.Count > 0)
            {
                SelectedEPGSource = EPGSources.First();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadEpgSourcesFromSettings: не удалось загрузить источники EPG из настроек.");
        }
    }

    public void SetChannels(IEnumerable<ChannelViewModel> channels)
    {
        // Wholesale replacement: INPC property, no CollectionChanged subscribers or XAML bindings
        Channels = new ObservableCollection<ChannelViewModel>(channels);
        ApplyFilter();
    }

    public void ApplyFilter(string? query = null)
    {
        var filtered = string.IsNullOrEmpty(query)
            ? Channels
            : Channels.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        FilteredChannels = new ObservableCollection<ChannelViewModel>(filtered);
    }

    public void ApplyEPGSource()
    {
        if (SelectedEPGSource != null)
        {
            EpgSource = SelectedEPGSource;
        }
    }

    public async Task LoadEPGAsync()
    {

        await _loadLock.WaitAsync();
        try
        {
            BeginLoadOperation();
            var channels = await _epgService.GetChannelsAsync();
            SetChannels(channels);
            await RecalculateCurrentProgramsAsync();
            EpgReloaded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadEPGAsync: не удалось загрузить EPG.");
            throw;
        }
        finally
        {
            EndLoadOperation();
            _loadLock.Release();
        }
    }


    public async Task ReloadForPlaylistAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            BeginLoadOperation();
            await _epgService.ReloadSourcesAsync();
            var channels = await _epgService.GetChannelsAsync();
            SetChannels(channels);
            await RecalculateCurrentProgramsAsync();
            EpgReloaded?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            EndLoadOperation();
            _loadLock.Release();
        }
    }

    public async Task LoadEPGForChannelAsync(int channelId)
    {

        await _loadLock.WaitAsync();
        try
        {
            var entries = await _epgService.GetEPGEntriesAsync(channelId);
            var channel = Channels.FirstOrDefault(c => c.Id == channelId);
            if (channel == null || channel.IsVodCatalogItem)
            {
                return;
            }

            channel.EPGEntries.Clear();
            foreach (var entry in entries)
            {
                entry.IsCurrent = false;
                channel.EPGEntries.Add(entry);
            }

            var now = DateTime.Now;
            var current = entries.FirstOrDefault(e => e.StartTime <= now && now < e.EndTime);
            _logger.LogInformation(
                "EPG канал {ChannelId}: записей {Count}, текущая «{Title}», описание {DescLen} симв.",
                channelId, entries.Count, current?.ProgramName ?? "—", current?.Description?.Length ?? 0);
            if (current != null)
            {
                current.IsCurrent = true;
                channel.CurrentProgramTitle = current.ProgramName;
                channel.CurrentProgramDescription = current.Description ?? string.Empty;
                channel.CurrentEPGEntry = current;
            }
            else
            {
                channel.CurrentProgramTitle = NoProgramTitle;
                channel.CurrentProgramDescription = string.Empty;
                channel.CurrentEPGEntry = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadEPGForChannelAsync: не удалось загрузить EPG канала {ChannelId}.", channelId);
            throw;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public async Task RefreshEPGAsync()
    {
        BeginLoadOperation();
        try
        {

            await Task.Run(() => _epgService.RefreshEPGAsync());
            await LoadEPGAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RefreshEPGAsync: не удалось обновить EPG.");
            throw;
        }
        finally
        {
            EndLoadOperation();
        }
    }

    private void ResetWindowToNow()
    {
        WindowStart = DateTime.Now - BackwardSpan;
    }

    private void RecalculateTimeScaleHours()
    {
        var totalHours = (int)(BackwardSpan + ForwardSpan).TotalHours;
        TimeScaleHours = Enumerable.Range(0, totalHours)
            .Select(offset => WindowStart.AddHours(offset).Hour)
            .ToList();
    }


    private const int YieldEveryNChannels = 50;


    private static string NoProgramTitle => L.T("Programma_Nedostupna_Lbl");

    private async Task RecalculateCurrentProgramsAsync()
    {
        var now = DateTime.Now;
        var processed = 0;

        foreach (var channel in Channels)
        {

            if (channel.IsPortalItem || channel.IsVodCatalogItem)
            {
                continue;
            }

            var current = await _epgService.GetCurrentProgramAsync(channel.Id);

            channel.CurrentProgramTitle = current?.ProgramName ?? NoProgramTitle;
            channel.CurrentProgramDescription = current?.Description ?? string.Empty;
            channel.CurrentEPGEntry = current;
            if (current != null)
            {
                current.IsCurrent = true;
                current.RefreshLiveProgress();
            }

            channel.RefreshCurrentProgramProgress();

            processed++;
            if (processed % YieldEveryNChannels == 0)
            {
                await Task.Yield();
            }
        }
    }


    private int _refreshLightRuns;

    public async Task RefreshCurrentProgramsLightAsync()
    {
        if (IsLoading || Interlocked.CompareExchange(ref _refreshLightRuns, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await RefreshCurrentProgramsLightCoreAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RefreshCurrentProgramsLightAsync: не удалось обновить текущие программы.");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshLightRuns, 0);
        }
    }

    private async Task RefreshCurrentProgramsLightCoreAsync()
    {
        var processed = 0;

        foreach (var channel in Channels)
        {


            if (channel.IsPortalItem || channel.IsVodCatalogItem)
            {
                continue;
            }

            var current = await _epgService.GetCurrentProgramAsync(channel.Id);

            if (!string.Equals(channel.CurrentProgramTitle, current?.ProgramName ?? NoProgramTitle, StringComparison.Ordinal))
            {

                if (channel.CurrentEPGEntry != null)
                {
                    channel.CurrentEPGEntry.IsCurrent = false;
                }
                if (current != null)
                {
                    current.IsCurrent = true;
                }

                channel.CurrentProgramTitle = current?.ProgramName ?? NoProgramTitle;
                channel.CurrentProgramDescription = current?.Description ?? string.Empty;
                channel.CurrentEPGEntry = current;
            }

            channel.RefreshCurrentProgramProgress();
            current?.RefreshLiveProgress();

            processed++;
            if (processed % YieldEveryNChannels == 0)
            {
                await Task.Yield();
            }
        }
    }


    private void RebindTimelineEntries()
    {
        foreach (var channel in Channels)
        {
            var entries = channel.EPGEntries.ToList();
            channel.EPGEntries.Clear();
            foreach (var entry in entries)
            {
                channel.EPGEntries.Add(entry);
            }
        }
    }


    [RelayCommand]
    private async Task RefreshEpgAsync()
    {
        if (IsLoading)
        {
            return;
        }
        await RefreshEPGAsync();
    }
}
