using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer.Controls;

/// <summary>
/// Корневой Grid страницы с управлением видимостью курсора. UIElement.ProtectedCursor
/// в используемой версии Windows App SDK — protected, поэтому доступ только из
/// наследника. Это единственный вход в input-site WinUI: ShowCursor потокозависим
/// (курсор держит поток input-site), WM_SETCURSOR сайту не приходит, обнуление
/// GCLP_HCURSOR игнорируется. Невидимый курсор делаем через Win32 CreateCursor
/// (полностью прозрачные маски) и IInputCursorStaticsInterop.CreateFromHCursor —
/// по рецепту Simon Mourier (simonmourier.com/blog/Cursor-cur-in-WinUI-3).
/// </summary>
public sealed partial class CursorGrid : Grid
{
    private static Microsoft.UI.Input.InputCursor? _hiddenCursor;

    /// <summary>Прячет курсор, пока указатель над окном.</summary>
    public void HideCursorOverWindow()
    {
        if (_hiddenCursor == null)
        {
            _hiddenCursor = CreateInvisibleCursor();
            Serilog.Log.Debug("CursorGrid: невидимый курсор создан: {Ok}", _hiddenCursor != null);
        }
        if (_hiddenCursor != null)
        {
            ProtectedCursor = _hiddenCursor;
        }
    }

    /// <summary>Возвращает системный курсор по умолчанию.</summary>
    public void ShowCursorOverWindow()
    {
        ProtectedCursor = null;
    }

    /// <summary>
    /// Новая независимая копия невидимого курсора — для другого элемента:
    /// один и тот же экземпляр InputCursor на двух элементах одновременно
    /// input-site, судя по всему, не поддерживает (молча снимает с первого).
    /// </summary>
    /// <summary>
    /// Общий невидимый курсор (создаётся один раз). Повторные CreateCursor/
    /// CreateFromHCursor в одном сеансе падают с 0x80070716 (ресурс не
    /// найден), поэтому плодить по экземпляру на элемент нельзя.
    /// </summary>
    private static Microsoft.UI.Input.InputCursor? _sharedHiddenCursor;

    public Microsoft.UI.Input.InputCursor? CreateHiddenCursor()
    {
        if (_sharedHiddenCursor != null)
        {
            return _sharedHiddenCursor;
        }
        _sharedHiddenCursor = CreateInvisibleCursor();
        Serilog.Log.Information("CursorGrid: общий невидимый курсор создан: {Ok}",
            _sharedHiddenCursor != null);
        return _sharedHiddenCursor;
    }

    private static Microsoft.UI.Input.InputCursor? CreateInvisibleCursor()
    {
        try
        {
            // 32x32, моно-маски: AND=1 — пиксель курсора прозрачен. Все единицы
            // в AND и нули в XOR дают полностью невидимый курсор.
            const int width = 32;
            const int height = 32;
            const int bytesPerRow = width / 8; // 4 байта на строку моно-маски
            var and = new byte[bytesPerRow * height];
            var xor = new byte[bytesPerRow * height];
            for (var i = 0; i < and.Length; i++)
            {
                and[i] = 0xFF;
            }

            var hInst = GetModuleHandle(IntPtr.Zero);
            var hcursor = CreateCursor(hInst, 0, 0, width, height, and, xor);
            Serilog.Log.Debug("CursorGrid: CreateCursor → hcursor=0x{HCursor:X}", hcursor.ToInt64());
            if (hcursor == IntPtr.Zero)
            {
                Serilog.Log.Warning("CursorGrid: CreateCursor не удался (код {Code}).",
                    Marshal.GetLastWin32Error());
                return null;
            }

            return CreateCursorFromHCursor(hcursor);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "CursorGrid: не удалось создать невидимый курсор.");
            return null;
        }
    }

    /// <summary>
    /// Оборачивание нативного HCURSOR в WinUI InputCursor через
    /// IInputCursorStaticsInterop (публичного API CreateFromHCursor в C#
    /// проекции нет).
    /// </summary>
    private static Microsoft.UI.Input.InputCursor? CreateCursorFromHCursor(nint hcursor)
    {
        if (hcursor == 0)
        {
            return null;
        }
        const string classId = "Microsoft.UI.Input.InputCursor";
        // Прямой P/Invoke RoGetActivationFactory возвращает E_INVALIDARG —
        // берём фабрику штатным механизмом CsWinRT (он же используется для
        // всех WinRT-активаций приложения) и кастуем к COM-интерфейсу.
        var interop = WinRT.ActivationFactory.Get(classId)
            .AsInterface<IInputCursorStaticsInterop>();
        Marshal.ThrowExceptionForHR(interop.CreateFromHCursor(hcursor, out var cursorAbi));
        Serilog.Log.Debug("CursorGrid: CreateFromHCursor abi=0x{Abi:X}", cursorAbi);
        if (cursorAbi == 0)
        {
            return null;
        }
        return WinRT.MarshalInspectable<Microsoft.UI.Input.InputCursor>.FromAbi(cursorAbi);
    }

    [ComImport, Guid("ac6f5065-90c4-46ce-beb7-05e138e54117"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInputCursorStaticsInterop
    {
        // IInspectable unused methods
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        [PreserveSig]
        int CreateFromHCursor(nint hcursor, out nint inputCursor);
    }

    [ComImport, Guid("00000035-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivationFactory
    {
    }

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll")]
    private static extern int WindowsCreateString(string source, int length, out nint hstring);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll")]
    private static extern int RoGetActivationFactory(nint runtimeClassId, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IActivationFactory factory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateCursor(IntPtr hInst, int xHotSpot, int yHotSpot,
        int nWidth, int nHeight, byte[] pvANDPlane, byte[] pvXORPlane);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(IntPtr moduleName);
}
