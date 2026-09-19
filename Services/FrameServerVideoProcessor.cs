using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Microsoft.UI;

namespace IptvPlayer.Services
{


    internal sealed class FrameServerVideoProcessor : IDisposable
    {

        public const string ModeName = "D3D11VP (RTX VSR / Intel VSR)";

        private readonly ILogger _logger;
        private VideoProcessorInterop? _interop;
        private bool _loggedBlt;

        private FrameServerVideoProcessor(VideoProcessorInterop interop, ILogger logger)
        {
            _interop = interop;
            _logger = logger;
        }


        public static FrameServerVideoProcessor? TryCreate(IntPtr nativeDevice, ILogger logger)
        {
            var interop = VideoProcessorInterop.TryCreate(nativeDevice, logger);
            return interop is null ? null : new FrameServerVideoProcessor(interop, logger);
        }


        public bool TryRender(CanvasDevice canvasDevice, CanvasSwapChain swapChain,
            IDirect3DSurface frameSurface, int frameW, int frameH,
            int outW, int outH, int dstW, int dstH,
            float offsetX, float offsetY, IntPtr nativeDevice)
        {
            if (_interop is null)
            {
                return false;
            }

            var inputTexture = VideoProcessorInterop.GetTextureFromSurface(frameSurface);
            try
            {
                if (inputTexture == IntPtr.Zero)
                {
                    _logger.LogInformation(
                        "FrameServerVideoProcessor: входная текстура недоступна, откат на шейдерный путь.");
                    return false;
                }

                var outputSurface = _interop.EnsureOutput(nativeDevice, outW, outH);
                if (outputSurface is null)
                {
                    return false;
                }

                var dst = new VideoProcessorInterop.Rect(offsetX, offsetY, dstW, dstH);
                _interop.Blt(inputTexture, frameW, frameH, dst, outW, outH);
                if (!_loggedBlt)
                {
                    _loggedBlt = true;
                    _logger.LogInformation(
                        "FrameServerVideoProcessor: Blt идёт ({FW}x{FH} → {DW}x{DH}); входной формат поверхности {Input}.",
                        frameW, frameH, dstW, dstH,
                        frameSurface.Description.Format);
                }

                using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(canvasDevice, outputSurface);


                var left = Math.Max(0f, offsetX);
                var top = Math.Max(0f, offsetY);
                var right = Math.Min((float)outW, offsetX + dstW);
                var bottom = Math.Min((float)outH, offsetY + dstH);
                if (right - left < 1f || bottom - top < 1f)
                {
                    left = 0;
                    top = 0;
                    right = outW;
                    bottom = outH;
                }
                var visible = new Rect(left, top, right - left, bottom - top);

                using var session = swapChain.CreateDrawingSession(Colors.Black);
                session.DrawImage(bitmap, visible, visible, 1f,
                    CanvasImageInterpolation.NearestNeighbor);
                swapChain.Present();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "FrameServerVideoProcessor: ошибка Video Processor, откат на шейдерный путь.");
                DisposeInterop();
                return false;
            }
            finally
            {
                // GetTextureFromSurface returns an AddRef'd raw texture
                if (inputTexture != IntPtr.Zero)
                {
                    Marshal.Release(inputTexture);
                }
            }
        }

        public void Dispose()
        {
            DisposeInterop();
        }

        private void DisposeInterop()
        {
            _interop?.Dispose();
            _interop = null;
        }
    }
}
