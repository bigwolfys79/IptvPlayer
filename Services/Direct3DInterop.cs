using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace IptvPlayer.Services
{
    /// <summary>
    /// Минимальный D3D11-интероп для рендер-пути frame server: создание
    /// устройства, BGRA8-текстуры-приёмника кадра и оборачивание её в
    /// WinRT IDirect3DSurface / IDirect3DDevice.
    ///
    /// Win2D (WinUI3) не создаёт IDirect3DSurface и не отдаёт свой D3D-девайс,
    /// а MediaPlayer.CopyFrameToVideoSurface требует готовую поверхность —
    /// поэтому девайс, текстура и обёртка делаются здесь через стандартные
    /// экспорты d3d11.dll: CreateDirect3D11DeviceFromDXGIBuffer и
    /// CreateDirect3D11DeviceFromDXGIDevice. Текстура живёт на НАШЕМ девайсе,
    /// из которого создаётся и CanvasDevice — Win2D требует совпадения
    /// девайса при CreateFromDirect3D11Surface.
    /// </summary>
    internal static unsafe class Direct3DInterop
    {
        // IID_IDXGIDevice {54EC77FA-1377-44E6-8C32-88FD5F44C84C}
        private static readonly Guid IidIdxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
        // IID_IDXGISurface {CAFCB56C-6AC3-4889-BF47-9E23BBD260EC}
        private static readonly Guid IidIdxgiSurface = new("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC");

        private const uint D3D11CreateDeviceBgraSupport = 0x20;
        private const int DxgiFormatB8G8R8A8UNorm = 87;
        private const uint D3D11BindRenderTarget = 0x20;
        private const uint D3D11BindShaderResource = 0x8;

        [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", PreserveSig = true)]
        private static extern int D3D11CreateDevice(
            IntPtr adapter, uint driverType, uint software, uint flags,
            IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
            out IntPtr device, IntPtr featureLevelOut, out IntPtr immediateContext);

        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = true)]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice, out IntPtr inspectable);

        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11SurfaceFromDXGISurface", PreserveSig = true)]
        private static extern int CreateDirect3D11SurfaceFromDXGISurface(
            IntPtr dxgiSurface, out IntPtr inspectable);

        /// <summary>
        /// Создаёт D3D11-девайс и возвращает (нативный девайс, IDirect3DDevice).
        /// Нативный указатель нужен для создания текстур.
        /// </summary>
        public static (IntPtr NativeDevice, IDirect3DDevice Device) CreateDevice()
        {
            // Уровни фич: 11.0 = 0xB000, 10.1 = 0xA100, 10.0 = 0xA000;
            // driverType 1 = D3D_DRIVER_TYPE_HARDWARE.
            var fl = stackalloc uint[] { 0xb000, 0xa100, 0xa000 };
            int hr = D3D11CreateDevice(
                IntPtr.Zero, 1, 0, D3D11CreateDeviceBgraSupport,
                (IntPtr)fl, 3, 7, out var device, IntPtr.Zero, out var context);
            Marshal.ThrowExceptionForHR(hr);
            _ = context;

            var iid = IidIdxgiDevice;
            var dxgiDevice = QueryInterfaceRaw(device, iid);
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable);
            Marshal.ThrowExceptionForHR(hr);
            Marshal.Release(dxgiDevice);

            var d3dDevice = WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
            return (device, d3dDevice);
        }

        /// <summary>
        /// Создаёт BGRA8-текстуру на девайсе и оборачивает её в IDirect3DSurface
        /// (приёмник кадра для CopyFrameToVideoSurface).
        /// </summary>
        public static IDirect3DSurface CreateBgraSurface(IntPtr nativeDevice, int width, int height)
        {
            // ID3D11Device vtable: CreateTexture2D = слот 5 после IUnknown.
            var desc = new Texture2DDesc
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DxgiFormatB8G8R8A8UNorm,
                SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
                Usage = 0, // D3D11_USAGE_DEFAULT
                BindFlags = D3D11BindRenderTarget | D3D11BindShaderResource,
                CPUAccessFlags = 0,
                MiscFlags = 0
            };

            var vtbl = *(IntPtr**)nativeDevice;
            var createTexture2D = (delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)vtbl[5];

            IntPtr texture = IntPtr.Zero;
            int hr = createTexture2D(nativeDevice, &desc, IntPtr.Zero, &texture);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                var dxgiSurface = QueryInterfaceRaw(texture, IidIdxgiSurface);
                try
                {
                    var wrapHr = CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface, out var inspectable);
                    Marshal.ThrowExceptionForHR(wrapHr);
                    try
                    {
                        return WinRT.MarshalInspectable<IDirect3DSurface>.FromAbi(inspectable);
                    }
                    finally
                    {
                        Marshal.Release(inspectable);
                    }
                }
                finally
                {
                    Marshal.Release(dxgiSurface);
                }
            }
            finally
            {
                Marshal.Release(texture);
            }
        }

        private static IntPtr QueryInterfaceRaw(IntPtr unknown, Guid iid)
        {
            var vtbl = *(IntPtr**)unknown;
            var qi = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[0];
            IntPtr result = IntPtr.Zero;
            int hr = qi(unknown, &iid, &result);
            if (hr < 0)
            {
                // E_NOINTERFACE здесь отображается как InvalidCastException —
                // логируем исходный HRESULT для диагностики.
                throw new COMException($"QueryInterface {{${iid}}} failed", hr);
            }
            return result;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Texture2DDesc
        {
            public uint Width;
            public uint Height;
            public uint MipLevels;
            public uint ArraySize;
            public int Format;
            public SampleDesc SampleDesc;
            public uint Usage;
            public uint BindFlags;
            public uint CPUAccessFlags;
            public uint MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SampleDesc
        {
            public uint Count;
            public uint Quality;
        }
    }
}
