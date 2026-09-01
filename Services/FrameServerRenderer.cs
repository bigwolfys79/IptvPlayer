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

        // Текстура-приёмник кадра в разрешении потока.
        private IDirect3DSurface? _frameSurface;
        private int _frameWidth, _frameHeight;

        // Промежуточная цель прохода 1 (размер окна).
        private CanvasRenderTarget? _upscaledTarget;

        private PixelShaderEffect? _upscaleEffect;
        private PixelShaderEffect? _sharpenEffect;
        // FSR 1.0: EASU (апскейл) + RCAS (резкость) — основной шейдерный
        // путь; при их отсутствии/ошибке — бикубик + CAS ниже.
        private PixelShaderEffect? _fsrEasuEffect;
        private PixelShaderEffect? _fsrRcasEffect;
        private bool _shaderPathBroken;

        private MediaPlayer? _player;
        private CanvasSwapChainPanel? _panel;
        private int _errorCount;
        private int _streamWidth, _streamHeight;
        private bool _loggedScaleInfo;
        private float _cachedDpi = 96f;

        // Сила резкости прохода 2 (0..1). Позже — в настройки.
        // Для RCAS 0.5 — заметный, 0.8 — агрессивный (почти максимум).
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

        public void Dispose() => Detach();

        private void Panel_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        {
            if (sender is CanvasSwapChainPanel panel && panel.DispatcherQueue.HasThreadAccess)
            {
                RecreateSwapChain();
            }
        }

        // Без дебаунса: замеры показали, что создание свапчейна стоит 1–9 мс
        // (даже 2560x1438), поэтому двойное создание при двух SizeChanged
        // подряд дешевле, чем любая задержка пересоздания. Дебаунс 50 мс
        // проверялся и убран: он добавлял 50 мс к ощущаемому развороту.
        // Момент пересоздания свапчейна: для лога «первый кадр после
        // пересоздания» — отделяет стоимость D3D-создания от ожидания
        // первого кадра медиа-движка при объективной проверке разворота.
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
                _upscaledTarget = null; // пересоздаётся в Render под новый размер
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

            // Приоритет путей: FSR 1.0 → бикубик + CAS → линейный.
            var fsrReady = _fsrEasuEffect != null && _fsrRcasEffect != null;
            var bicubicReady = _upscaleEffect != null && _sharpenEffect != null;
            var shaderMode =
                !_shaderPathBroken && fsrReady ? "FSR 1.0 (EASU+RCAS)" :
                !_shaderPathBroken && bicubicReady ? "бикубический + резкость" : null;
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
                // Однократный откат: шейдерный путь не работает на этом GPU.
                _shaderPathBroken = true;
                _logger.LogWarning(ex, "FrameServerRenderer: шейдерный путь отключён, откат на линейный.");
                DrawDirect(swapChain, bitmap, w, h);
            }

            if (!_loggedScaleInfo && _streamWidth > 0)
            {
                _loggedScaleInfo = true;
                var (_, sx, sy) = ComputeScale(_streamWidth, _streamHeight, w, h);
                var dstW = Math.Max(1, (int)Math.Round(_streamWidth * sx));
                var dstH = Math.Max(1, (int)Math.Round(_streamHeight * sy));
                _logger.LogInformation(
                    "Рендер-апскейл ({Mode}): поток {SW}x{SH} → окно {W}x{H}, выведено {DW}x{DH} (×{SX:F2};{SY:F2}), {ShaderPath}.",
                    VideoStretchMode, _streamWidth, _streamHeight, w, h, dstW, dstH, sx, sy, shaderMode);
            }

            // Первый кадр после пересоздания свапчейна: отделяет стоимость
            // D3D-создания от ожидания кадра медиа-движка (диагностика
            // ощущаемой задержки разворота в fullscreen).
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
            // Коэффициент масштабирования по режиму VideoStretchMode:
            // Uniform — вписать (min), UniformToFill — заполнить (max, края
            // обрезаются), Fill — по каждой оси своя пропорция.
            var (scale, scaleX, scaleY) = ComputeScale(frameW, frameH, w, h);
            var dstW = Math.Max(1, (int)MathF.Round(frameW * scaleX));
            var dstH = Math.Max(1, (int)MathF.Round(frameH * scaleY));
            var offsetX = (w - dstW) / 2;
            var offsetY = (h - dstH) / 2;

            upscale.Source1 = bitmap;
            upscale.Properties["srcSize"] = new Vector2(frameW, frameH);
            upscale.Properties["dstSize"] = new Vector2(dstW, dstH);
            // OneToOne: uv входа считается по обратной трансформации сцены —
            // под Transform2D uv покрывает 0..1 независимо от масштаба.
            upscale.Source1Mapping = SamplerCoordinateMapping.OneToOne;
            // Свою фильтрацию делает шейдер — сэмплер точечный, клип по краю.
            upscale.Source1Interpolation = CanvasImageInterpolation.NearestNeighbor;
            upscale.Source1BorderMode = EffectBorderMode.Hard;

            // Выход шейдера по умолчанию имеет размер входа; масштабируем его
            // ЕДИНЫМ коэффициентом до размера вписанного кадра (без растяжения).
            var stretch = new Transform2DEffect
            {
                Source = upscale,
                TransformMatrix = System.Numerics.Matrix3x2.CreateScale(scaleX, scaleY)
            };

            // Проход 1: бикубический апскейл в промежуточную цель размером
            // вписанного кадра (чёрные поля добавляются на шаге вывода).
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

            // Проход 2: адаптивная резкость (вход и выход одного размера,
            // 1:1 — трансформация не нужна); кадр выводится по центру окна,
            // свободное место остаётся чёрным (letterbox/pillarbox).
            sharpen.Source1 = _upscaledTarget;
            sharpen.Properties["dstSize"] = new Vector2(dstW, dstH);
            if (fsr)
            {
                // RCAS: 0..1 → стопы затухания exp2(-lerp(8..0)).
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
            // Откат без шейдеров: та же логика масштабирования по режиму,
            // чтобы пропорции совпадали с шейдерным путём.
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
