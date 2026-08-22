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
// Windows.Media.Playback.MediaPlayer конфликтует по имени с x:Name="MediaPlayer"
// (MediaPlayerElement) в разметке, поэтому в коде тип всегда указывается
// с полным неймспейсом: Windows.Media.Playback.MediaPlayer.

namespace IptvPlayer;

/// <summary>
/// Показ/скрытие видео-оверлеев с fade-анимацией (оконный и полноэкранный).
/// Вынесено из MainPage.xaml.cs (MVVM-этап 3: разбиение code-behind по зонам).
/// </summary>
public sealed partial class MainPage
{
    // Отдельный Storyboard для оконного оверлея (у полноэкранного свой) —
    // иначе переустановка цели анимации конфликтовала бы при быстрых
    // переключениях показ/скрытие обоих оверлеев.
    private readonly Storyboard _windowedOverlayFadeStoryboard = new();
    private readonly DoubleAnimation _windowedOverlayFadeAnimation = new() { EnableDependentAnimation = true };

    private void ShowWindowedVideoOverlay()
    {
        if (WindowedVideoOverlay.Visibility == Visibility.Visible && _windowedOverlayFadingIn)
        {
            return;
        }
        _windowedOverlayFadingIn = true;

        _windowedOverlayFadeStoryboard.Stop();
        WindowedVideoOverlay.Visibility = Visibility.Visible;

        // Верхняя шапка с названием канала — появляется мгновенно (без
        // анимации), вместе с нижней панелью управления, и прячется
        // вместе с ней по таймеру автоскрытия.
        WindowedTopOverlay.Visibility = Visibility.Visible;
        WindowedTopOverlay.Opacity = 1;

        _windowedOverlayFadeAnimation.To = 1;
        _windowedOverlayFadeAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(150));
        Storyboard.SetTarget(_windowedOverlayFadeAnimation, WindowedVideoOverlay);
        Storyboard.SetTargetProperty(_windowedOverlayFadeAnimation, "Opacity");

        _windowedOverlayFadeStoryboard.Children.Clear();
        _windowedOverlayFadeStoryboard.Children.Add(_windowedOverlayFadeAnimation);
        _windowedOverlayFadeStoryboard.Completed -= WindowedOverlayFadeOut_Completed;
        _windowedOverlayFadeStoryboard.Begin();
    }

    private void HideWindowedVideoOverlay(bool immediate = false)
    {
        _windowedOverlayFadingIn = false;
        _windowedOverlayFadeStoryboard.Stop();

        if (immediate)
        {
            WindowedVideoOverlay.Opacity = 0;
            WindowedVideoOverlay.Visibility = Visibility.Collapsed;
            WindowedTopOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        _windowedOverlayFadeAnimation.To = 0;
        _windowedOverlayFadeAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(250));
        Storyboard.SetTarget(_windowedOverlayFadeAnimation, WindowedVideoOverlay);
        Storyboard.SetTargetProperty(_windowedOverlayFadeAnimation, "Opacity");

        _windowedOverlayFadeStoryboard.Children.Clear();
        _windowedOverlayFadeStoryboard.Children.Add(_windowedOverlayFadeAnimation);
        _windowedOverlayFadeStoryboard.Completed -= WindowedOverlayFadeOut_Completed;
        _windowedOverlayFadeStoryboard.Completed += WindowedOverlayFadeOut_Completed;
        _windowedOverlayFadeStoryboard.Begin();
    }

    private void WindowedOverlayFadeOut_Completed(object? sender, object e)
    {
        WindowedVideoOverlay.Visibility = Visibility.Collapsed;
        WindowedTopOverlay.Visibility = Visibility.Collapsed;
    }

    // Один переиспользуемый Storyboard для показа/скрытия оверлея: перед каждым
    // запуском он останавливается (Stop не поднимает Completed), поэтому "старая"
    // анимация скрытия не может внезапно схлопнуть оверлей уже после того, как
    // его снова показали движением мыши.
    private readonly Storyboard _overlayFadeStoryboard = new();
    private readonly DoubleAnimation _overlayFadeAnimation = new() { EnableDependentAnimation = true };

    /// <summary>
    /// Плавно показывает оверлей поверх видео (fade-in по Opacity). Если оверлей
    /// уже полностью виден, ничего не анимирует — иначе Storyboard.Stop() при
    /// каждом вызове сбрасывал бы Opacity обратно к базовому значению (0 из
    /// XAML) перед новым fade-in, и при частых PointerMoved (быстрое дёрганье
    /// мышью) это выглядело как мигание/скрытие-показ оверлея.
    /// </summary>
    // Направление последнего fade для каждого оверлея. Раньше показ проверял
    // Opacity >= 1, и во время fade-in (150 мс) каждое движение мыши вызывало
    // новый Show: Storyboard.Stop() сбрасывал Opacity к базовому 0, анимация
    // начиналась заново и не успевала завершиться, пока мышь движется —
    // оверлей "появлялся" только после полной остановки мыши. Теперь если
    // fade-in уже идёт (или завершился) — Show просто ничего не делает.
    private bool _fullScreenOverlayFadingIn;
    private bool _windowedOverlayFadingIn;

    private void ShowFullScreenOverlay()
    {
        if (FullScreenOverlay.Visibility == Visibility.Visible && _fullScreenOverlayFadingIn)
        {
            return;
        }
        _fullScreenOverlayFadingIn = true;

        _overlayFadeStoryboard.Stop();

        FullScreenOverlay.Visibility = Visibility.Visible;

        _overlayFadeAnimation.To = 1;
        _overlayFadeAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(150));
        Storyboard.SetTarget(_overlayFadeAnimation, FullScreenOverlay);
        Storyboard.SetTargetProperty(_overlayFadeAnimation, "Opacity");

        _overlayFadeStoryboard.Children.Clear();
        _overlayFadeStoryboard.Children.Add(_overlayFadeAnimation);
        _overlayFadeStoryboard.Completed -= OverlayFadeOut_Completed;
        _overlayFadeStoryboard.Begin();
    }

    /// <summary>
    /// Плавно скрывает оверлей (fade-out по Opacity), затем сворачивает его,
    /// чтобы он не перехватывал события указателя поверх видео.
    /// При immediate = true скрывает мгновенно, без анимации (используется
    /// при выходе из fullscreen).
    /// </summary>
    private void HideFullScreenOverlay(bool immediate = false)
    {
        _fullScreenOverlayFadingIn = false;
        _overlayFadeStoryboard.Stop();

        if (immediate)
        {
            FullScreenOverlay.Opacity = 0;
            FullScreenOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        _overlayFadeAnimation.To = 0;
        _overlayFadeAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(250));
        Storyboard.SetTarget(_overlayFadeAnimation, FullScreenOverlay);
        Storyboard.SetTargetProperty(_overlayFadeAnimation, "Opacity");

        _overlayFadeStoryboard.Children.Clear();
        _overlayFadeStoryboard.Children.Add(_overlayFadeAnimation);
        _overlayFadeStoryboard.Completed -= OverlayFadeOut_Completed;
        _overlayFadeStoryboard.Completed += OverlayFadeOut_Completed;
        _overlayFadeStoryboard.Begin();
    }

    private void OverlayFadeOut_Completed(object? sender, object e)
    {
        if (_isFullScreen)
        {
            FullScreenOverlay.Visibility = Visibility.Collapsed;
        }
    }

}
