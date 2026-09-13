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
using System.Runtime.InteropServices;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.Controls;
using IptvPlayer.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace IptvPlayer;

/// <summary>
/// Показ/скрытие видео-оверлеев с fade-анимацией (оконный и полноэкранный).
/// Вынесено из MainPage.xaml.cs (MVVM-этап 3: разбиение code-behind по зонам).
/// </summary>
public sealed partial class MainPage
{

    private readonly Storyboard _windowedOverlayFadeStoryboard = new();
    private readonly DoubleAnimation _windowedOverlayFadeAnimation = new() { EnableDependentAnimation = true };

    private void ShowWindowedVideoOverlay()
    {
        if (WindowedVideoOverlay.Visibility == Visibility.Visible && _windowedOverlayFadingIn)
        {
            Serilog.Log.Debug("Overlay.ShowWindowed: уже видим и fading-in — пропуск");
            return;
        }
        _windowedOverlayFadingIn = true;
        Serilog.Log.Debug("Overlay: показ оконного оверлея (fade-in 150мс)");

        _windowedOverlayFadeStoryboard.Stop();
        WindowedVideoOverlay.Visibility = Visibility.Visible;

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
            Serilog.Log.Debug("Overlay.HideWindowed: мгновенное скрытие");
            WindowedVideoOverlay.Opacity = 0;
            WindowedVideoOverlay.Visibility = Visibility.Collapsed;
            WindowedTopOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        Serilog.Log.Debug("Overlay.HideWindowed: fade-out 250мс");

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
        Serilog.Log.Debug("Overlay: оконный оверлей скрыт (fade-out завершён)");
        WindowedVideoOverlay.Visibility = Visibility.Collapsed;
        WindowedTopOverlay.Visibility = Visibility.Collapsed;
    }

    private readonly Storyboard _overlayFadeStoryboard = new();
    private readonly DoubleAnimation _overlayFadeAnimation = new() { EnableDependentAnimation = true };

    /// <summary>
    /// Плавно показывает оверлей поверх видео (fade-in по Opacity). Если оверлей
    /// уже полностью виден, ничего не анимирует — иначе Storyboard.Stop() при
    /// каждом вызове сбрасывал бы Opacity обратно к базовому значению (0 из
    /// XAML) перед новым fade-in, и при частых PointerMoved (быстрое дёрганье
    /// мышью) это выглядело как мигание/скрытие-показ оверлея.
    /// </summary>

    private bool _fullScreenOverlayFadingIn;
    private bool _windowedOverlayFadingIn;

    private bool _suppressOverlayWake;

    private bool _cursorHidden;

    private Services.CursorHider? _cursorHider;

    /// <summary>
    /// Пробуждение из скрытого состояния по движению мыши (вызывается
    /// CursorHider, когда события указателя до XAML не доходят).
    /// </summary>
    private void WakeFromHiddenCursor()
    {
        Serilog.Log.Debug("Cursor: WakeFromHiddenCursor — движение мыши, показ оверлея");
        ShowCursorOverVideo();
        ShowFullScreenOverlay();
        _overlayHideTimer.Stop();
        _overlayHideTimer.Start();
    }

    /// <summary>
    /// Пробуждение по КЛИКУ в скрытом состоянии: курсор возвращается сразу,
    /// но мышь над видео оставляем прозрачной на ~400 мс — оба клика
    /// двойного щелчка должны попасть в XAML-подложку (VideoAreaBorder),
    /// иначе DoubleTapped не срабатывает. Оверлей показываем после окна
    /// двойного клика, если fullscreen не выключили.
    /// </summary>
    private void WakeFromHiddenCursorByClick()
    {
        Serilog.Log.Debug("Cursor: WakeFromHiddenCursorByClick — возврат курсора (мышь прозрачна 400мс)");
        _cursorHidden = false;
        _cursorHider?.Show(restoreMouse: false);
        RootGrid.ShowCursorOverWindow();
        _ = DelayedOverlayShowAfterClickAsync();
    }

    /// <summary>
    /// Колесо мыши в спрятанном состоянии: до XAML событие не доходит
    /// (мост прозрачен) — CursorHider ловит его низкоуровневым хуком.
    /// Логика та же, что у колеса над видео: 5% на метку колеса.
    /// </summary>
    private void OnWheelWhileCursorHidden(int wheelDelta)
    {
        var steps = wheelDelta / 120;
        if (steps == 0)
        {
            return;
        }
        var current = Player.IsMuted ? 0.0 : Player.LastUserVolume ?? Player.Player?.Volume ?? 1.0;
        var target = Math.Clamp(current + steps * 0.05, 0.0, 1.0);
        if (Math.Abs(target - current) < 0.001)
        {
            return;
        }
        Serilog.Log.Debug("Cursor: колесо при скрытом курсоре: VOLUME {Cur:F2} -> {Tgt:F2}", current, target);
        OnVolumeSliderChanged(target);
    }

    /// <summary>
    /// Двойной клик по видео в спрятанном состоянии: XAML-ская пара
    /// DoubleTapped не собирается (первый клик теряется в прозрачном мосте),
    /// поэтому CursorHider распознаёт её сам — переключаем fullscreen так же,
    /// как VideoArea_DoubleTapped.
    /// </summary>
    private void WakeFromHiddenCursorByDoubleClick()
    {
        Serilog.Log.Debug("Cursor: WakeFromHiddenCursorByDoubleClick — переключение fullscreen");
        _cursorHidden = false;
        RootGrid.ShowCursorOverWindow();
        _cursorHider?.RestoreMouse();
        SetFullScreenMode(!_isFullScreen);
    }

    private async Task DelayedOverlayShowAfterClickAsync()
    {
        Serilog.Log.Debug("Cursor: DelayedOverlayShowAfterClickAsync — ожидание 400мс");
        await Task.Delay(400);

        if (!_isFullScreen)
        {
            Serilog.Log.Debug("Cursor: DelayedOverlayShowAfterClick — fullscreen выключен, RestoreMouse");
            _cursorHider?.RestoreMouse();
            return;
        }

        if (FullScreenOverlay.Visibility == Visibility.Visible && _fullScreenOverlayFadingIn)
        {
            Serilog.Log.Debug("Cursor: DelayedOverlayShowAfterClick — оверлей уже видим, RestoreMouse");
            _cursorHider?.RestoreMouse();
            return;
        }
        Serilog.Log.Debug("Cursor: DelayedOverlayShowAfterClick — показ оверлея после клика");
        _cursorHider?.RestoreMouse();
        ShowFullScreenOverlay();
        _overlayHideTimer.Stop();
        _overlayHideTimer.Start();
    }

    /// <summary>Показывает системный курсор.</summary>
    private void ShowCursorOverVideo()
    {
        Serilog.Log.Debug("Cursor.ShowCursorOverVideo: ProtectedCursor=null на CursorGrid (показ)");
        _cursorHidden = false;
        _cursorHider?.Show();
        RootGrid.ShowCursorOverWindow();
        SetProtectedCursor(MediaPlayer, null);
    }

    /// <summary>Прячет курсор над окном (вызывается при автоскрытии оверлея).</summary>
    private void HideCursorOverVideo()
    {
        Serilog.Log.Debug("Cursor.HideCursorOverVideo: ProtectedCursor на CursorGrid (один элемент), CursorHider для моста");
        RootGrid.HideCursorOverWindow();
        _cursorHidden = true;

        if (_cursorHider == null && MainWindow.Instance != null)
        {
            _cursorHider = new Services.CursorHider(
                WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance),
                WakeFromHiddenCursor,
                WakeFromHiddenCursorByClick,
                WakeFromHiddenCursorByDoubleClick,
                OnWheelWhileCursorHidden);
            Serilog.Log.Information("Cursor: CursorHider создан");
        }
        _cursorHider?.Hide();

        _ = NudgePointerDelayedAsync();

        Serilog.Log.Debug("Cursor: ProtectedCursor=null на ключевых элементах + отложенный нудж");
    }


    /// <summary>
    /// Установка protected-свойства UIElement.ProtectedCursor на произвольный
    /// элемент (наследника сделать нельзя) — приём из блога Simon Mourier.
    /// </summary>
    private static void SetProtectedCursor(Microsoft.UI.Xaml.UIElement element,
        Microsoft.UI.Input.InputCursor? cursor)
    {
        try
        {
            typeof(Microsoft.UI.Xaml.UIElement).InvokeMember("ProtectedCursor",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                null, element, new[] { (object?)cursor });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Cursor: не удалось установить ProtectedCursor на {Type}.", element.GetType().Name);
        }
    }

    private async Task NudgePointerDelayedAsync()
    {
        Serilog.Log.Debug("Cursor: нудж старт (ожидание 120мс для применения ProtectedCursor)");
        _suppressOverlayWake = true;
        try
        {
            await Task.Delay(120);
            NudgePointer(2);
            Serilog.Log.Debug("Cursor: нудж вправо +2px");
            await Task.Delay(30);
            NudgePointer(-2);
            Serilog.Log.Debug("Cursor: нудж влево -2px (позиция не смещена)");
            await Task.Delay(30);
        }
        finally
        {
            _suppressOverlayWake = false;
            Serilog.Log.Debug("Cursor: нудж завершён, _suppressOverlayWake=false");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, INPUT[] inputs, int size);

    /// <summary>Сдвиг мыши на dx пикселей — принудительное событие указателя.</summary>
    private static void NudgePointer(int dx)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { dx = dx, dy = 0, dwFlags = MOUSEEVENTF_MOVE }
        };
        var sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        Serilog.Log.Debug("Cursor: нудж dx={Dx}, SendInput={Sent}", dx, sent);
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _actionToastHideTimer;

    /// <summary>
    /// Короткое подтверждение действия внизу по центру видео (громкость,
    /// mute, таймер сна): текст + авто-скрытие через 2,2 с, fade по Opacity.
    /// Показывается в обеих поверхностях — оконной и полноэкранной.
    /// </summary>
    private void ShowActionToast(string message)
    {
        ActionToastText.Text = message;
        OverlayActionToastText.Text = message;

        var storyboard = new Storyboard();
        foreach (var target in new[] { ActionToast, OverlayActionToast })
        {
            var animation = new DoubleAnimation
            {
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(150)),
                EasingFunction = new QuadraticEase()
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, "Opacity");
            storyboard.Children.Add(animation);
        }
        storyboard.Begin();

        if (_actionToastHideTimer is not { } timer)
        {
            timer = DispatcherQueue.CreateTimer();
            _actionToastHideTimer = timer;
        }
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(2200);
        timer.Tick -= ActionToastHideTimer_Tick;
        timer.Tick += ActionToastHideTimer_Tick;
        timer.Start();
    }

    private void ActionToastHideTimer_Tick(object? sender, object e)
    {
        _actionToastHideTimer?.Stop();

        var storyboard = new Storyboard();
        foreach (var target in new[] { ActionToast, OverlayActionToast })
        {
            var animation = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                EasingFunction = new QuadraticEase()
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, "Opacity");
            storyboard.Children.Add(animation);
        }
        storyboard.Begin();
    }

    private void ShowFullScreenOverlay()
    {

        if (ViewModel.IsEpgVisible)
        {
            Serilog.Log.Debug("Overlay.ShowFullScreen: EPG открыт — пропуск");
            return;
        }

        if (FullScreenOverlay.Visibility == Visibility.Visible && _fullScreenOverlayFadingIn)
        {
            Serilog.Log.Debug("Overlay.ShowFullScreen: уже видим и fading-in — пропуск");
            return;
        }
        _fullScreenOverlayFadingIn = true;

        Serilog.Log.Debug("Overlay: показ полноэкранного оверлея (fade-in 150мс, курсор вернуть)");
        ShowCursorOverVideo();

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

        if (_isFullScreen)
        {
            Serilog.Log.Debug("Overlay.HideFullScreen: fullscreen — HideCursorOverVideo (immediate={Immediate})", immediate);
            HideCursorOverVideo();
        }
        else
        {
            Serilog.Log.Debug("Overlay.HideFullScreen: не fullscreen — ShowCursorOverVideo");
            ShowCursorOverVideo();
        }

        if (immediate)
        {
            FullScreenOverlay.Opacity = 0;
            FullScreenOverlay.Visibility = Visibility.Collapsed;
            Serilog.Log.Debug("Overlay.HideFullScreen: мгновенное скрытие (без fade-out)");
            return;
        }

        Serilog.Log.Debug("Overlay.HideFullScreen: fade-out 250мс");
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
        Serilog.Log.Debug("Overlay: полноэкранный оверлей скрыт (fade-out завершён, isFullScreen={FS})", _isFullScreen);
        if (_isFullScreen)
        {
            FullScreenOverlay.Visibility = Visibility.Collapsed;
        }
    }

}
