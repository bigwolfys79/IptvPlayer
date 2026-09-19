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


    private void SetFullScreenMode(bool enable)
    {
        _isFullScreen = enable;
        Serilog.Log.Information("FullScreen: {Action}", enable ? "вход в полноэкранный режим" : "выход из полноэкранного режима");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var swTotal = System.Diagnostics.Stopwatch.StartNew();

        MainWindow.Instance?.SetOsFullScreen(enable);
        Serilog.Log.Information("FullScreen-DIAG: presenter {Ms:F0} мс", sw.Elapsed.TotalMilliseconds);

        ForceVideoRelayout();

        sw.Restart();
        if (enable)
        {

            _wasEpgVisibleBeforeFullScreen = ViewModel.IsEpgVisible;
            ViewModel.IsEpgVisible = false;
            ApplyEpgVisibility();

            if (ChannelListColumn.ActualWidth > 0)
            {
                _channelListExpandedWidth = ChannelListColumn.ActualWidth;
                LastChannelListWidth = _channelListExpandedWidth;
            }
            ChannelListColumn.MinWidth = 0;
            ChannelListColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);

            OverlayChannelsPanel.Visibility = _localVideoFile == null
                ? Visibility.Visible
                : Visibility.Collapsed;
            OverlayChannelsColumn.Width = _localVideoFile == null
                ? new GridLength(320)
                : new GridLength(0);

            HideWindowedVideoOverlay(immediate: true);
            WindowedTopOverlay.Visibility = Visibility.Collapsed;

            VideoAreaBorder.Padding = new Thickness(0);

            StatsOverlay.Margin = new Thickness(344, 100, 0, 0);


            SyncVolumeSliders(Player.Player?.Volume ?? Player.LastUserVolume ?? 1.0);

            Serilog.Log.Information("FullScreen-DIAG: колонки/оверлеи {Ms:F0} мс", sw.Elapsed.TotalMilliseconds);

            _lastOverlayPointerPosition = new Windows.Foundation.Point(-1, -1);
            ShowFullScreenOverlay();

            _ = NudgePointerDelayedAsync();

            _ = ScrollOverlayChannelIntoViewAsync();

            _overlayHideTimer.Stop();
            _overlayHideTimer.Start();
        }
        else
        {


            if (_localVideoFile == null)
            {
                ChannelListColumn.MinWidth = 280;
                ChannelListColumn.Width = new GridLength(_channelListExpandedWidth);
                SplitterColumn.Width = GridLength.Auto;
            }
            ViewModel.IsEpgVisible = _wasEpgVisibleBeforeFullScreen;
            ApplyEpgVisibility();


            VideoAreaBorder.Padding = new Thickness(12);

            StatsOverlay.Margin = new Thickness(12, 60, 0, 0);

            _overlayHideTimer.Stop();
            HideFullScreenOverlay(immediate: true);
            _lastWindowedOverlayPointerPosition = new Windows.Foundation.Point(-1, -1);
        }

        Serilog.Log.Information("FullScreen-DIAG: итого {Ms:F0} мс", swTotal.Elapsed.TotalMilliseconds);
    }


    private void ForceVideoRelayout()
    {
        MediaPlayer.Visibility = Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(() => MediaPlayer.Visibility = Visibility.Visible);
    }


    private Windows.Foundation.Point _lastOverlayPointerPosition = new(-1, -1);

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isFullScreen || _suppressOverlayWake)
        {
            return;
        }
        var position = e.GetCurrentPoint(RootGrid).Position;

        if (Math.Abs(position.X - _lastOverlayPointerPosition.X) <= 1 &&
            Math.Abs(position.Y - _lastOverlayPointerPosition.Y) <= 1)
        {
            return;
        }
        _lastOverlayPointerPosition = position;

        if (EpgPanelBorder.Visibility == Visibility.Visible)
        {
            if (FullScreenOverlay.Visibility == Visibility.Visible)
            {
                HideFullScreenOverlay(immediate: true);
                _overlayHideTimer.Stop();
            }
            return;
        }

        ShowFullScreenOverlay();
        _overlayHideTimer.Stop();
        _overlayHideTimer.Start();
    }


    private void VideoArea_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isFullScreen)
        {
            return;
        }

        var position = e.GetCurrentPoint(VideoAreaBorder).Position;
        if (Math.Abs(position.X - _lastWindowedOverlayPointerPosition.X) < 1 &&
            Math.Abs(position.Y - _lastWindowedOverlayPointerPosition.Y) < 1)
        {
            return;
        }
        _lastWindowedOverlayPointerPosition = position;

        Serilog.Log.Debug("VideoArea move: EpgVisible={Epg} isFullScreen={FS}",
            ViewModel.IsEpgVisible, _isFullScreen);

        ShowWindowedVideoOverlay();
        _overlayHideTimer.Stop();
        _overlayHideTimer.Start();
    }


    private void RootGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {


        if (EpgPanelBorder.Visibility == Visibility.Visible)
        {
            Serilog.Log.Debug("Wheel: EPG visible — BLOCKED");
            return;
        }

        var position = e.GetCurrentPoint(VideoAreaBorder).Position;
        if (position.X < 0 || position.Y < 0 ||
            position.X > VideoAreaBorder.ActualWidth ||
            position.Y > VideoAreaBorder.ActualHeight)
        {
            Serilog.Log.Debug("Wheel: outside VideoAreaBorder ({X:F0},{Y:F0}) vs ({W:F0}x{H:F0}) — ignored",
                position.X, position.Y, VideoAreaBorder.ActualWidth, VideoAreaBorder.ActualHeight);
            return;
        }

        var wheel = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
        if (wheel == 0)
        {
            return;
        }

        var current = Player.IsMuted ? 0.0 : Player.LastUserVolume ?? Player.Player?.Volume ?? 1.0;
        var target = Math.Clamp(current + (wheel > 0 ? 0.05 : -0.05), 0.0, 1.0);
        if (Math.Abs(target - current) < 0.001)
        {
            return;
        }

        Serilog.Log.Debug("Wheel: VOLUME {Cur:F2} -> {Tgt:F2}", current, target);
        OnVolumeSliderChanged(target);

        if (_isFullScreen)
        {
            ShowFullScreenOverlay();
        }
        else
        {
            ShowWindowedVideoOverlay();
        }
        _overlayHideTimer.Stop();
        _overlayHideTimer.Start();

        e.Handled = true;
    }

}
