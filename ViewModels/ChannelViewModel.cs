using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IptvPlayer.ViewModels;

/// <summary>
/// Канал для UI. Все изменяемые свойства — INotifyPropertyChanged через
/// SetProperty (CommunityToolkit.Mvvm): раньше каждое писалось вручную на
/// ~12 строк, и рассинхрон «поле/свойство/уведомление» был реальным
/// источником багов. Ручные свойства (а не [ObservableProperty]) выбраны
/// осознанно — сгенерированные генератором не совместимы с AOT/WinRT-ABI
/// (MVVMTK0045). Вычисляемые свойства (HasArchive и пр.) — обычные, их
/// обновление вызывается из сеттеров полей-источников.
/// </summary>
public partial class ChannelViewModel : ObservableObject
{
    // Все свойства — ручные (поле + SetProperty): сгенерированные
    // [ObservableProperty] в WinUI-сценариях не создают WinRT-проекторов
    // (предупреждение MVVMTK0045), а семантика уведомления та же.
    // Раньше каждое писалось по ~12 строк вручную, и рассинхрон
    // «поле/свойство/уведомление» был реальным источником багов;
    // SetProperty сводит это к минимуму.
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

    /// <summary>
    /// Описание текущей передачи из EPG — показывается в верхних оверлеях
    /// под названием передачи (аналог описания фильма портала).
    /// </summary>
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

    /// <summary>
    /// Request-объект элемента видео-портала (JSON, как пришёл из API).
    /// Заполнен только у источников-порталов; StreamUrl у таких элементов
    /// null до первого клика — по клику поток запрашивается у портала
    /// (VideoPortalService.ResolveStreamAsync) и результат не кэшируется.
    /// </summary>
    public string? PortalRequest
    {
        get => _portalRequest;
        set => SetProperty(ref _portalRequest, value);
    }

    /// <summary>
    /// Элемент каталога портала (фильм/сериал), а не ТВ-канал: у таких
    /// нет и не может быть EPG — все проходы EPG (пересчёт текущих
    /// передач, сопоставление с XMLTV, добор логотипов) их пропускают.
    /// В VOD-плейлистах таких элементов основная масса (например,
    /// 22 009 из 22 009 в «Ilock video»).
    /// </summary>
    public bool IsPortalItem => !string.IsNullOrEmpty(_portalRequest);

    /// <summary>
    /// Локальный видеофайл (карточка «Видео» на хабе), а не канал из
    /// плейлиста: StreamUrl — «сырой» путь диска, позиция досмотра хранится
    /// под отдельным ключом «file::путь» и не попадает в списки портала.
    /// </summary>
    public bool IsLocalFile { get; set; }

    private string? _description;

    /// <summary>
    /// Описание элемента каталога портала (аннотация фильма/сериала).
    /// Показывается в верхнем оверлее; у обычных каналов M3U — null.
    /// </summary>
    public string? Description
    {
        get => _description;
        set => SetProperty(ref _description, value);
    }

    private int _year;

    /// <summary>
    /// Год выпуска элемента портала (0 — не указан). Используется
    /// сортировкой списка по году.
    /// </summary>
    public int Year
    {
        get => _year;
        set => SetProperty(ref _year, value);
    }

    private string? _genre;

    /// <summary>
    /// Жанр элемента портала (из фильтра manifest.controls.filters).
    /// null у M3U-каналов и элементов без жанра.
    /// </summary>
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

    /// <summary>
    /// Доля прошедшей части текущей передачи (0..1) для тонкой полосы
    /// прогресса в строке канала и в шапке полноэкранного оверлея. Течёт
    /// со временем, поэтому уведомление поднимается таймером обновления
    /// текущей передачи (RefreshCurrentProgramProgress) и при смене самой
    /// передачи — не только сеттером CurrentEPGEntry.
    /// </summary>
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

    /// <summary>Идёт ли сейчас какая-то передача (для видимости полосы прогресса).</summary>
    public bool HasCurrentProgram => CurrentEPGEntry != null;

    /// <summary>
    /// Периодическое уведомление для x:Bind-полос прогресса: значение
    /// CurrentProgramProgress зависит от стененных часов, а не от свойств.
    /// </summary>
    public void RefreshCurrentProgramProgress()
    {
        OnPropertyChanged(nameof(CurrentProgramProgress));
    }

    /// <summary>
    /// Глубина архива передач канала в днях (атрибут tvg-rec / catchup-days
    /// плейлиста; 0 — архива нет). Определяет зелёную точку в списке каналов.
    /// </summary>
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

    /// <summary>
    /// Канал в избранном (звёздочка в списке). Хранится в настройках по имени
    /// канала — Id нестабилен между запусками. Избранные показываются первыми
    /// в списке и в группе «★ Избранное» полноэкранного оверлея.
    /// </summary>
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

    /// <summary>Есть ли у канала архив передач (tvg-rec &gt; 0).</summary>
    public bool HasArchive => CatchupDays > 0;

    /// <summary>Подсказка точки-индикатора архива в списке каналов.</summary>
    public string ArchiveToolTip => string.Format(
        Services.L.T("Tip_ArchiveAvailable"), CatchupDays);

    /// <summary>Подсказка звёздочки в списке каналов.</summary>
    public string FavoriteToolTip => IsFavorite
        ? Services.L.T("Ubrat_Iz_Izbrannogo")
        : Services.L.T("Dobavit_V_Izbrannoe");
}
