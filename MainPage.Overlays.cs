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

    // Курсор для режима «не беспокоить» поверх видео: в fullscreen, пока
    // показаны оверлеи, — обычная стрелка; когда оверлей автоскрылся, курсор
    // убирается совсем, как в видеоплеерах. См. Controls/CursorGrid —
    // ProtectedCursor (единственный вход в input-site WinUI: ShowCursor
    // потокозависим и из UI-потока не действует, WM_SETCURSOR сайту не
    // приходит) в этом SDK protected, поэтому корневой Grid страницы —
    // наследник с публичными методами.
    // Невидимые курсоры по элементам: input-site периодически переоценивает
    // курсор по элементу под указателем, и любой элемент БЕЗ ProtectedCursor
    // в этот момент возвращает стрелку (мелькание). Ставим курсор на всё
    // реализованное визуальное дерево; экземпляр на каждый элемент свой —
    // один и тот же InputCursor на нескольких элементах не работает.
    private readonly Dictionary<Microsoft.UI.Xaml.UIElement, Microsoft.UI.Input.InputCursor> _hiddenCursorByElement = new();

    // True во время «нуджа» (синтетический сдвиг мыши 2 px для применения
    // ProtectedCursor): такие события не должны будить оверлей.
    private bool _suppressOverlayWake;

    // True, пока курсор спрятан в fullscreen: в это время замораживается
    // секундное обновление текста StatsOverlay (см. _archivePositionTimer) —
    // под обновляющийся текст input-site переоценивал курсор и возвращал
    // стрелку (мелькание).
    private bool _cursorHidden;

    // Прозрачность мыши над видео-окном (DesktopChildSiteBridge) + опрос
    // GetCursorPos для пробуждения.
    private Services.CursorHider? _cursorHider;

    /// <summary>
    /// Пробуждение из скрытого состояния по движению мыши (вызывается
    /// CursorHider, когда события указателя до XAML не доходят).
    /// </summary>
    private void WakeFromHiddenCursor()
    {
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
        _cursorHidden = false;
        _cursorHider?.Show(restoreMouse: false); // курсор да, мышь — нет
        foreach (var element in _hiddenCursorByElement.Keys.ToList())
        {
            SetProtectedCursor(element, null);
        }
        _hiddenCursorByElement.Clear();
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
        _cursorHidden = false;
        foreach (var element in _hiddenCursorByElement.Keys.ToList())
        {
            SetProtectedCursor(element, null);
        }
        _hiddenCursorByElement.Clear();
        RootGrid.ShowCursorOverWindow();
        _cursorHider?.RestoreMouse();
        SetFullScreenMode(!_isFullScreen);
    }

    private async Task DelayedOverlayShowAfterClickAsync()
    {
        await Task.Delay(400);
        // Двойной клик успел выключить fullscreen — ничего не показываем.
        if (!_isFullScreen)
        {
            _cursorHider?.RestoreMouse();
            return;
        }
        // Пользователь начал двигать мышь — оверлей уже показан обычным путём.
        if (FullScreenOverlay.Visibility == Visibility.Visible && _fullScreenOverlayFadingIn)
        {
            _cursorHider?.RestoreMouse();
            return;
        }
        _cursorHider?.RestoreMouse();
        ShowFullScreenOverlay();
        _overlayHideTimer.Stop();
        _overlayHideTimer.Start();
    }

    /// <summary>Показывает системный курсор.</summary>
    private void ShowCursorOverVideo()
    {
        _cursorHidden = false;
        _cursorHider?.Show(); // вернуть мышь над видео (снять прозрачность моста)
        Serilog.Log.Debug("Cursor: показ");
        foreach (var element in _hiddenCursorByElement.Keys.ToList())
        {
            SetProtectedCursor(element, null);
        }
        _hiddenCursorByElement.Clear();
        RootGrid.ShowCursorOverWindow();
        SetProtectedCursor(MediaPlayer, null);
    }

    /// <summary>Прячет курсор над окном (вызывается при автоскрытии оверлея).</summary>
    private void HideCursorOverVideo()
    {
        // Полное покрытие визуального дерева (экземпляр курсора на каждый
        // элемент): скрытие работает при любом положении указателя. Раньше
        // это ломало компоновку видео при переходах fullscreen — теперь
        // после смены режима плеер пересобирает компоновку принудительно
        // (см. ForceVideoRelayout в SetFullScreenMode).
        RootGrid.HideCursorOverWindow();
        _cursorHidden = true;

        // Над видео курсор прячет Services/CursorHider (окно видеомоста
        // делается прозрачным для мыши — срабатывает ProtectedCursor
        // подложки); пробуждение по движению он отслеживает сам опросом
        // GetCursorPos и зовёт этот колбэк.
        if (_cursorHider == null && MainWindow.Instance != null)
        {
            _cursorHider = new Services.CursorHider(
                WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance),
                WakeFromHiddenCursor,
                WakeFromHiddenCursorByClick,
                WakeFromHiddenCursorByDoubleClick,
                OnWheelWhileCursorHidden);
        }
        _cursorHider?.Hide();
        foreach (var element in EnumerateVisualTree(RootGrid))
        {
            if (!_hiddenCursorByElement.TryGetValue(element, out var cursor) || cursor == null)
            {
                cursor = RootGrid.CreateHiddenCursor();
                if (cursor == null)
                {
                    break;
                }
                _hiddenCursorByElement[element] = cursor;
            }
            SetProtectedCursor(element, cursor);
        }

        // Input-site применяет ProtectedCursor только при СЛЕДУЮЩЕМ событии
        // указателя (отсюда «прячется после клика»): пока мышь неподвижна,
        // курсор остаётся стрелкой. Установка свойства доходит до site
        // асинхронно, поэтому нудж делается отложенно (~120 мс) и дважды
        // (1 px вправо, затем 1 px назад — позиция не смещается); порог
        // «синтетических» PointerMoved (<= 1 px) не даёт ему разбудить оверлей.
        _ = NudgePointerDelayedAsync();

        Serilog.Log.Debug("Cursor: ProtectedCursor=None на ключевых элементах + отложенный нудж");
    }

    /// <summary>
    /// Поддерево области видео для установки невидимого курсора. ВАЖНО: в
    /// потомки MediaPlayerElement не спускаемся — установка ProtectedCursor
    /// на внутренние визуал-элементы плеера ломает компоновку видео-острова
    /// (видео со смещением при выходе/повторном входе в fullscreen). Сам
    /// MediaPlayer и все соседние элементы покрываются.
    /// </summary>
    private IEnumerable<Microsoft.UI.Xaml.UIElement> VideoAreaDescendants()
    {
        yield return VideoAreaBorder;
        var children = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(VideoAreaBorder);
        for (var i = 0; i < children; i++)
        {
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(VideoAreaBorder, i) is not Microsoft.UI.Xaml.UIElement child)
            {
                continue;
            }
            if (ReferenceEquals(child, MediaPlayer))
            {
                yield return child; // плеер целиком, без спуска внутрь
                continue;
            }
            foreach (var element in EnumerateVisualTree(child))
            {
                yield return element;
            }
        }

        foreach (var element in EnumerateVisualTree(StatsOverlay))
        {
            yield return element;
        }
        foreach (var element in EnumerateVisualTree(FullScreenOverlay))
        {
            yield return element;
        }
    }

    private static IEnumerable<Microsoft.UI.Xaml.UIElement> EnumerateVisualTree(Microsoft.UI.Xaml.UIElement root)
    {
        yield return root;
        var children = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < children; i++)
        {
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i) is Microsoft.UI.Xaml.UIElement child)
            {
                foreach (var descendant in EnumerateVisualTree(child))
                {
                    yield return descendant;
                }
            }
        }
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
        Serilog.Log.Debug("Cursor: нудж старт");
        _suppressOverlayWake = true;
        try
        {
            await Task.Delay(120);
            NudgePointer(2);
            await Task.Delay(30);
            NudgePointer(-2);
            await Task.Delay(30);
        }
        finally
        {
            _suppressOverlayWake = false;
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

    private void ShowFullScreenOverlay()
    {
        if (FullScreenOverlay.Visibility == Visibility.Visible && _fullScreenOverlayFadingIn)
        {
            return;
        }
        _fullScreenOverlayFadingIn = true;

        Serilog.Log.Debug("Overlay: показ полноэкранного оверлея (курсор вернуть)");
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

        // В fullscreen вместе с оверлеем прячем и курсор; при выходе из
        // fullscreen (immediate, _isFullScreen уже false) курсор обязателен.
        if (_isFullScreen)
        {
            Serilog.Log.Debug("Overlay: скрытие полноэкранного оверлея (прячем курсор)");
            HideCursorOverVideo();
        }
        else
        {
            ShowCursorOverVideo();
        }

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
