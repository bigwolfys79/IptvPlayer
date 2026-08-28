using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace IptvPlayer.Dialogs;

/// <summary>Строка списка эпизодов: порядковый номер и название серии.</summary>
public class EpisodePickerItem
{
    public string Number { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public PortalEpisode Episode { get; set; } = new();
}

/// <summary>
/// Диалог выбора серии сериала портала: шапка (постер, название, описание),
/// комбобокс сезона (сезоны — соседние карточки каталога, серийные списки
/// подгружаются flick'ом) и список серий. Клик по серии — выбрать и закрыть;
/// «Смотреть с первой» — первый эпизод; «Отмена» — ничего не выбирать.
/// Возвращает выбранную пару (карточка сезона, эпизод).
/// </summary>
public sealed partial class EpisodePickerDialog : UserControl
{
    private readonly IVideoPortalService _videoPortalService =
        App.Services.GetRequiredService<IVideoPortalService>();
    private readonly MainPageViewModel _viewModel =
        App.Services.GetRequiredService<MainPageViewModel>();

    private ContentDialog? _hostDialog;
    private bool _updatingSeasons;
    private PortalFlickResult _flick;

    /// <summary>Выбранный сезон (карточка каталога) + эпизод + его список серий.</summary>
    private (ChannelViewModel Channel, PortalEpisode Episode, System.Collections.Generic.List<PortalEpisode> Episodes)? _result;

    public ObservableCollection<EpisodePickerItem> Episodes { get; } = new();

    private EpisodePickerDialog(ChannelViewModel channel, PortalFlickResult flick)
    {
        _flick = flick;
        Seasons = _viewModel.GetPortalSeasonSiblings(channel);
        CurrentSeason = channel;
        InitializeComponent();
    }

    /// <summary>Сезоны сериала (включая текущий), отсортированные по номерам.</summary>
    private System.Collections.Generic.List<ChannelViewModel> Seasons { get; }

    private ChannelViewModel CurrentSeason { get; set; }

    /// <summary>
    /// Показывает диалог и возвращает выбранную пару сезон/эпизод
    /// (null — отменено).
    /// </summary>
    public static async Task<(ChannelViewModel Channel, PortalEpisode Episode, System.Collections.Generic.List<PortalEpisode> Episodes)?> PickAsync(
        XamlRoot xamlRoot, ChannelViewModel channel, PortalFlickResult flick)
    {
        var control = new EpisodePickerDialog(channel, flick);
        await control.LoadAsync();

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = L.T("Vybor_Serii"),
            Content = control
        };
        control._hostDialog = dialog;
        await dialog.ShowAsync();
        return control._result;
    }

    private Task LoadAsync()
    {
        SerialTitleText.Text = _flick.SerialTitle;
        SerialDescriptionText.Text = _flick.Description ?? string.Empty;
        SerialDescriptionText.Visibility =
            string.IsNullOrEmpty(_flick.Description) ? Visibility.Collapsed : Visibility.Visible;

        if (Uri.TryCreate(_flick.PosterUrl, UriKind.Absolute, out var posterUri))
        {
            PosterImage.Source = new BitmapImage(posterUri);
        }
        else
        {
            PosterImage.Visibility = Visibility.Collapsed;
        }

        CancelButton.Content = L.T("Otmena_Lbl");
        PlayFirstButton.Content = L.T("Smotret_S_Pervoy_Lbl");
        SeasonLabel.Text = L.T("Sezon_Lbl");

        // Комбобокс сезона — только когда сезонов в каталоге больше одного.
        SeasonPanel.Visibility = Seasons.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (Seasons.Count > 1)
        {
            _updatingSeasons = true;
            foreach (var season in Seasons)
            {
                SeasonCombo.Items.Add(new ComboBoxItem
                {
                    Content = SeasonLabelOf(season.Name),
                    Tag = season,
                    IsSelected = ReferenceEquals(season, CurrentSeason)
                });
            }

            _updatingSeasons = false;
        }

        FillEpisodes();
        return Task.CompletedTask;
    }

    /// <summary>«Название. Сезон 3. (2021)» → «Сезон 3»; без пометки — как есть.</summary>
    private static string SeasonLabelOf(string name) =>
        MainPageViewModel.ParsePortalSeasonName(name).Season is { } season
            ? (season.From == season.To
                ? string.Format(L.T("Sezon_0"), season.From)
                : string.Format(L.T("Sezon_0_1"), season.From, season.To))
            : name;

    private void FillEpisodes()
    {
        Episodes.Clear();
        var index = 1;
        foreach (var episode in _flick.Episodes)
        {
            Episodes.Add(new EpisodePickerItem
            {
                Number = index.ToString(),
                Title = episode.Title,
                Episode = episode
            });
            index++;
        }

        EpisodesCountText.Text = string.Format(L.T("Seriy_0"), _flick.Episodes.Count, _flick.Episodes.Count);

        if (Episodes.Count > 0)
        {
            EpisodesList.ScrollIntoView(Episodes[0]);
        }
    }

    private async void SeasonCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSeasons ||
            sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: ChannelViewModel season } } ||
            ReferenceEquals(season, CurrentSeason))
        {
            return;
        }

        // Серии выбранного сезона — отдельный flick-запрос (шапка сериала в
        // диалоге остаётся от исходной карточки, они различаются только
        // списком эпизодов).
        CurrentSeason = season;
        SeasonLoading.Visibility = Visibility.Visible;
        try
        {
            var playlist = _viewModel.AppSettings.Playlists
                .FirstOrDefault(p => p.Id == _viewModel.AppSettings.ActivePlaylistId);
            if (playlist != null && !string.IsNullOrEmpty(season.PortalRequest))
            {
                _flick = await _videoPortalService.ResolveEpisodesAsync(playlist, season.PortalRequest);
                FillEpisodes();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Диалог серий: не удалось загрузить сезон «{Season}».", season.Name);
        }
        finally
        {
            SeasonLoading.Visibility = Visibility.Collapsed;
        }
    }

    private void EpisodeItem_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EpisodePickerItem item)
        {
            _result = (CurrentSeason, item.Episode, _flick.Episodes);
            _hostDialog?.Hide();
        }
    }

    private void PlayFirstButton_Click(object sender, RoutedEventArgs e)
    {
        _result = (CurrentSeason, _flick.Episodes.FirstOrDefault()!, _flick.Episodes);
        _hostDialog?.Hide();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _result = null;
        _hostDialog?.Hide();
    }
}
