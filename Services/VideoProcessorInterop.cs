using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace IptvPlayer.Services
{


    internal sealed unsafe class VideoProcessorInterop : IDisposable
    {
        private static readonly Guid IidId3d11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");


        private static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

        private const int DxgiFormatB8G8R8A8UNorm = 87;
        private const uint D3D11BindRenderTarget = 0x20;
        private const uint D3D11BindShaderResource = 0x8;

        private readonly ILogger _logger;


        private IntPtr _videoDevice;


        private IntPtr _videoContext;


        private IntPtr _enumerator;


        private IntPtr _processor;

        private VideoProcessorInterop(ILogger logger)
        {
            _logger = logger;
        }


        public static VideoProcessorInterop? TryCreate(IntPtr nativeDevice, ILogger logger)
        {
            if (nativeDevice == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var vp = new VideoProcessorInterop(logger);
                vp.Initialize(nativeDevice);
                return vp;
            }
            catch (Exception ex)
            {
                logger.LogInformation(ex,
                    "VideoProcessorInterop: видеопроцессор недоступен, остаёмся на шейдерном пути.");
                return null;
            }
        }

        private void Initialize(IntPtr nativeDevice)
        {
            var iidVideoDevice = new Guid("10EC4D5B-975A-4689-B9E4-D0AAC30FE333");
            _videoDevice = QueryInterfaceRaw(nativeDevice, iidVideoDevice);


            var getImmediateContext =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)(*(IntPtr**)nativeDevice)[40];
            IntPtr context = IntPtr.Zero;
            getImmediateContext(nativeDevice, &context);
            if (context == IntPtr.Zero)
            {
                throw new COMException("GetImmediateContext вернул нулевой контекст");
            }

            try
            {
                var iidVideoContext = new Guid("61F21C45-3C0E-4A74-9CEA-67100D9AD5E4");
                _videoContext = QueryInterfaceRaw(context, iidVideoContext);
            }
            finally
            {
                Marshal.Release(context);
            }


            var desc = new VideoProcessorContentDesc
            {
                InputFrameFormat = 0,
                InputRateNumerator = 60,
                InputRateDenominator = 1,
                InputWidth = 1920,
                InputHeight = 1080,
                OutputRateNumerator = 60,
                OutputRateDenominator = 1,
                OutputWidth = 1920,
                OutputHeight = 1080,
                Usage = 0
            };

            var createEnumerator =
                (delegate* unmanaged[Stdcall]<IntPtr, VideoProcessorContentDesc*, IntPtr*, int>)(
                    *(IntPtr**)_videoDevice)[10];
            IntPtr enumerator = IntPtr.Zero;
            int hr = createEnumerator(_videoDevice, &desc, &enumerator);
            if (hr < 0)
            {
                throw new COMException($"CreateVideoProcessorEnumerator hr=0x{hr:X8}", hr);
            }
            _enumerator = enumerator;

            var createProcessor =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr*, int>)(
                    *(IntPtr**)_videoDevice)[4];
            IntPtr processor = IntPtr.Zero;
            hr = createProcessor(_videoDevice, _enumerator, 0, &processor);
            if (hr < 0)
            {
                throw new COMException($"CreateVideoProcessor hr=0x{hr:X8}", hr);
            }
            _processor = processor;

            _logger.LogInformation(
                "VideoProcessorInterop: D3D11 Video Processor создан (аппаратный путь доступен).");
        }


        public static IntPtr GetTextureFromSurface(IDirect3DSurface surface)
        {
            try
            {
                var surfacePtr = ((IWinRTObject)surface).NativeObject.ThisPtr;
                var access = QueryInterfaceStatic(surfacePtr, IidDxgiInterfaceAccess);
                try
                {
                    var getInterface =
                        (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(
                            *(IntPtr**)access)[3];
                    var iid = IidId3d11Texture2D;
                    IntPtr texture = IntPtr.Zero;
                    int hr = getInterface(access, &iid, &texture);
                    return hr >= 0 ? texture : IntPtr.Zero;
                }
                finally
                {
                    Marshal.Release(access);
                }
            }
            catch
            {
                return IntPtr.Zero;
            }
        }


        public IDirect3DSurface? EnsureOutput(IntPtr nativeDevice, int width, int height)
        {
            if (_outputTexture != IntPtr.Zero && _outputWidth == width && _outputHeight == height)
            {
                return _outputSurface;
            }

            ReleaseOutput();

            var desc = new Texture2DDesc
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DxgiFormatB8G8R8A8UNorm,
                SampleDesc = new SampleDesc { Count = 1, Quality = 0 },
                Usage = 0,
                BindFlags = D3D11BindRenderTarget | D3D11BindShaderResource,
                CPUAccessFlags = 0,
                MiscFlags = 0
            };

            var createTexture2D =
                (delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)(
                    *(IntPtr**)nativeDevice)[5];
            IntPtr texture = IntPtr.Zero;
            int hr = createTexture2D(nativeDevice, &desc, IntPtr.Zero, &texture);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
            _outputTexture = texture;

            var outputViewDesc = new VideoProcessorOutputViewDesc
            {
                ViewDimension = 1,
                MipSlice = 0
            };

            var createOutputView =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, VideoProcessorOutputViewDesc*, IntPtr*, int>)
                    (*(IntPtr**)_videoDevice)[9];
            IntPtr outputView = IntPtr.Zero;
            hr = createOutputView(_videoDevice, _outputTexture, _enumerator, &outputViewDesc, &outputView);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
            _outputView = outputView;

            _outputSurface = WrapTextureAsSurface(_outputTexture);
            _outputWidth = width;
            _outputHeight = height;
            return _outputSurface;
        }


        public void Blt(IntPtr inputTexture, int frameW, int frameH, Rect dst, int outW, int outH)
        {
            var inputViewDesc = new VideoProcessorInputViewDesc
            {
                FourCC = 0,
                ViewDimension = 1,
                MipSlice = 0,
                ArraySlice = 0
            };

            var createInputView =
                (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, VideoProcessorInputViewDesc*, IntPtr*, int>)(
                    *(IntPtr**)_videoDevice)[8];
            IntPtr inputView = IntPtr.Zero;
            int hr = createInputView(_videoDevice, inputTexture, _enumerator, &inputViewDesc, &inputView);
            if (hr < 0)
            {
                throw new COMException($"CreateVideoProcessorInputView hr=0x{hr:X8}", hr);
            }
            try
            {


                var srcRect = new RawRect(0, 0, frameW, frameH);
                var dstRect = new RawRect((int)dst.X, (int)dst.Y, (int)(dst.X + dst.Width), (int)(dst.Y + dst.Height));


                float kx = frameW / dst.Width;
                float ky = frameH / dst.Height;
                int clipL = Math.Max(0, -dstRect.Left);
                int clipT = Math.Max(0, -dstRect.Top);
                int clipR = Math.Max(0, dstRect.Right - outW);
                int clipB = Math.Max(0, dstRect.Bottom - outH);
                dstRect = new RawRect(
                    dstRect.Left + clipL,
                    dstRect.Top + clipT,
                    dstRect.Right - clipR,
                    dstRect.Bottom - clipB);
                srcRect = new RawRect(
                    srcRect.Left + (int)(clipL * kx),
                    srcRect.Top + (int)(clipT * ky),
                    srcRect.Right - (int)(clipR * kx),
                    srcRect.Bottom - (int)(clipB * ky));

                var setStreamSourceRect =
                    (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, RawRect*, void>)(
                        *(IntPtr**)_videoContext)[30];
                setStreamSourceRect(_videoContext, _processor, 0, 1, &srcRect);


                var setStreamDestRect =
                    (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, RawRect*, void>)(
                        *(IntPtr**)_videoContext)[31];
                setStreamDestRect(_videoContext, _processor, 0, 1, &dstRect);


                var setOutputTargetRect =
                    (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, RawRect*, void>)(
                        *(IntPtr**)_videoContext)[13];
                setOutputTargetRect(_videoContext, _processor, 1, &dstRect);


                var setStreamFrameFormat =
                    (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, void>)(
                        *(IntPtr**)_videoContext)[27];
                setStreamFrameFormat(_videoContext, _processor, 0, 0);

                var stream = new VideoProcessorStream
                {
                    Enable = 1,
                    OutputIndex = 0,
                    InputFrameOrField = 0,
                    PastFrames = 0,
                    FutureFrames = 0,
                    PastSurfaces = IntPtr.Zero,
                    InputSurface = inputView,
                    FutureSurfaces = IntPtr.Zero,
                    PastSurfacesRight = IntPtr.Zero,
                    InputSurfaceRight = IntPtr.Zero,
                    FutureSurfacesRight = IntPtr.Zero
                };

                var blt =
                    (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, uint, VideoProcessorStream*, int>)(
                        *(IntPtr**)_videoContext)[53];
                hr = blt(_videoContext, _processor, _outputView, 0, 1, &stream);
                if (hr < 0)
                {
                    throw new COMException($"VideoProcessorBlt hr=0x{hr:X8}", hr);
                }
            }
            finally
            {
                Marshal.Release(inputView);
            }
        }

        public void Dispose()
        {
            ReleaseOutput();
            Release(ref _processor);
            Release(ref _enumerator);
            Release(ref _videoContext);
            Release(ref _videoDevice);
        }

        private IDirect3DSurface? _outputSurface;
        private IntPtr _outputTexture;
        private IntPtr _outputView;
        private int _outputWidth, _outputHeight;

        private static IDirect3DSurface WrapTextureAsSurface(IntPtr texture)
        {
            var iid = new Guid("CAFCB56C-6AC3-4889-BF47-9E23BBD260EC");
            var dxgiSurface = QueryInterfaceStatic(texture, iid);
            try
            {
                int hr = CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface, out var inspectable);
                Marshal.ThrowExceptionForHR(hr);
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

        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11SurfaceFromDXGISurface", PreserveSig = true)]
        private static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr inspectable);

        private static void Release(ref IntPtr ptr)
        {
            if (ptr != IntPtr.Zero)
            {
                Marshal.Release(ptr);
                ptr = IntPtr.Zero;
            }
        }

        private void ReleaseOutput()
        {
            if (_outputSurface is not null)
            {
                _outputSurface = null;
            }
            Release(ref _outputView);
            Release(ref _outputTexture);
            _outputWidth = 0;
            _outputHeight = 0;
        }

        private static IntPtr QueryInterfaceStatic(IntPtr unknown, Guid iid)
        {
            var vtbl = *(IntPtr**)unknown;
            var qi = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[0];
            IntPtr result = IntPtr.Zero;
            int hr = qi(unknown, &iid, &result);
            Marshal.ThrowExceptionForHR(hr);
            return result;
        }

        private static IntPtr QueryInterfaceRaw(IntPtr unknown, Guid iid)
        {
            return QueryInterfaceStatic(unknown, iid);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct VideoProcessorContentDesc
        {
            public uint InputFrameFormat;
            public uint InputRateNumerator;
            public uint InputRateDenominator;
            public uint InputWidth;
            public uint InputHeight;
            public uint OutputRateNumerator;
            public uint OutputRateDenominator;
            public uint OutputWidth;
            public uint OutputHeight;
            public uint Usage;
        }


        [StructLayout(LayoutKind.Sequential)]
        private struct VideoProcessorInputViewDesc
        {
            public uint FourCC;
            public uint ViewDimension;
            public uint MipSlice;
            public uint ArraySlice;
        }


        [StructLayout(LayoutKind.Sequential)]
        private struct VideoProcessorOutputViewDesc
        {
            public uint ViewDimension;
            public uint MipSlice;
        }


        [StructLayout(LayoutKind.Sequential)]
        private struct VideoProcessorStream
        {
            public int Enable;
            public uint OutputIndex;
            public uint InputFrameOrField;
            public uint PastFrames;
            public uint FutureFrames;
            public IntPtr PastSurfaces;
            public IntPtr InputSurface;
            public IntPtr FutureSurfaces;
            public IntPtr PastSurfacesRight;
            public IntPtr InputSurfaceRight;
            public IntPtr FutureSurfacesRight;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawRect
        {
            public RawRect(int left, int top, int right, int bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }

            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }


        public readonly struct Rect
        {
            public Rect(float x, float y, float width, float height)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
            }

            public float X { get; }
            public float Y { get; }
            public float Width { get; }
            public float Height { get; }
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
