using System;
using System.Runtime.InteropServices;
using System.Text;

namespace IptvPlayer.Services;

/// <summary>
/// Скрытие курсора НАД ВИДЕО в fullscreen. Курсором окна видеомоста
/// (Microsoft.UI.Content.DesktopChildSiteBridge) из нашего потока управлять
/// не получается никак (SetCursor/WM_SETCURSOR/класс окна — сайт рисует сам),
/// поэтому обход: пока курсор должен быть спрятан, окно моста делается
/// прозрачным для МЫШИ (WS_EX_TRANSPARENT) — hit-test проваливается к
/// XAML-подложке под видео, где работает ProtectedCursor. Видео продолжает
/// рисоваться, меняется только маршрутизация указателя.
/// События движения до XAML при этом могут не доходить, поэтому пробуждение
/// (движение мыши) отслеживаем опросом GetCursorPos каждые 16 мс.
/// </summary>
public sealed class CursorHider : IDisposable
{
    private const string VideoBridgeClass = "Microsoft.UI.Content.DesktopChildSiteBridge";
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;

    private readonly IntPtr _mainHwnd;
    private readonly Action _wake;
    private readonly Action _wakeByClick;
    private readonly Action _wakeByDoubleClick;
    private readonly Action<int> _wheel;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcher;
    private readonly LowLevelMouseProc? _mouseHookProc;
    private readonly List<IntPtr> _bridges = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private POINT _lastPointer;
    private DateTime _lastClickUtc = DateTime.MinValue;
    private DateTime _clickWatchUntil = DateTime.MinValue;
    private bool _buttonWasDown;

    public CursorHider(IntPtr mainHwnd, Action wake, Action wakeByClick, Action wakeByDoubleClick, Action<int> wheel)
    {
        _mainHwnd = mainHwnd;
        _wake = wake;
        _wakeByClick = wakeByClick;
        _wakeByDoubleClick = wakeByDoubleClick;
        _wheel = wheel;
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _dispatcher = dispatcher;
        // Хук колеса: пока мост прозрачен, WM_MOUSEWHEEL до XAML не доходит —
        // ловим низкоуровневым хуком (ставится только на время скрытия).
        _mouseHookProc = LowLevelMouseHook;
        if (dispatcher != null)
        {
            _timer = dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.Tick += (s, e) =>
            {
                var now = DateTime.UtcNow;

                // После первого клика следим за вторым (окно двойного щелчка)
                // даже после выхода из скрытого состояния: клик «сквозь»
                // прозрачный мост до XAML не доходит, DoubleTapped XAML не
                // соберётся — распознаём пару сами.
                if (!_hidden && now >= _clickWatchUntil)
                {
                    _timer?.Stop(); // наблюдение закончено — глушим опрос
                    return;
                }

                bool buttonDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
                bool newPress = buttonDown && !_buttonWasDown;
                _buttonWasDown = buttonDown;

                if (_hidden)
                {
                    // Указатель фактически над XAML-окнами нашего потока (мост
                    // прозрачен), поэтому SetCursor(NULL) действует и перекрывает
                    // «хвостовые» курсоры элементов (слайдеры, сплиттеры).
                    SetCursor(IntPtr.Zero);

                    if (newPress)
                    {
                        if ((now - _lastClickUtc).TotalMilliseconds <= 500)
                        {
                            // Второй быстрый клик — двойной щелчок по видео.
                            _lastClickUtc = DateTime.MinValue;
                            _clickWatchUntil = DateTime.MinValue;
                            _wakeByDoubleClick();
                            return;
                        }
                        _lastClickUtc = now;
                        // Смотрим второй клик ~500 мс даже после пробуждения.
                        _clickWatchUntil = now.AddMilliseconds(600);
                        _wakeByClick();
                        return;
                    }
                }
                else if (newPress)
                {
                    // Второй клик в окне наблюдения после пробуждения.
                    if ((now - _lastClickUtc).TotalMilliseconds <= 500)
                    {
                        _lastClickUtc = DateTime.MinValue;
                        _clickWatchUntil = DateTime.MinValue;
                        _wakeByDoubleClick();
                    }
                    return;
                }

                if (!_hidden)
                {
                    return;
                }
                if (!GetCursorPos(out var pos))
                {
                    return;
                }
                var dx = Math.Abs(pos.X - _lastPointer.X);
                var dy = Math.Abs(pos.Y - _lastPointer.Y);
                _lastPointer = pos;
                if (dx > 2 || dy > 2)
                {
                    _wake();
                }
            };
        }
    }

    // Читается из таймера — volatile.
    private static volatile bool _hidden;

    /// <summary>Спрятать курсор над видео (idempotent).</summary>
    public void Hide()
    {
        _hidden = true;
        InstallMouseHook();

        // Окно моста могло пересоздаться (смена канала/режима) — пересобираем.
        foreach (var old in _bridges)
        {
            ClearTransparent(old);
        }
        _bridges.Clear();
        EnumChildWindows(_mainHwnd, (child, lp) =>
        {
            var buffer = new StringBuilder(64);
            GetClassName(child, buffer, 64);
            if (buffer.ToString() == VideoBridgeClass)
            {
                _bridges.Add(child);
            }
            return true;
        }, IntPtr.Zero);

        foreach (var bridge in _bridges)
        {
            var style = GetWindowLong(bridge, GWL_EXSTYLE);
            SetWindowLong(bridge, GWL_EXSTYLE, (nint)(style | WS_EX_TRANSPARENT));
        }

        GetCursorPos(out _lastPointer);
        _timer?.Start();
        Serilog.Log.Debug("CursorHider: скрытие, видео-окон: {Windows} (мышь прозрачна)",
            _bridges.Count);
    }

    /// <summary>Вернуть обычную мышь над видео (idempotent).</summary>
    public void Show()
    {
        Show(restoreMouse: true);
    }

    /// <summary>
    /// Показ без восстановления мыши: остановить скрытие курсора, но окно
    /// моста оставить прозрачным (для окна двойного клика — оба клика должны
    /// попасть в XAML-подложку, иначе DoubleTapped не собирается).
    /// </summary>
    public void Show(bool restoreMouse)
    {
        _hidden = false;
        if (restoreMouse)
        {
            _timer?.Stop();
            RemoveMouseHook();
            foreach (var bridge in _bridges)
            {
                ClearTransparent(bridge);
            }
        }
        // При restoreMouse=false таймер НЕ останавливаем: идёт наблюдение
        // за вторым кликом двойного щелчка (_clickWatchUntil).
        Serilog.Log.Debug("CursorHider: показ (мышь {Mouse})",
            restoreMouse ? "восстановлена" : "ещё прозрачна");
    }

    // ===================== Хук колеса мыши =====================

    private IntPtr _mouseHook;

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero || _mouseHookProc == null)
        {
            return;
        }
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(IntPtr.Zero), 0);
        Serilog.Log.Debug("CursorHider: хук колеса установлен: {Ok}", _mouseHook != IntPtr.Zero);
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr LowLevelMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_MOUSEWHEEL = 0x020A;
        if (nCode == 0 && (uint)wParam == WM_MOUSEWHEEL && _hidden)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var delta = (short)((info.mouseData >> 16) & 0xFFFF);
            _dispatcher?.TryEnqueue(() => _wheel(delta));
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>Снять прозрачность окна моста (после окна двойного клика).</summary>
    public void RestoreMouse()
    {
        foreach (var bridge in _bridges)
        {
            ClearTransparent(bridge);
        }
    }

    private static void ClearTransparent(IntPtr hwnd)
    {
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, (nint)(style & ~WS_EX_TRANSPARENT));
    }

    public void Dispose()
    {
        _hidden = false;
        _timer?.Stop();
        RemoveMouseHook();
        foreach (var bridge in _bridges)
        {
            ClearTransparent(bridge);
        }
        _bridges.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwnd, EnumChildProc proc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    private const int VK_LBUTTON = 0x01;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // ===================== Win32: LL mouse hook =====================

    private const int WH_MOUSE_LL = 14;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(IntPtr moduleName);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLong(IntPtr hwnd, int index, nint value);
}
