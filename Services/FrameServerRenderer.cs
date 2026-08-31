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

namespace IptvPlayer.Services
{
    /// <summary>
    /// Рендер-путь «frame server» (экспериментальный апскейл, фаза 2):
    /// MediaPlayer работает с IsVideoFrameServerEnabled и сам ничего не
    /// рисует. Кадр копируется в текстуру РАЗРЕШЕНИЯ ПОТОКА
    /// (CopyFrameToVideoSurface), а в окно рисуется двумя HLSL-проходами:
    ///   1) Upscale.cso — Catmull-Rom бикубический апскейл под размер окна;
    ///   2) Sharpen.cso — адаптивная резкость (упрощённый CAS).
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

        // Текстура-приёмник кадра в разрешении потока.
        private IDirect3DSurface? _frameSurface;
        private int _frameWidth, _frameHeight;

        // Промежуточная цель прохода 1 (размер окна).
        private CanvasRenderTarget? _upscaledTarget;

        private PixelShaderEffect? _upscaleEffect;
        private PixelShaderEffect? _sharpenEffect;
        private bool _shaderPathBroken;

        private MediaPlayer? _player;
        private CanvasSwapChainPanel? _panel;
        private int _errorCount;
        private int _streamWidth, _streamHeight;
        private bool _loggedScaleInfo;

        // Сила резкости прохода 2 (0..1). Позже — в настройки.
        private const float Sharpening = 0.35f;

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
                _upscaleEffect = LoadEffect("Upscale.cso");
                _sharpenEffect = LoadEffect("Sharpen.cso");
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
                _upscaleEffect != null && _sharpenEffect != null ? "вкл" : "откат на линейный");
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

        public void Dispose() => Detach();

        private void Panel_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
            if (sender is CanvasSwapChainPanel panel && panel.DispatcherQueue.HasThreadAccess)
            {
                RecreateSwapChain();
            }
        }

        private void RecreateSwapChain()
        {
            if (_panel is null || _canvasDevice is null)
            {
                return;
            }

            try
            {
                var w = (float)Math.Max(1, _panel.ActualWidth);
                var h = (float)Math.Max(1, _panel.ActualHeight);
                var dpi = _panel.XamlRoot?.RasterizationScale ?? 96f;
                _panel.SwapChain = new CanvasSwapChain(_canvasDevice, w, h, 60f,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, CanvasAlphaMode.Ignore);
                _upscaledTarget = null; // пересоздаётся в Render под новый размер
                _logger.LogInformation(
                    "FrameServerRenderer: свапчейн {W}x{H} (dpi {Dpi:F0}).", w, h, dpi);
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
                // Кадр уже рисуется — пропускаем: рисование медленнее доставки
                // кадров, без пропуска очередь росла бы бесконечно.
                return;
            }

            try
            {
                Render(sender);
            }
            catch (Exception ex)
            {
                // Без лимита ошибка рендера сыпалась бы 30-60 раз в секунду.
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

            // Приёмник кадра — в разрешении потока: масштабирование делаем
            // сами шейдером, а не билинейно в CopyFrameToVideoSurface.
            var frameW = _streamWidth > 0 ? _streamWidth : w;
            var frameH = _streamHeight > 0 ? _streamHeight : h;
            if (_frameSurface is null || _frameWidth != frameW || _frameHeight != frameH)
            {
                _frameSurface?.Dispose();
                _frameSurface = Direct3DInterop.CreateBgraSurface(_nativeDevice, frameW, frameH);
                _frameWidth = frameW;
                _frameHeight = frameH;
                // Сообщаем медиа-движку размер приёмника.
                sender.SetSurfaceSize(new Size(frameW, frameH));
            }

            // Кадр: медиа-движок → наша текстура (GPU, 1:1 без масштаба).
            sender.CopyFrameToVideoSurface(_frameSurface);

            using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(
                _canvasDevice, _frameSurface);

            var upscale = _upscaleEffect;
            var sharpen = _sharpenEffect;
            if (upscale == null || sharpen == null || _shaderPathBroken)
            {
                DrawDirect(swapChain, bitmap, w, h);
                return;
            }

            try
            {
                DrawWithShaders(swapChain, bitmap, upscale, sharpen, frameW, frameH, w, h);
            }
            catch (Exception ex)
            {
                // Однократный откат: шейдерный путь не работает на этом GPU.
                _shaderPathBroken = true;
                _logger.LogWarning(ex, "FrameServerRenderer: шейдерный путь отключён, откат на линейный.");
                DrawDirect(swapChain, bitmap, w, h);
            }

            if (!_loggedScaleInfo && _streamWidth > 0)
            {
                _loggedScaleInfo = true;
                var scale = (double)w / _streamWidth;
                _logger.LogInformation(
                    "Рендер-апскейл: поток {SW}x{SH} → окно {W}x{H} (×{Scale:F2}), бикубический + резкость.",
                    _streamWidth, _streamHeight, w, h, scale);
            }
        }

        private void DrawWithShaders(CanvasSwapChain swapChain, CanvasBitmap bitmap,
            PixelShaderEffect upscale, PixelShaderEffect sharpen,
            int frameW, int frameH, int w, int h)
        {
            // Проход 1: бикубический апскейл в промежуточную цель.
            if (_upscaledTarget is null ||
                _upscaledTarget.SizeInPixels.Width != w ||
                _upscaledTarget.SizeInPixels.Height != h)
            {
                _upscaledTarget?.Dispose();
                _upscaledTarget = new CanvasRenderTarget(_canvasDevice, w, h, 96f,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Ignore);
            }

            upscale.Source1 = bitmap;
            upscale.Properties["srcSize"] = new Vector2(frameW, frameH);
            upscale.Properties["dstSize"] = new Vector2(w, h);
            // OneToOne: uv входа считается по обратной трансформации сцены —
            // под Transform2D uv покрывает 0..1 независимо от масштаба.
            upscale.Source1Mapping = SamplerCoordinateMapping.OneToOne;
            // Свою фильтрацию делает шейдер — сэмплер точечный, клип по краю.
            upscale.Source1Interpolation = CanvasImageInterpolation.NearestNeighbor;
            upscale.Source1BorderMode = EffectBorderMode.Hard;

            // Выход шейдера по умолчанию имеет размер входа; растягиваем до
            // размера окна преобразованием поверх эффекта.
            var stretch = new Transform2DEffect
            {
                Source = upscale,
                TransformMatrix = System.Numerics.Matrix3x2.CreateScale(
                    (float)w / frameW, (float)h / frameH)
            };

            // Проход 1: бикубический апскейл в промежуточную цель.
            if (_upscaledTarget is null ||
                _upscaledTarget.SizeInPixels.Width != w ||
                _upscaledTarget.SizeInPixels.Height != h)
            {
                _upscaledTarget?.Dispose();
                _upscaledTarget = new CanvasRenderTarget(_canvasDevice, w, h, 96f,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Ignore);
            }

            using (var s1 = _upscaledTarget.CreateDrawingSession())
            {
                s1.DrawImage(stretch, new Vector2(0, 0));
            }

            // Проход 2: адаптивная резкость → свапчейн (вход и выход одного
            // размера, 1:1 — трансформация не нужна).
            sharpen.Source1 = _upscaledTarget;
            sharpen.Properties["dstSize"] = new Vector2(w, h);
            sharpen.Properties["sharpening"] = Sharpening;
            sharpen.Source1Mapping = SamplerCoordinateMapping.OneToOne;
            sharpen.Source1Interpolation = CanvasImageInterpolation.NearestNeighbor;
            sharpen.Source1BorderMode = EffectBorderMode.Hard;

            using (var s2 = swapChain.CreateDrawingSession(Colors.Black))
            {
                s2.DrawImage(sharpen, new Vector2(0, 0));
            }

            swapChain.Present();
        }

        private void DrawDirect(CanvasSwapChain swapChain, CanvasBitmap bitmap, int w, int h)
        {
            using (var session = swapChain.CreateDrawingSession(Colors.Black))
            {
                session.DrawImage(bitmap, new Rect(0, 0, w, h),
                    new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), 1f,
                    CanvasImageInterpolation.HighQualityCubic);
            }

            swapChain.Present();
        }
    }
}
