using System;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Microsoft.UI;

namespace IptvPlayer.Services
{
    /// <summary>
    /// Аппаратный путь рендера frame server через D3D11 Video Processor
    /// (обёртка над VideoProcessorInterop). Кадр масштабируется
    /// видеопроцессором драйвера — на NVIDIA/Intel это RTX VSR / Intel VSR,
    /// если включены в панели драйвера.
    ///
    /// Приоритет выше шейдерного пути (FSR 1.0 / бикубик): при недоступности
    /// или любой ошибке TryRender возвращает false один раз, и
    /// FrameServerRenderer откатывается на шейдеры. Выходная текстура
    /// кэшируется по размеру окна; результат рисуется на свапчейн целиком —
    /// чёрные поля формируются очисткой сессии.
    /// </summary>
    internal sealed class FrameServerVideoProcessor : IDisposable
    {
        /// <summary>Подпись режима для логов.</summary>
        public const string ModeName = "D3D11VP (RTX VSR / Intel VSR)";

        private readonly ILogger _logger;
        private VideoProcessorInterop? _interop;
        private bool _loggedBlt;

        private FrameServerVideoProcessor(VideoProcessorInterop interop, ILogger logger)
        {
            _interop = interop;
            _logger = logger;
        }

        /// <summary>
        /// Пытается создать видеопроцессор на указанном D3D11-девайсе.
        /// Возвращает null, если аппаратный путь недоступен (не ошибка).
        /// </summary>
        public static FrameServerVideoProcessor? TryCreate(IntPtr nativeDevice, ILogger logger)
        {
            var interop = VideoProcessorInterop.TryCreate(nativeDevice, logger);
            return interop is null ? null : new FrameServerVideoProcessor(interop, logger);
        }

        /// <summary>
        /// Масштабирует кадр видеопроцессором и рисует результат на свапчейн.
        /// scale — коэффициенты из ComputeScale рендерера; offsetX/offsetY —
        /// позиция вписанного кадра (для UniformToFill могут быть отрицательными,
        /// видеопроцессор сам обрезает по границе назначения).
        /// Возвращает false, если путь нужно считать недоступным (откат на шейдеры).
        /// </summary>
        public bool TryRender(CanvasDevice canvasDevice, CanvasSwapChain swapChain,
            IDirect3DSurface frameSurface, int frameW, int frameH,
            int outW, int outH, int dstW, int dstH,
            float offsetX, float offsetY, IntPtr nativeDevice)
        {
            if (_interop is null)
            {
                return false;
            }

            try
            {
                var inputTexture = VideoProcessorInterop.GetTextureFromSurface(frameSurface);
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

                // Рисуем только область кадра: VideoProcessorBlt пишет в
                // выходную текстуру исключительно dst-рект, вне его остаются
                // прошлоканальные пиксели (полоски по краям после смены
                // канала с иным разрешением/округлением). Поля формируются
                // чёрной очисткой сессии. Текстура выровнена по окну 1:1,
                // поэтому источник и назначение — один и тот же рект.
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
