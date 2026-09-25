using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IptvPlayer.ViewModels;


public partial class ChannelViewModel : ObservableObject
{

    private string _name = string.Empty;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private int _id;

    public int Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    private bool _isLive;

    public bool IsLive
    {
        get => _isLive;
        set => SetProperty(ref _isLive, value);
    }

    private string? _currentProgramTitle;

    public string? CurrentProgramTitle
    {
        get => _currentProgramTitle;
        set => SetProperty(ref _currentProgramTitle, value);
    }

    private string? _currentProgramDescription;


    public string? CurrentProgramDescription
    {
        get => _currentProgramDescription;
        set => SetProperty(ref _currentProgramDescription, value);
    }

    private bool _isPlaying;

    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetProperty(ref _isPlaying, value);
    }

    private ObservableCollection<IptvPlayer.Models.EPGEntry> _ePGEntries = new();

    public ObservableCollection<IptvPlayer.Models.EPGEntry> EPGEntries
    {
        get => _ePGEntries;
        set => SetProperty(ref _ePGEntries, value);
    }

    private string? _streamUrl;

    public string? StreamUrl
    {
        get => _streamUrl;
        set => SetProperty(ref _streamUrl, value);
    }

    private string? _portalRequest;


    public string? PortalRequest
    {
        get => _portalRequest;
        set => SetProperty(ref _portalRequest, value);
    }


    public bool IsPortalItem => !string.IsNullOrEmpty(_portalRequest);


    // m3u VOD catalog entry: no EPG, no live-program subtitle
    public bool IsVodCatalogItem { get; set; }


    public bool IsLocalFile { get; set; }

    private string? _description;


    public string? Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    private int _year;


    public int Year
    {
        get => _year;
        set => SetProperty(ref _year, value);
    }

    private string? _genre;


    public string? Genre
    {
        get => _genre;
        set => SetProperty(ref _genre, value);
    }

    private string? _logoUrl;

    public string? LogoUrl
    {
        get => _logoUrl;
        set => SetProperty(ref _logoUrl, value);
    }

    private string? _group;

    public string? Group
    {
        get => _group;
        set => SetProperty(ref _group, value);
    }

    private string? _tvgId;

    public string? TvgId
    {
        get => _tvgId;
        set => SetProperty(ref _tvgId, value);
    }

    private IptvPlayer.Models.EPGEntry? _currentEPGEntry;

    public IptvPlayer.Models.EPGEntry? CurrentEPGEntry
    {
        get => _currentEPGEntry;
        set
        {
            if (SetProperty(ref _currentEPGEntry, value))
            {
                OnPropertyChanged(nameof(CurrentProgramProgress));
                OnPropertyChanged(nameof(HasCurrentProgram));
            }
        }
    }


    public double CurrentProgramProgress
    {
        get
        {
            var entry = CurrentEPGEntry;
            if (entry == null)
            {
                return 0;
            }
            var total = (entry.EndTime - entry.StartTime).TotalSeconds;
            if (total <= 0)
            {
                return 0;
            }
            return Math.Clamp((DateTime.Now - entry.StartTime).TotalSeconds / total, 0.0, 1.0);
        }
    }


    public bool HasCurrentProgram => CurrentEPGEntry != null;


    public void RefreshCurrentProgramProgress()
    {
        OnPropertyChanged(nameof(CurrentProgramProgress));
    }


    private int _catchupDays;

    public int CatchupDays
    {
        get => _catchupDays;
        set
        {
            if (SetProperty(ref _catchupDays, value))
            {
                OnPropertyChanged(nameof(HasArchive));
                OnPropertyChanged(nameof(ArchiveToolTip));
            }
        }
    }


    private bool _isFavorite;

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                OnPropertyChanged(nameof(FavoriteToolTip));
            }
        }
    }


    public bool HasArchive => CatchupDays > 0;


    public string ArchiveToolTip => string.Format(
        Services.L.T("Tip_ArchiveAvailable"), CatchupDays);


    public string FavoriteToolTip => IsFavorite
        ? Services.L.T("Ubrat_Iz_Izbrannogo")
        : Services.L.T("Dobavit_V_Izbrannoe");
}
