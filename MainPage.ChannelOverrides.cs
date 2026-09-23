using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer;


public sealed partial class MainPage : Page
{


    // Apply saved move/remove overrides after playlist load
    private async Task<List<ChannelViewModel>> ApplyChannelOverridesAsync(List<ChannelViewModel> channels)
    {
        var playlist = _activePlaylist;
        if (playlist == null || channels.Count == 0)
        {
            return channels;
        }

        try
        {
            var overrides = await _playlistCacheService.GetChannelOverridesAsync(playlist.Id);
            if (overrides.Count == 0)
            {
                return channels;
            }

            var applied = ChannelOverrideMatcher.Apply(channels, overrides);
            if (applied.Count != channels.Count)
            {
                _logger.LogInformation(
                    "Правки каналов плейлиста {PlaylistId}: применено {Count} удалений.",
                    playlist.Id, channels.Count - applied.Count);
            }
            return applied;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Применение правок каналов плейлиста {PlaylistId}.", playlist.Id);
            return channels;
        }
    }

    private void ChannelMoveMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: ChannelViewModel channel })
        {
            _ = MoveChannelToGroupAsync(channel);
        }
    }

    private void ChannelDeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: ChannelViewModel channel })
        {
            _ = DeleteChannelAsync(channel);
        }
    }


    // Fill context menu texts
    private void ChannelContextMenu_Opening(object? sender, object e)
    {
        if (sender is MenuFlyout { Items.Count: >= 2 } menu)
        {
            if (menu.Items[0] is MenuFlyoutItem moveItem)
            {
                moveItem.Text = L.T("Perenesti_V_Gruppu");
            }
            if (menu.Items[1] is MenuFlyoutItem deleteItem)
            {
                deleteItem.Text = L.T("Udalit_Kanal_Iz_Spiska");
            }
        }
    }

    // Move channel to another group
    private async Task MoveChannelToGroupAsync(ChannelViewModel channel)
    {
        if (!await EnsurePinApprovedForGroupAsync(channel.Group))
        {
            return;
        }

        var groups = ViewModel.Channels
            .Select(c => c.Group?.Trim())
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();

        var newGroup = await Dialogs.MoveChannelDialog.PickAsync(
            Content.XamlRoot, channel.Name, groups, channel.Group?.Trim());
        if (newGroup == null ||
            string.Equals(newGroup, channel.Group?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_activePlaylist == null || string.IsNullOrEmpty(channel.StreamUrl))
        {
            return;
        }

        var originalGroup = channel.Group?.Trim();
        channel.Group = newGroup;

        try
        {
            await _playlistCacheService.UpsertChannelOverrideAsync(new PlaylistDatabaseService.ChannelOverride(
                _activePlaylist.Id, channel.StreamUrl, channel.Name, originalGroup, channel.TvgId,
                newGroup, IsDeleted: false, CreatedAtUtc: DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Сохранение переноса канала «{Channel}».", channel.Name);
        }

        await RebuildChannelRepositoryAsync();
        ShowActionToast(string.Format(L.T("Kanal_Perenesen_V_Gruppu"), channel.Name, newGroup));
    }

    // Remove channel from list
    private async Task DeleteChannelAsync(ChannelViewModel channel)
    {
        if (!await EnsurePinApprovedForGroupAsync(channel.Group))
        {
            return;
        }

        var confirm = new ThemedContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = L.T("Udalit_Kanal_Iz_Spiska"),
            Content = new TextBlock
            {
                Text = string.Format(L.T("Udalit_Kanal_Podtverzhdenie"), channel.Name),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = L.T("Udalit_Lbl"),
            CloseButtonText = L.T("Otmena_Lbl")
        };
        if (await DialogQueue.ShowAsync(confirm) != ContentDialogResult.Primary)
        {
            return;
        }

        if (_activePlaylist == null || string.IsNullOrEmpty(channel.StreamUrl))
        {
            return;
        }

        // Keep playback running even if the deleted channel was selected
        ViewModel.Channels.Remove(channel);

        try
        {
            await _playlistCacheService.UpsertChannelOverrideAsync(new PlaylistDatabaseService.ChannelOverride(
                _activePlaylist.Id, channel.StreamUrl, channel.Name, channel.Group?.Trim(), channel.TvgId,
                NewGroup: null, IsDeleted: true, CreatedAtUtc: DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Сохранение удаления канала «{Channel}».", channel.Name);
        }

        await RebuildChannelRepositoryAsync();
        ShowActionToast(string.Format(L.T("Kanal_Udalen_Iz_Spiska"), channel.Name));
    }


    // Sync channel list after an edit
    private async Task RebuildChannelRepositoryAsync()
    {
        CaptureListAnchorIndex();

        await _channelRepository.Clear();
        await _channelRepository.AddChannelsAsync(ViewModel.Channels);

        ViewModel.EpgViewModel.SetChannels(ViewModel.Channels.ToList());
        ViewModel.UpdateChannelCountText();

        var selectedGroup = ViewModel.SelectedGroup;

        ViewModel.RefreshGroups(selectedGroup, keepFilters: true);
        ViewModel.FilterChannels();
        await RestoreListViewportAsync();
    }

    private int _rebuildAnchorIndex = -1;


    // Anchor the viewport itself, not the playing channel: on repeated deletes
    // the playing channel is already gone from the list and IndexOf returns -1
    private void CaptureListAnchorIndex()
    {
        if (ChannelsListView.ItemsPanelRoot is ItemsStackPanel { FirstVisibleIndex: >= 0 } panel)
        {
            _rebuildAnchorIndex = panel.FirstVisibleIndex;
            return;
        }
        _rebuildAnchorIndex = ViewModel.SelectedChannel is { } anchor
            ? ViewModel.DisplayedChannels.IndexOf(anchor)
            : -1;
    }


    // Keep viewport position when the playing channel is no longer in the list
    private async Task RestoreListViewportAsync()
    {
        if (_rebuildAnchorIndex < 0)
        {
            return;
        }

        var displayed = ViewModel.DisplayedChannels;
        if (ViewModel.SelectedChannel is { } selected && displayed.Contains(selected))
        {
            _rebuildAnchorIndex = -1;
            return;
        }

        var index = Math.Min(_rebuildAnchorIndex, displayed.Count - 1);
        _rebuildAnchorIndex = -1;
        if (index < 0)
        {
            return;
        }

        await Task.Yield();
        await WaitForItemsPanelAsync(ChannelsListView, TimeSpan.FromSeconds(1.5));
        ChannelsListView.ScrollIntoView(displayed[index], ScrollIntoViewAlignment.Leading);
    }


    // Require PIN for locked group actions
    private async Task<bool> EnsurePinApprovedForGroupAsync(string? group)
    {
        var settings = ViewModel.AppSettings;
        if (!ParentalControlService.IsPinRequiredForGroup(settings, group))
        {
            return true;
        }

        return await ShowPinApprovalDialogAsync();
    }


    // Show PIN confirmation dialog
    private async Task<bool> ShowPinApprovalDialogAsync()
    {
        var pinBox = new PasswordBox
        {
            PlaceholderText = L.T("Vvod_Pin_Pole"),
            Width = 280
        };
        var errorText = new TextBlock
        {
            Text = L.T("Nevernyy_PIN"),
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Visibility = Visibility.Collapsed
        };
        var panel = new StackPanel { Spacing = 12, MinWidth = 280 };
        panel.Children.Add(pinBox);
        panel.Children.Add(errorText);

        var dialog = new ThemedContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = L.T("Roditelskiy_Kontrol_Lbl"),
            Content = panel,
            PrimaryButtonText = L.T("OK"),
            CloseButtonText = L.T("Otmena_Lbl")
        };

        while (await DialogQueue.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            if (ParentalControlService.VerifyPin(ViewModel.AppSettings, pinBox.Password))
            {
                return true;
            }

            errorText.Visibility = Visibility.Visible;
            pinBox.Password = string.Empty;
            pinBox.Focus(FocusState.Programmatic);
        }

        return false;
    }
}
