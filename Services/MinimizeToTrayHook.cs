using System;
using System.Runtime.InteropServices;

namespace IptvPlayer.Services;

/// <summary>
/// Перехват сворачивания окна через Win32 subclass (SetWindowSubclass):
/// OverlappedPresenter в используемой версии Windows App SDK не имеет
/// события изменения состояния, поэтому ловим WM_SIZE с SIZE_MINIMIZED
/// в оконной процедуре. Экземпляр должен жить, пока жив окно, — иначе
/// delegate subclass'а соберётся GC и приложение упадёт в wndproc.
/// </summary>
public sealed class MinimizeToTrayHook : IDisposable
{
    private const uint WM_SIZE = 0x0005;
    private const int SIZE_MINIMIZED = 1;

    private readonly IntPtr _hwnd;
    private readonly Action _onMinimized;

    // Держим delegate в поле: SetWindowSubclass хранит только указатель.
    private readonly SubclassProc _proc;

    public MinimizeToTrayHook(Microsoft.UI.Xaml.Window window, Action onMinimized)
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _onMinimized = onMinimized;
        _proc = WndProc;
        SetWindowSubclass(_hwnd, _proc, 0x49505456 /* 'IPTV' */, IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint idSubclass, IntPtr refData)
    {
        if (msg == WM_SIZE && lParam == (IntPtr)SIZE_MINIMIZED)
        {
            try
            {
                _onMinimized();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "MinimizeToTrayHook: обработчик сворачивания упал.");
            }
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        RemoveWindowSubclass(_hwnd, _proc, 0x49505456);
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint idSubclass, IntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint idSubclass, IntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint idSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
