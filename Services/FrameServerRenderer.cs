using System;
using System.IO;
using System.Threading;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Extensions.Logging;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Playback;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace IptvPlayer.Services
{
    /// <summary>
    /// Рендер-путь «frame server» (экспериментальный апскейл, фаза 2):
    /// MediaPlayer работает с IsVideoFrameServerEnabled и сам ничего не
    /// рисует. Кадр копируется в текстуру РАЗРЕШЕНИЯ ПОТОКА
    /// (CopyFrameToVideoSurface), а в окно рисуется двумя HLSL-проходами:
    ///   1) FsrEasu.cso — FSR 1.0 EASU (порт AMD ffx_fsr1.h, MIT):
    ///      edge-адаптивный апскейл, кадр вписывается в окно единым
    ///      коэффициентом Math.Min(w/frameW, h/frameH);
    ///   2) FsrRcas.cso — FSR 1.0 RCAS: резкость с robust-лимитерами.
    /// Фолбэк при отсутствии FSR-шейдеров: бикубик Catmull-Rom (Upscale.cso)
    /// + упрощённый CAS (Sharpen.cso); далее — линейный масштаб движка.
    /// Это даёт заметно более чёткую картинку, чем линейный масштаб
    /// медиа-движка, особенно на апскейле SD (×2+). Шейдеры — Assets/Shaders.
    ///
    /// Девайс один и тот же для текстуры-приёмника и для Win2D (Win2D требует
    /// совпадения девайса при CreateFromDirect3D11Surface) — см.
    /// Direct3DInterop. Кадры приходят на фоновом потоке: рисуем там же под
    /// атомарным флагом (кадр пропускается, если предыдущий ещё рисуется),
    /// UI-поток нужен только для создания/ресайза свапчейна.
    /// При любой ошибке шейдерного пути — однократный откат на прямую
    /// отрисовку (линейный масштаб движка), без падения плеера.
    /// </summary>
    public sealed class FrameServerRenderer : IDisposable
    {
        private readonly ILogger _logger;
        private volatile int _drawing;
        private CanvasDevice? _canvasDevice;
        private IntPtr _nativeDevice;
        private IDirect3DDevice? _d3dDevice;


        private IDirect3DSurface? _frameSurface;
        private int _frameWidth, _frameHeight;
        private bool _frameIsNv12;


        private CanvasRenderTarget? _upscaledTarget;

        private PixelShaderEffect? _upscaleEffect;
        private PixelShaderEffect? _sharpenEffect;


        private PixelShaderEffect? _fsrEasuEffect;
        private PixelShaderEffect? _fsrRcasEffect;
        private bool _shaderPathBroken;

        private FrameServerVideoProcessor? _videoProcessor;

        private MediaPlayer? _player;
        private CanvasSwapChainPanel? _panel;
        private int _errorCount;
        private int _streamWidth, _streamHeight;
        private bool _loggedScaleInfo;
        private float _cachedDpi = 96f;

        private const float Sharpening = 0.80f;

        /// <summary>
        /// Режим отображения (дублирует MediaPlayer.Stretch, который при
        /// frame server-рендере не участвует в отрисовке): Uniform — вписать
        /// с чёрными полями, UniformToFill — заполнить окно с обрезкой краёв,
        /// Fill — растянуть без сохранения пропорций.
        /// </summary>
        public Stretch VideoStretchMode { get; set; } = Stretch.Uniform;

        public FrameServerRenderer(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Подключает рендер к панели и играющему плееру. UI-поток.
        /// streamWidth/streamHeight — разрешение потока (для лога и размера
        /// текстуры-приёмника).
        /// </summary>
        public void Attach(CanvasSwapChainPanel panel, MediaPlayer player,
            int streamWidth = 0, int streamHeight = 0)
        {
            Detach();

            if (_canvasDevice is null)
            {
                (_nativeDevice, _d3dDevice) = Direct3DInterop.CreateDevice();
                _canvasDevice = CanvasDevice.CreateFromDirect3D11Device(_d3dDevice);
                _videoProcessor = FrameServerVideoProcessor.TryCreate(_nativeDevice, _logger);
                _fsrEasuEffect = LoadEffect("FsrEasu.cso");
                _fsrRcasEffect = LoadEffect("FsrRcas.cso");
                _upscaleEffect = LoadEffect("Upscale.cso");
                _sharpenEffect = LoadEffect("Sharpen.cso");
                _logger.LogInformation(
                    "FrameServerRenderer: шейдерный путь — {Path}.",
                    _fsrEasuEffect != null && _fsrRcasEffect != null
                        ? "FSR 1.0 (EASU+RCAS)"
                        : _upscaleEffect != null && _sharpenEffect != null
                            ? "бикубик + CAS (FSR не загружен)"
                            : "откат на линейный");
            }

            _panel = panel;
            _player = player;
            _loggedScaleInfo = false;
            _streamWidth = streamWidth;
            _streamHeight = streamHeight;
            RecreateSwapChain();
            panel.SizeChanged += Panel_SizeChanged;
            player.VideoFrameAvailable += Player_VideoFrameAvailable;
            _logger.LogInformation(
                "FrameServerRenderer: подключён к плееру (поток {SW}x{SH}, шейдеры {Shaders}).",
                streamWidth, streamHeight,
                _fsrEasuEffect != null && _fsrRcasEffect != null ? "FSR 1.0"
                    : _upscaleEffect != null && _sharpenEffect != null ? "бикубик" : "откат на линейный");
        }

        private PixelShaderEffect? LoadEffect(string fileName)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", fileName);
                return new PixelShaderEffect(File.ReadAllBytes(path));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FrameServerRenderer: шейдер {File} не загружен.", fileName);
                return null;
            }
        }

        /// <summary>
        /// Отключает рендер (смена канала, выключение режима).
        /// </summary>
        public void Detach()
        {
            if (_player != null)
            {
                _player.VideoFrameAvailable -= Player_VideoFrameAvailable;
                _player = null;
            }
            if (_panel != null)
            {
                _panel.SizeChanged -= Panel_SizeChanged;
                _panel.SwapChain = null;
                _panel = null;
            }
        }

        public void Dispose()
        {
            Detach();
            _videoProcessor?.Dispose();
            _videoProcessor = null;
            _frameSurface?.Dispose();
            _frameSurface = null;
            _upscaledTarget?.Dispose();
            _upscaledTarget = null;
            _canvasDevice?.Dispose();
            _canvasDevice = null;
            _d3dDevice = null;
            _nativeDevice = IntPtr.Zero;
        }

        private void Panel_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
            if (sender is CanvasSwapChainPanel panel && panel.DispatcherQueue.HasThreadAccess)
            {
                RecreateSwapChain();
            }
        }

        private System.Diagnostics.Stopwatch? _recreatedAt;

        private void RecreateSwapChain()
        {
            RecreateSwapChainCore();
        }

        private void RecreateSwapChainCore()
        {
            if (_panel is null || _canvasDevice is null)
            {
                return;
            }

            try
            {
                var w = (float)Math.Max(1, _panel.ActualWidth);
                var h = (float)Math.Max(1, _panel.ActualHeight);
                var dpiScale = _panel.XamlRoot?.RasterizationScale ?? 1.0;
                var dpi = (float)(dpiScale * 96.0);
                _cachedDpi = dpi;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _panel.SwapChain = new CanvasSwapChain(_canvasDevice, w, h, 60f,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, CanvasAlphaMode.Ignore);
                var createMs = sw.Elapsed.TotalMilliseconds;
                _upscaledTarget = null;
                _recreatedAt = System.Diagnostics.Stopwatch.StartNew();
                _logger.LogInformation(
                    "FrameServerRenderer: свапчейн {W}x{H} (dpi {Dpi:F0}, scale {Scale:F2}) за {Ms:F0} мс.",
                    w, h, dpi, dpiScale, createMs);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FrameServerRenderer: не удалось создать свапчейн.");
            }
        }

        private void Player_VideoFrameAvailable(MediaPlayer sender, object args)
        {
            if (_canvasDevice is null || _panel?.SwapChain is null)
            {
                return;
            }

            var session = sender.PlaybackSession;
            if (session is null || session.PlaybackState != MediaPlaybackState.Playing)
            {
                return;
            }

            if (Interlocked.Exchange(ref _drawing, 1) == 1)
            {


                return;
            }

            try
            {
                Render(sender);
            }
            catch (Exception ex)
            {

                if (_errorCount < 5)
                {
                    _errorCount++;
                    _logger.LogWarning(ex, "FrameServerRenderer: ошибка рендера кадра ({N}).", _errorCount);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _drawing, 0);
            }
        }

        private void Render(MediaPlayer sender)
        {
            var swapChain = _panel?.SwapChain;
            if (swapChain is null)
            {
                return;
            }

            var w = (int)swapChain.Size.Width;
            var h = (int)swapChain.Size.Height;
            if (w < 1 || h < 1)
            {
                return;
            }

            // Размер кадра — NaturalVideoWidth/Height сессии: фактическое
            // отображаемое разрешение (учитывает SAR и поворот), обновляется
            // при смене потока без пересоздания плеера. Размеры из диагностики
            // Attach бывают нулевыми или пиксельными — тогда приёмник получал
            // чужие пропорции, и растяжение/кроп считались неверно.
            var session = sender.PlaybackSession;
            var frameW = 0;
            var frameH = 0;
            if (session != null)
            {
                frameW = (int)session.NaturalVideoWidth;
                frameH = (int)session.NaturalVideoHeight;
            }
            if (frameW <= 0)
            {
                frameW = _streamWidth > 0 ? _streamWidth : w;
            }
            if (frameH <= 0)
            {
                frameH = _streamHeight > 0 ? _streamHeight : h;
            }

            // Видеопроцессорный путь принимает кадр в NV12 (нативный формат
            // frame server и обязательное условие подстановки RTX VSR драйвером),
            // шейдерный путь Win2D требует BGRA. NV12 требует чётные размеры.
            var wantNv12 = _videoProcessor is not null;
            if (wantNv12 && ((frameW & 1) != 0 || (frameH & 1) != 0))
            {
                frameW += frameW & 1;
                frameH += frameH & 1;
            }
            if (_frameSurface is null || _frameWidth != frameW || _frameHeight != frameH ||
                _frameIsNv12 != wantNv12)
            {
                _frameSurface?.Dispose();
                const int DxgiFormatNv12 = 103;
                const int DxgiFormatBgra = 87;
                _frameSurface = wantNv12
                    ? Direct3DInterop.CreateSurface(_nativeDevice, frameW, frameH, DxgiFormatNv12)
                    : Direct3DInterop.CreateSurface(_nativeDevice, frameW, frameH, DxgiFormatBgra);
                _frameWidth = frameW;
                _frameHeight = frameH;
                _frameIsNv12 = wantNv12;
                _logger.LogInformation(
                    "FrameServerRenderer: приёмник кадра {Format} {W}x{H}.",
                    wantNv12 ? "NV12" : "BGRA", frameW, frameH);

                sender.SetSurfaceSize(new Size(frameW, frameH));
            }

            try
            {
                sender.CopyFrameToVideoSurface(_frameSurface);
            }
            catch (Exception ex) when (_frameIsNv12)
            {
                // NV12-приёмник не поддержан окружением — отключаем
                // видеопроцессорный путь, следующий кадр придёт в BGRA.
                _logger.LogWarning(ex,
                    "FrameServerRenderer: копирование кадра в NV12 не удалось, откат на BGRA (шейдерный путь).");
                _videoProcessor?.Dispose();
                _videoProcessor = null;
                _frameSurface?.Dispose();
                _frameSurface = null;
                return;
            }

            var fsrReady = _fsrEasuEffect != null && _fsrRcasEffect != null;
            var bicubicReady = _upscaleEffect != null && _sharpenEffect != null;
            var shaderMode =
                !_shaderPathBroken && fsrReady ? "FSR 1.0 (EASU+RCAS)" :
                !_shaderPathBroken && bicubicReady ? "бикубический + резкость" : null;

            var (scale, scaleX, scaleY) = ComputeScale(frameW, frameH, w, h);
            var dstW = Math.Max(1, (int)MathF.Round(frameW * scaleX));
            var dstH = Math.Max(1, (int)MathF.Round(frameH * scaleY));
            var offsetX = (w - dstW) / 2;
            var offsetY = (h - dstH) / 2;

            if (_videoProcessor is not null && shaderMode is not null)
            {
                if (_videoProcessor.TryRender(_canvasDevice!, swapChain, _frameSurface,
                        frameW, frameH, w, h, dstW, dstH, offsetX, offsetY, _nativeDevice))
                {
                    if (!_loggedScaleInfo && _streamWidth > 0)
                    {
                        _loggedScaleInfo = true;
                        _logger.LogInformation(
                            "Рендер-апскейл ({Mode}): поток {SW}x{SH} → окно {W}x{H}, выведено {DW}x{DH} (×{SX:F2};{SY:F2}), {ShaderPath}.",
                            VideoStretchMode, _streamWidth, _streamHeight, w, h, dstW, dstH, scaleX, scaleY,
                            FrameServerVideoProcessor.ModeName);
                    }
                    return;
                }

                // Неудача TryRender: путь отключён внутри — снимаем и
                // видеопроцессор, чтобы приёмник кадра пересоздался в BGRA
                // (Win2D NV12 не читает). Иначе — цикл пересозданий NV12.
                _videoProcessor?.Dispose();
                _videoProcessor = null;
                _frameSurface?.Dispose();
                _frameSurface = null;
                _loggedScaleInfo = false;
                return;
            }

            using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(
                _canvasDevice, _frameSurface);

            if (shaderMode is null)
            {
                DrawDirect(swapChain, bitmap, w, h);
                return;
            }

            var upscale = fsrReady ? _fsrEasuEffect! : _upscaleEffect!;
            var sharpen = fsrReady ? _fsrRcasEffect! : _sharpenEffect!;

            try
            {
                DrawWithShaders(swapChain, bitmap, upscale, sharpen, frameW, frameH, w, h, fsrReady);
            }
            catch (Exception ex)
            {

                _shaderPathBroken = true;
                _logger.LogWarning(ex, "FrameServerRenderer: шейдерный путь отключён, откат на линейный.");
                DrawDirect(swapChain, bitmap, w, h);
            }

            if (!_loggedScaleInfo && _streamWidth > 0)
            {
                _loggedScaleInfo = true;
                _logger.LogInformation(
                    "Рендер-апскейл ({Mode}): поток {SW}x{SH} → окно {W}x{H}, выведено {DW}x{DH} (×{SX:F2};{SY:F2}), {ShaderPath}.",
                    VideoStretchMode, _streamWidth, _streamHeight, w, h, dstW, dstH, scaleX, scaleY, shaderMode);
            }

            var recreated = _recreatedAt;
            if (recreated != null)
            {
                _recreatedAt = null;
                _logger.LogInformation(
                    "FrameServerRenderer: первый кадр после пересоздания свапчейна — через {Ms:F0} мс.",
                    recreated.Elapsed.TotalMilliseconds);
            }
        }

        private void DrawWithShaders(CanvasSwapChain swapChain, CanvasBitmap bitmap,
            PixelShaderEffect upscale, PixelShaderEffect sharpen,
            int frameW, int frameH, int w, int h, bool fsr)
        {

            var (scale, scaleX, scaleY) = ComputeScale(frameW, frameH, w, h);
            var dstW = Math.Max(1, (int)MathF.Round(frameW * scaleX));
            var dstH = Math.Max(1, (int)MathF.Round(frameH * scaleY));
            var offsetX = (w - dstW) / 2;
            var offsetY = (h - dstH) / 2;

            upscale.Source1 = bitmap;
            upscale.Properties["srcSize"] = new Vector2(frameW, frameH);
            upscale.Properties["dstSize"] = new Vector2(dstW, dstH);


            upscale.Source1Mapping = SamplerCoordinateMapping.OneToOne;

            upscale.Source1Interpolation = CanvasImageInterpolation.NearestNeighbor;
            upscale.Source1BorderMode = EffectBorderMode.Hard;

            var stretch = new Transform2DEffect
            {
                Source = upscale,
                TransformMatrix = System.Numerics.Matrix3x2.CreateScale(scaleX, scaleY)
            };

            var dpi = _cachedDpi;
            if (_upscaledTarget is null ||
                _upscaledTarget.SizeInPixels.Width != dstW ||
                _upscaledTarget.SizeInPixels.Height != dstH)
            {
                _upscaledTarget?.Dispose();
                _upscaledTarget = new CanvasRenderTarget(_canvasDevice, dstW, dstH, dpi,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Ignore);
            }

            using (var s1 = _upscaledTarget.CreateDrawingSession())
            {
                s1.DrawImage(stretch, new Vector2(0, 0));
            }

            sharpen.Source1 = _upscaledTarget;
            sharpen.Properties["dstSize"] = new Vector2(dstW, dstH);
            if (fsr)
            {

                sharpen.Properties["sharpness"] = Sharpening;
            }
            else
            {
                sharpen.Properties["sharpening"] = Sharpening;
            }
            sharpen.Source1Mapping = SamplerCoordinateMapping.OneToOne;
            sharpen.Source1Interpolation = CanvasImageInterpolation.NearestNeighbor;
            sharpen.Source1BorderMode = EffectBorderMode.Hard;

            using (var s2 = swapChain.CreateDrawingSession(Colors.Black))
            {
                s2.DrawImage(sharpen, new Vector2(offsetX, offsetY));
            }

            swapChain.Present();
        }

        /// <summary>
        /// Коэффициенты масштабирования по режиму: Uniform — единый
        /// коэффициент вписывания, UniformToFill — единый коэффициент
        /// заполнения (пропорции сохранены, края обрезаются), Fill —
        /// независимые коэффициенты по осям (растяжение).
        /// </summary>
        private (float Scale, float ScaleX, float ScaleY) ComputeScale(
            int frameW, int frameH, int w, int h)
        {
            var sx = (float)w / frameW;
            var sy = (float)h / frameH;
            return VideoStretchMode switch
            {
                Stretch.Fill => (MathF.Max(sx, sy), sx, sy),
                Stretch.UniformToFill => (MathF.Max(sx, sy), MathF.Max(sx, sy), MathF.Max(sx, sy)),
                _ => (MathF.Min(sx, sy), MathF.Min(sx, sy), MathF.Min(sx, sy)),
            };
        }

        private void DrawDirect(CanvasSwapChain swapChain, CanvasBitmap bitmap, int w, int h)
        {


            var bw = (float)bitmap.Size.Width;
            var bh = (float)bitmap.Size.Height;
            var (_, sx, sy) = ComputeScale((int)bw, (int)bh, w, h);
            var dw = bw * sx;
            var dh = bh * sy;
            var x = (w - dw) / 2f;
            var y = (h - dh) / 2f;

            using (var session = swapChain.CreateDrawingSession(Colors.Black))
            {
                session.DrawImage(bitmap, new Rect(x, y, dw, dh),
                    new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), 1f,
                    CanvasImageInterpolation.HighQualityCubic);
            }

            swapChain.Present();
        }
    }
}
