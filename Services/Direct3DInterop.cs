using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace IptvPlayer.Services
{


    internal static unsafe class Direct3DInterop
    {

        private static readonly Guid IidIdxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

        private static readonly Guid IidIdxgiSurface = new("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC");

        private const uint D3D11CreateDeviceBgraSupport = 0x20;
        private const uint D3D11CreateDeviceVideoSupport = 0x800;
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


        public static (IntPtr NativeDevice, IDirect3DDevice Device) CreateDevice()
        {


            var fl = stackalloc uint[] { 0xb000, 0xa100, 0xa000 };
            int hr = D3D11CreateDevice(
                IntPtr.Zero, 1, 0, D3D11CreateDeviceBgraSupport | D3D11CreateDeviceVideoSupport,
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


        public static IDirect3DSurface CreateBgraSurface(IntPtr nativeDevice, int width, int height)
        {
            return CreateSurface(nativeDevice, width, height, DxgiFormatB8G8R8A8UNorm);
        }


        public static IDirect3DSurface CreateSurface(IntPtr nativeDevice, int width, int height, int dxgiFormat)
        {

            var desc = new Texture2DDesc
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = dxgiFormat,
                SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
                Usage = 0,
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
