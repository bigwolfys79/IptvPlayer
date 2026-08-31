using System;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Extensions.Logging;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Playback;
using Windows.UI;

namespace IptvPlayer.Services
{
    /// <summary>
    /// Рендер-путь «frame server» (экспериментальный апскейл, фаза 2):
    /// MediaPlayer работает с IsVideoFrameServerEnabled и сам ничего не
    /// рисует. В событии VideoFrameAvailable кадр копируется в BGRA8-
    /// текстуру на НАШЕМ D3D-девайсе (CopyFrameToVideoSurface), а та
    /// рисуется в CanvasSwapChainPanel через Win2D.
    ///
    /// Разрешение вывода задаётся рендером (размер панели), а не дескриптором
    /// потока — поэтому здесь доступно реальное апскейлирование, недоступное
    /// фильтрам FFmpeg внутри MediaStreamSource (см. VideoUpscaler.cs).
    ///
    /// Девайс один и тот же для текстуры-приёмника и для Win2D (Win2D требует
    /// совпадения девайса при CreateFromDirect3D11Surface) — см.
    /// Direct3DInterop. Кадры приходят на фоновом потоке: рисуем там же под
    /// атомарным флагом (кадр пропускается, если предыдущий ещё рисуется),
    /// UI-поток нужен только для создания/ресайза свапчейна.
    ///
    /// Текущая интерполяция — HighQualityCubic. Дальше: собственные HLSL-
    /// шейдеры (CanvasPixelShader), например FSR/Anime4K-подобные.
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
        private MediaPlayer? _player;
        private CanvasSwapChainPanel? _panel;

        public FrameServerRenderer(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Подключает рендер к панели и играющему плееру. UI-поток.
        /// </summary>
        public void Attach(CanvasSwapChainPanel panel, MediaPlayer player)
        {
            Detach();

            if (_canvasDevice is null)
            {
                (_nativeDevice, _d3dDevice) = Direct3DInterop.CreateDevice();
                _canvasDevice = CanvasDevice.CreateFromDirect3D11Device(_d3dDevice);
            }

            _panel = panel;
            _player = player;
            RecreateSwapChain();
            panel.SizeChanged += Panel_SizeChanged;
            player.VideoFrameAvailable += Player_VideoFrameAvailable;
            _logger.LogInformation("FrameServerRenderer: подключён к плееру.");
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
                _logger.LogWarning(ex, "FrameServerRenderer: ошибка рендера кадра.");
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

            // Поверхность-приёмник пересоздаём при смене размера окна.
            if (_frameSurface is null || _frameWidth != w || _frameHeight != h)
            {
                _frameSurface?.Dispose();
                _frameSurface = Direct3DInterop.CreateBgraSurface(_nativeDevice, w, h);
                _frameWidth = w;
                _frameHeight = h;
                // Сообщаем медиа-движку размер приёмника.
                sender.SetSurfaceSize(new Size(w, h));
            }

            // Кадр: медиа-движок → наша текстура (GPU).
            sender.CopyFrameToVideoSurface(_frameSurface);

            // Текстура → свапчейн той же машиной: девайс общий, так что
            // CreateFromDirect3D11Surface обёртку не копирует.
            using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(
                _canvasDevice, _frameSurface);

            using (var drawSession = swapChain.CreateDrawingSession(Microsoft.UI.Colors.Black))
            {
                // Кадр уже в размере свапчейна — рисуем 1:1, Uniform-вписывание
                // сделано самим размером приёмника (SetSurfaceSize + копия).
                drawSession.DrawImage(bitmap, new Rect(0, 0, w, h),
                    new Rect(0, 0, bitmap.Size.Width, bitmap.Size.Height), 1f,
                    CanvasImageInterpolation.NearestNeighbor);
            }

            swapChain.Present();
        }
    }
}
