using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace IptvPlayer.Services;

/// <summary>
/// Иконка в системном трее на голом Win32 (Shell_NotifyIconW): сторонние
/// пакеты (H.NotifyIcon.WinUI) несовместимы с нашей версией Windows App SDK
/// на net8. Левый клик — показать окно, правый — меню «Показать/Выход».
/// Иконка и меню живут в собственном невидимом Win32-окне с родным
/// message loop потоком — XAML не участвует, поэтому просто и стабильно.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint WM_TRAYICON = 0x8000; // WM_APP
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;

    private IntPtr _window;
    private ushort _classAtom;
    private readonly IntPtr _icon;
    private readonly Thread _messageThread;
    private IntPtr _hmenu = IntPtr.Zero;
    private bool _disposed;

    // Возврат в UI-поток приложения (окно создано на UI-потоке).
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly Action _show;
    private readonly Action _exit;

    public TrayIconService(string iconPath, Action show, Action exit)
    {
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _show = show;
        _exit = exit;

        // 32x32 из файла; LR_DEFAULTSIZE обязательна при cx=cy=0.
        _icon = LoadImage(IntPtr.Zero, iconPath, 1 /*IMAGE_ICON*/, 0, 0, 0x10 | 0x40 /*LR_LOADFROMFILE|LR_DEFAULTSIZE*/);
        if (_icon == IntPtr.Zero)
        {
            Serilog.Log.Warning("Трей: не удалось загрузить иконку {Path}.", iconPath);
        }

        var closed = new ManualResetEvent(false);
        _messageThread = new Thread(() =>
        {
            Current = this; // HwndProc выполняется на этом потоке.
            _window = CreateMessageWindow();
            closed.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        });
        _messageThread.IsBackground = true;
        _messageThread.Start();
        closed.WaitOne(2000);
    }

    private IntPtr CreateMessageWindow()
    {
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProc),
            lpszClassName = "IptvPlayerTray"
        };
        _classAtom = RegisterClass(ref wc);
        var hwnd = CreateWindowEx(0, "IptvPlayerTray", "", 0, 0, 0, 0, 0, -3 /*HWND_MESSAGE*/, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return hwnd;
    }

    /// <summary>
    /// Иконка в трее показывается только пока окно приложения скрыто
    /// (свернуто/закрыто в трей). Изначально не добавляется — только по Show().
    /// </summary>
    private readonly object _visibilitySync = new();
    private bool _addedToTray;

    /// <summary>Добавляет иконку в трей (повторные вызовы — no-op).</summary>
    public void Show()
    {
        lock (_visibilitySync)
        {
            if (_addedToTray || _disposed)
            {
                return;
            }
            NotifyTray(0x0 /*NIM_ADD*/);
            _addedToTray = true;
        }
    }

    /// <summary>Убирает иконку из трея (повторные вызовы — no-op).</summary>
    public void Hide()
    {
        lock (_visibilitySync)
        {
            if (!_addedToTray)
            {
                return;
            }
            NotifyTray(0x2 /*NIM_DELETE*/);
            _addedToTray = false;
        }
    }

    private void NotifyTray(uint message)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _window,
            uID = 1,
            uFlags = 0x2 /*NIF_MESSAGE*/ | 0x1 /*NIF_ICON*/ | 0x4 /*NIF_TIP*/,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _icon,
            szTip = "IptvPlayer"
        };
        if (!Shell_NotifyIcon(message, ref data))
        {
            Serilog.Log.Warning("Трей: Shell_NotifyIcon({Message}) не удался (код {Code}).",
                message, Marshal.GetLastWin32Error());
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly WndProcDelegate WndProc = HwndProc;

    private static IntPtr HwndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            var service = Current;
            if (service == null)
            {
                return IntPtr.Zero;
            }

            switch ((uint)lParam.ToInt64())
            {
                case WM_LBUTTONUP:
                    service._dispatcher.TryEnqueue(new Microsoft.UI.Dispatching.DispatcherQueueHandler(service._show));
                    break;

                case WM_RBUTTONUP:
                    service.ShowContextMenu();
                    break;
            }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    [ThreadStatic]
    internal static TrayIconService? Current;

    private void ShowContextMenu()
    {
        // Нативное popup-меню в координатах курсора; commands 100/101.
        _hmenu = CreatePopupMenu();
        AppendMenu(_hmenu, 0, 100, L.T("Pokazat"));
        AppendMenu(_hmenu, 0, 101, L.T("Vykhod"));

        GetCursorPos(out var pt);
        SetForegroundWindow(_window);
        var cmd = TrackPopupMenu(_hmenu, 0x0182 /*TPM_RETURNCMD|TPM_NONOTIFY*/, pt.X, pt.Y, 0, _window, IntPtr.Zero);
        DestroyMenu(_hmenu);
        _hmenu = IntPtr.Zero;

        if (cmd == 100)
        {
            _dispatcher.TryEnqueue(new Microsoft.UI.Dispatching.DispatcherQueueHandler(_show));
        }
        else if (cmd == 101)
        {
            _dispatcher.TryEnqueue(new Microsoft.UI.Dispatching.DispatcherQueueHandler(_exit));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _window,
                uID = 1
            };
            Shell_NotifyIcon(0x2 /*NIM_DELETE*/, ref data);
            PostMessage(_window, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Уборка при выходе — best-effort.
        }
    }

    // ===================== Win32 =====================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    // Полная современная разметка NOTIFYICONDATAW: без корректного cbSize
    // (первое поле) Shell_NotifyIcon молча отклоняет вызов.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion; // union с uTimeout — оба 4 байта.
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved,
        IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
