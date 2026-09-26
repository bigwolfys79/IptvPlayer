using System.Collections.Generic;
using System.Linq;

namespace IptvPlayer.Models;


public class AppSettings
{
    public List<EPGSource> EpgSources { get; set; } = new()
    {


        new EPGSource { Url = "http://epg.one/epg.xml.gz", IsEnabled = true },
    };


    public string? PlaylistUrl { get; set; }


    public List<PlaylistSource> Playlists { get; set; } = new();


    public int ActivePlaylistId { get; set; }


    public int PlaylistRefreshDays { get; set; } = 1;


    public int EpgRefreshDays { get; set; } = 1;


    public int EpgArchiveDaysBack { get; set; } = 3;


    public double Volume { get; set; } = 1.0;


    public string DecoderMode { get; set; } = "Software";


    public int PreferredQuality { get; set; } = 0;


    public string AudioNormalization { get; set; } = "Dynamic";


    // ISO-ish language codes in priority order, comma-separated ("rus,ukr,eng"); empty = keep stream default
    public string PreferredAudioLanguage { get; set; } = "rus";


    public int AudioVolumeBoost { get; set; } = 100;


    public string VideoUpscaler { get; set; } = "Off";


    public bool FrameServerRender { get; set; } = false;


    public int ReminderMinutes { get; set; } = 5;


    public string VideoStretch { get; set; } = "Uniform";


    public List<ProgramReminder> ProgramReminders { get; set; } = new();


    public List<string> FavoriteChannels { get; set; } = new();


    public string? LastWatchedChannel { get; set; }


    public string Theme { get; set; } = "Default";


    public string Language { get; set; } = "ru";


    public int ReadAheadSeconds { get; set; } = 15;


    public int VodReadAheadSeconds { get; set; } = 8;


    public WindowPlacement? WindowPlacement { get; set; }


    public bool ChannelListPosterView { get; set; }

    public double ChannelListWidth { get; set; }


    public List<ScheduledRecording> ScheduledRecordings { get; set; } = new();


    public string? RecordingsFolder { get; set; }


    public string? UpdateCheckUrl { get; set; }


    public bool AutoUpdateEnabled { get; set; } = true;


    public DateTime? LastUpdateCheckUtc { get; set; }


    public bool CloseToTray { get; set; }


    public bool MinimizeToTray { get; set; }


    public List<InterruptedRecording> InterruptedRecordings { get; set; } = new();


    public Dictionary<string, VodResumePosition> VodResumePositions { get; set; } = new();

    public bool ParentalControlEnabled { get; set; }
    public string? ParentalControlPinHash { get; set; }
    public List<string> ParentalControlBlockedGroups { get; set; } = new();
    public DateTime? ParentalControlUnlockedUntilUtc { get; set; }


    [System.Text.Json.Serialization.JsonIgnore]
    public string? ParentalTempUnlockedChannel { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? ParentalTempUnlockedGroup { get; set; }


    public int ParentalDailyLimitMinutes { get; set; }


    public string? ParentalWatchedDate { get; set; }


    public int ParentalWatchedSeconds { get; set; }


    public bool StatsOverlayVisible { get; set; }


    public bool DiagnosticStreamProxy { get; set; }


    public bool TempDiagnosticsEnabled { get; set; }


    public bool FileLoggingEnabled { get; set; } = true;


    public bool ShowHubOnStartup { get; set; } = true;


    // Online-cinema source master switch: off hides the catalog and stops
    // all its background collection
    public bool OnlineCinemaEnabled { get; set; } = true;


    // How often (minutes) the visible online-cinema list is re-read from the
    // catalog DB while background collection runs; 0 = never during playback
    public int OnlineCinemaListRefreshMinutes { get; set; } = 60;


    // How often (seconds) the background collector deepens the catalog by one
    // page while a film is playing
    public int OnlineCinemaCollectIntervalSeconds { get; set; } = 120;


    public int SleepTimerMinutes { get; set; }


    public string SleepTimerAction { get; set; } = "Stop";


    public List<EPGSource> GetActiveEpgSources()
    {
        var playlist = Playlists.FirstOrDefault(p => p.Id == ActivePlaylistId);
        // Portals, m3u VOD catalogs and online cinema have no broadcast
        // schedule — skip the pointless XMLTV download unless the user
        // assigned sources explicitly
        if (playlist != null &&
            (playlist.IsPortal || playlist.IsVodCatalog || playlist.IsOnlineCinema) &&
            playlist.EpgSources.Count == 0)
        {
            return new List<EPGSource>();
        }

        return playlist?.EpgSources.Count > 0 ? playlist.EpgSources : EpgSources;
    }
}


public class PlaylistSource
{
    public int Id { get; set; }


    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;


    public string Type { get; set; } = "m3u";


    public string? PortalKey { get; set; }


    public bool IsPortal => string.Equals(Type, "portal", StringComparison.OrdinalIgnoreCase);


    public bool IsVodCatalog => string.Equals(Type, "m3u-vod", StringComparison.OrdinalIgnoreCase);


    public bool IsOnlineCinema => string.Equals(Type, "online-cinema", StringComparison.OrdinalIgnoreCase);


    public string? LastWatchedChannel { get; set; }


    public List<EPGSource> EpgSources { get; set; } = new();
}
