using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.Controls;
using IptvPlayer.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace IptvPlayer;


public sealed partial class MainPage
{

    private string _channelNumberInput = string.Empty;
    private readonly DispatcherTimer _channelNumberInputTimer = new() { Interval = TimeSpan.FromSeconds(3) };


    private bool _hotkeysAttached;


    private void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {

        if (IsFocusedWithin(element => element is ContentDialog))
        {
            return;
        }


        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(CoreVirtualKeyStates.Down))
        {
            switch (e.Key)
            {
                case VirtualKey.F:

                    if (!_isFullScreen)
                    {
                        ChannelSearchBox.Focus(FocusState.Keyboard);
                        SelectAllInSearchBox();
                    }
                    e.Handled = true;
                    return;

                case VirtualKey.J:

                    ToggleStatsOverlay();
                    e.Handled = true;
                    return;

                case VirtualKey.M:

                    ToggleMiniPlayer();
                    e.Handled = true;
                    return;

                case VirtualKey.T:

                    ToggleAlwaysOnTop();
                    e.Handled = true;
                    return;
            }
        }


        if (IsTextInputFocused())
        {
            return;
        }

        if (_channelNumberInput.Length > 0)
        {
            switch (e.Key)
            {
                case VirtualKey.Enter:
                    CommitChannelNumber();
                    e.Handled = true;
                    return;
                case VirtualKey.Back:
                    _channelNumberInput = _channelNumberInput[..^1];
                    if (_channelNumberInput.Length == 0)
                    {
                        CancelChannelNumber();
                    }
                    else
                    {
                        UpdateChannelNumberOverlay();
                    }
                    e.Handled = true;
                    return;
                case VirtualKey.Escape:
                    CancelChannelNumber();
                    e.Handled = true;
                    return;
            }
        }

        var digit = DigitFromKey(e.Key);
        if (digit >= 0)
        {
            HandleChannelNumberDigit(digit);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Back:


                ViewModel.GoToPreviousChannelCommand.Execute(null);
                e.Handled = true;
                break;

            case VirtualKey.Space:

                if ((Player.IsArchivePlaying || Player.IsVodPlaying) && Player.Player != null)
                {
                    ViewModel.ToggleArchivePauseCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case VirtualKey.M:
                Player.ToggleMute();
                ShowActionToast(L.T(Player.IsMuted ? "Bez_Zvuka_M_Lbl" : "Zvuk_Vklyuchen"));
                e.Handled = true;
                break;

            case VirtualKey.V:

                CycleVideoStretch();
                e.Handled = true;
                break;

            case VirtualKey.F or VirtualKey.F11:
                SetFullScreenMode(!_isFullScreen);
                e.Handled = true;
                break;

            case VirtualKey.Escape:

                // Leave fullscreen first; only then close overlays / return to hub
                if (_isFullScreen)
                {
                    SetFullScreenMode(false);
                    e.Handled = true;
                }
                else if (ViewModel.IsEpgVisible)
                {
                    ViewModel.IsEpgVisible = false;
                    ApplyEpgVisibility();
                    e.Handled = true;
                }
                else if (_cameFromHub && Frame.CanGoBack)
                {
                    Frame.GoBack();
                    e.Handled = true;
                }
                break;

            case VirtualKey.PageUp or VirtualKey.Up
                or VirtualKey.PageDown or VirtualKey.Down:

                if (IsNavigationControlFocused())
                {
                    return;
                }
                ZapToAdjacentChannel(
                    e.Key is VirtualKey.PageDown or VirtualKey.Down ? +1 : -1);
                e.Handled = true;
                break;
        }
    }


    private void ZapToAdjacentChannel(int offset)
    {
        var channels = ViewModel.DisplayedChannels;
        if (channels.Count == 0)
        {
            return;
        }

        var index = ViewModel.SelectedChannel is { } current ? channels.IndexOf(current) : -1;
        int next;
        if (index < 0)
        {
            // No selection: jump to first/last instead of wrapping around a bogus index
            next = offset > 0 ? 0 : channels.Count - 1;
        }
        else
        {
            next = (index + offset + channels.Count) % channels.Count;
        }

        var channel = channels[next];
        ViewModel.SelectAndPlayChannelCommand.Execute(channel);
        ChannelsListView.ScrollIntoView(channel);

        if (_isFullScreen)
        {
            OverlayChannelsListView.ScrollIntoView(channel);
            ShowFullScreenOverlay();
            _overlayHideTimer.Stop();
            _overlayHideTimer.Start();
        }
    }

    private void HandleChannelNumberDigit(int digit)
    {
        if (_channelNumberInput.Length >= 4)
        {
            return;
        }

        _channelNumberInput += digit.ToString();
        UpdateChannelNumberOverlay();

        _channelNumberInputTimer.Stop();
        _channelNumberInputTimer.Start();
    }


    private void UpdateChannelNumberOverlay()
    {
        var channels = ViewModel.DisplayedChannels;
        ChannelNumberText.Text = _channelNumberInput;

        ChannelNumberName.Text =
            int.TryParse(_channelNumberInput, out var n) && n >= 1 && n <= channels.Count
                ? channels[n - 1].Name
                : string.Format(L.T("Iz_0"), channels.Count, channels.Count);

        ChannelNumberOverlay.Visibility = Visibility.Visible;
    }

    private void CommitChannelNumber()
    {
        _channelNumberInputTimer.Stop();
        var input = _channelNumberInput;
        _channelNumberInput = string.Empty;
        ChannelNumberOverlay.Visibility = Visibility.Collapsed;

        if (int.TryParse(input, out var n) &&
            n >= 1 && n <= ViewModel.DisplayedChannels.Count)
        {
            var channel = ViewModel.DisplayedChannels[n - 1];
            ViewModel.SelectAndPlayChannelCommand.Execute(channel);
            ChannelsListView.ScrollIntoView(channel);

            if (_isFullScreen)
            {
                OverlayChannelsListView.ScrollIntoView(channel);
            }
        }
    }

    private void CancelChannelNumber()
    {
        _channelNumberInputTimer.Stop();
        _channelNumberInput = string.Empty;
        ChannelNumberOverlay.Visibility = Visibility.Collapsed;
    }


    private static int DigitFromKey(VirtualKey key)
    {
        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
        {
            return (int)key - (int)VirtualKey.Number0;
        }
        if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9)
        {
            return (int)key - (int)VirtualKey.NumberPad0;
        }
        return -1;
    }


    private bool IsTextInputFocused() =>
        IsFocusedWithin(element => element is TextBox or AutoSuggestBox);


    private bool IsNavigationControlFocused() =>
        IsFocusedWithin(element =>
            element is ListView or Slider or ComboBox or TextBox or AutoSuggestBox);

    private bool IsFocusedWithin(Func<DependencyObject, bool> match) =>
        FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused &&
        AnyAncestorOrSelf(focused, match);


    private static bool AnyAncestorOrSelf(DependencyObject element, Func<DependencyObject, bool> match)
    {
        while (element != null)
        {
            if (match(element))
            {
                return true;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }


    private void SelectAllInSearchBox()
    {
        if (FindDescendant<TextBox>(ChannelSearchBox) is { } box)
        {
            box.SelectAll();
        }
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T matched)
            {
                return matched;
            }
            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }

}
