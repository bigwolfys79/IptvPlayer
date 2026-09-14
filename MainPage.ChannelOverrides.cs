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

/// <summary>
/// Пользовательские правки каналов поверх плейлиста: перенос в другую группу
/// и удаление из списка. Правки живут в SQLite (channel_overrides) и
/// применяются после каждой загрузки/обновления плейлиста — ChannelsOverride
/// переживает перекачку m3u. Действия над каналами заблокированных групп
/// требуют PIN (если установлен).
/// </summary>
public sealed partial class MainPage : Page
{
    /// <summary>
    /// Применяет сохранённые правки к свежезагруженному списку каналов:
    /// удаляет скрытые каналы, переносит перемещённые. Вызывается до
    /// наполнения репозитория и ViewModel.Channels.
    /// </summary>
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

    /// <summary>
    /// Тексты пунктов контекстного меню канала задаются при открытии:
    /// MenuFlyoutItem не берёт текст из «плоского» ключа ресурса, а ключ с
    /// суффиксом .Text конфликтовал бы с ним же (нужен для L.T в коде).
    /// </summary>
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
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (_activePlaylist == null || string.IsNullOrEmpty(channel.StreamUrl))
        {
            return;
        }

        if (ReferenceEquals(ViewModel.SelectedChannel, channel))
        {
            try
            {
                ViewModel.Player.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Остановка плеера перед удалением канала «{Channel}».", channel.Name);
            }
        }

        ViewModel.Channels.Remove(channel);
        ViewModel.SelectedChannel = null;

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

    /// <summary>
    /// Синхронизирует список каналов после правки: репозиторий EPG,
    /// EpgViewModel, счётчик, группы и фильтр.
    /// </summary>
    private async Task RebuildChannelRepositoryAsync()
    {
        await _channelRepository.Clear();
        foreach (var channel in ViewModel.Channels)
        {
            await _channelRepository.AddChannelAsync(channel);
        }

        ViewModel.EpgViewModel.SetChannels(ViewModel.Channels.ToList());
        ViewModel.UpdateChannelCountText();
        ViewModel.RefreshGroups();
        ViewModel.FilterChannels();
    }

    /// <summary>
    /// PIN-гейт для действий над каналом заблокированной группы: при
    /// включённом родительском контроле и установленном PIN запрашивает
    /// разовое подтверждение. true — действие разрешено.
    /// </summary>
    private async Task<bool> EnsurePinApprovedForGroupAsync(string? group)
    {
        var settings = ViewModel.AppSettings;
        if (!ParentalControlService.IsPinRequiredForGroup(settings, group))
        {
            return true;
        }

        return await ShowPinApprovalDialogAsync();
    }

    /// <summary>Диалог разового подтверждения PIN (без разблокировки групп).</summary>
    private async Task<bool> ShowPinApprovalDialogAsync()
    {
        var pinBox = new PasswordBox
        {
            PlaceholderText = L.T("Vvod_Pin_Pole"),
            Width = 280
        };
        var errorText = new TextBlock
        {
            Text = L.T("Nevernyy_Pin"),
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

        while (await dialog.ShowAsync() == ContentDialogResult.Primary)
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
