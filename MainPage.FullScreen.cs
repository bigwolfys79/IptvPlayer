using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.Controls;
using IptvPlayer.ViewModels;
using Windows.System;
using Windows.UI.Core;
// Windows.Media.Playback.MediaPlayer конфликтует по имени с x:Name="MediaPlayer"
// (MediaPlayerElement) в разметке, поэтому в коде тип всегда указывается
// с полным неймспейсом: Windows.Media.Playback.MediaPlayer.

namespace IptvPlayer;

/// <summary>
/// Полноэкранный режим и показ/скрытие оверлея по движению мыши.
/// Вынесено из MainPage.xaml.cs (MVVM-этап 3: разбиение code-behind по зонам).
/// </summary>
public sealed partial class MainPage
{
    /// <summary>
    /// Включает/выключает полноэкранный режим: переключает настоящий OS-уровневый
    /// presenter окна (без рамки, без заголовка — MainWindow.SetOsFullScreen) и
    /// сворачивает боковые панели страницы. В fullscreen список каналов, кнопки
    /// плеера, вызов EPG и кнопка выхода доступны через автоскрывающийся оверлей
    /// (см. RootGrid_PointerMoved / ShowFullScreenOverlay / HideFullScreenOverlay).
    /// </summary>
    private void SetFullScreenMode(bool enable)
    {
        _isFullScreen = enable;
        Serilog.Log.Information("FullScreen: {Action}", enable ? "вход в полноэкранный режим" : "выход из полноэкранного режима");

        // Диагностика отставания видео при смене фулскрина: замеряем шаги до
        // момента, когда панель видео получит новый размер (в лог рендерера
        // придёт «свапчейн ...»). Временно.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var swTotal = System.Diagnostics.Stopwatch.StartNew();

        // Presenter переключается первым и в отеле от остальной логики:
        // если что-то ниже бросит исключение, окно всё равно развернётся.
        MainWindow.Instance?.SetOsFullScreen(enable);
        Serilog.Log.Information("FullScreen-DIAG: presenter {Ms:F0} мс", sw.Elapsed.TotalMilliseconds);

        // Смена presenter'а иногда оставляет видео-остров со смещённой
        // компоновкой (видео рисуется не там, где элемент): пересобираем
        // компоновку плеера принудительно. MediaPlayer один и тот же —
        // воспроизведение не прерывается, только мгновенная перерисовка.
        ForceVideoRelayout();

        sw.Restart();
        if (enable)
        {
            // Запоминаем, была ли EPG открыта, чтобы вернуть как было при выходе.
            _wasEpgVisibleBeforeFullScreen = ViewModel.IsEpgVisible;
            ViewModel.IsEpgVisible = false;
            ApplyEpgVisibility();

            // MinWidth на колонке иначе перебивает Width=0 ниже — колонка
            // физически не может стать уже минимума, пока не снимем
            // ограничение, и левая панель остаётся видна. Перед сворачиванием
            // запоминаем текущую ширину (в т.ч. выбранную сплиттером), чтобы
            // вернуть её при выходе из fullscreen.
            if (ChannelListColumn.ActualWidth > 0)
            {
                _channelListExpandedWidth = ChannelListColumn.ActualWidth;
            }
            ChannelListColumn.MinWidth = 0;
            ChannelListColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);

            // Оконный оверлей поверх видео больше не нужен — в fullscreen
            // все органы управления в полноэкранном оверлее.
            HideWindowedVideoOverlay(immediate: true);
            WindowedTopOverlay.Visibility = Visibility.Collapsed;

            // VideoAreaBorder в оконном режиме держит Padding=12 вокруг
            // видео (декоративная рамка) — в fullscreen он давал полосы
            // ~3 мм по краям экрана. Убираем, при выходе возвращаем.
            VideoAreaBorder.Padding = new Thickness(0);

            // Оверлей статистики уезжает правее списка каналов (320 px) и
            // НИЖЕ шапки с названием канала — раньше висел поверх неё.
            StatsOverlay.Margin = new Thickness(344, 100, 0, 0);

            // Слайдеры громкости обоих оверлеев показывают текущую громкость.
            SyncVolumeSliders(Player.Player?.Volume ?? Player.LastUserVolume ?? 1.0);

            Serilog.Log.Information("FullScreen-DIAG: колонки/оверлеи {Ms:F0} мс", sw.Elapsed.TotalMilliseconds);

            // Сначала показываем оверлей. Группированный источник для оверлейного
            // списка пересобираем ЗДЕСЬ: в оконном режиме RefreshOverlayChannelGroups
            // по FilterChanged пропускается при скрытом оверлее (оптимизация
            // больших каталогов) — при входе в fullscreen нужен свежий.
            _lastOverlayPointerPosition = new Windows.Foundation.Point(-1, -1);
            ShowFullScreenOverlay();
            sw.Restart();
            RefreshOverlayChannelGroups();
            Serilog.Log.Information("FullScreen-DIAG: RefreshOverlayChannelGroups {Ms:F0} мс", sw.Elapsed.TotalMilliseconds);

            // БИСЕКЦИЯ, шаг 2: входной нудж включён (проверяем связку).
            _ = NudgePointerDelayedAsync();

            _ = ScrollOverlayChannelIntoViewAsync();

            _overlayHideTimer.Stop();
            _overlayHideTimer.Start();
        }
        else
        {
            ChannelListColumn.MinWidth = 280;
            ChannelListColumn.Width = new GridLength(_channelListExpandedWidth);
            SplitterColumn.Width = GridLength.Auto;
            ViewModel.IsEpgVisible = _wasEpgVisibleBeforeFullScreen;
            ApplyEpgVisibility();

            // Возврат декоративной рамки вокруг видео (убрана в fullscreen).
            VideoAreaBorder.Padding = new Thickness(12);

            StatsOverlay.Margin = new Thickness(12, 60, 0, 0);

            _overlayHideTimer.Stop();
            HideFullScreenOverlay(immediate: true);
            _lastWindowedOverlayPointerPosition = new Windows.Foundation.Point(-1, -1);
        }

        Serilog.Log.Information("FullScreen-DIAG: итого {Ms:F0} мс", swTotal.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Принудительная пересборка компоновки видео-острова: мгновенно
    /// скрываем и показываем MediaPlayerElement. Лечит смещение видео после
    /// смены presenter'а (окно fullscreen ↔ оконное), когда DComp-остров
    /// продолжал рисовать по старым координатам.
    /// </summary>
    private void ForceVideoRelayout()
    {
        MediaPlayer.Visibility = Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(() => MediaPlayer.Visibility = Visibility.Visible);
    }

    /// <summary>
    /// Движение мыши в fullscreen-режиме показывает оверлей (список каналов,
    /// кнопки плеера, EPG, выход) и сбрасывает таймер автоскрытия. Вне
    /// fullscreen-режима не делает ничего.
    /// </summary>
    // Последняя обработанная позиция указателя в fullscreen-режиме. Нужна, чтобы
    // отличать настоящее движение мыши от "синтетических" PointerMoved, которые
    // WinUI генерирует с той же самой координатой, когда под неподвижным курсором
    // появляется/исчезает элемент (в нашем случае — сам FullScreenOverlay). Без
    // этой проверки показ/скрытие оверлея зацикливались сами на себя.
    private Windows.Foundation.Point _lastOverlayPointerPosition = new(-1, -1);

    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isFullScreen || _suppressOverlayWake)
        {
            return;
        }
        var position = e.GetCurrentPoint(RootGrid).Position;
        // <= 1 px: нулевые «синтетические» PointerMoved от появления/исчезания
        // элементов под курсором плюс наш собственный нудж 1 px из
        // HideCursorOverVideo (применение ProtectedCursor требует события
        // указателя) не должны будить оверлей и возвращать курсор.
        if (Math.Abs(position.X - _lastOverlayPointerPosition.X) <= 1 &&
            Math.Abs(position.Y - _lastOverlayPointerPosition.Y) <= 1)
        {
            return;
        }
        _lastOverlayPointerPosition = position;

        // EPG-панель видна — полноэкранный оверлей не показываем
        // независимо от позиции курсора. Скрим (EpgScrimBorder) покрывает
        // весь экран, клик по нему закрывает EPG.
        if (EpgPanelBorder.Visibility == Visibility.Visible)
        {
            if (FullScreenOverlay.Visibility == Visibility.Visible)
            {
                HideFullScreenOverlay(immediate: true);
                _overlayHideTimer.Stop();
            }
            return;
        }

        ShowFullScreenOverlay();
        _overlayHideTimer.Stop();
        _overlayHideTimer.Start();
    }

    /// <summary>
    /// Движение мыши над областью видео в ОКОННОМ режиме показывает компактный
    /// оверлей управления (громкость/пауза архива/EPG/fullscreen) и сбрасывает
    /// общий таймер автоскрытия. Защита от "синтетических" PointerMoved — как в
    /// RootGrid_PointerMoved, но координаты относительно области видео.
    /// </summary>
    private void VideoArea_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isFullScreen)
        {
            return;
        }

        var position = e.GetCurrentPoint(VideoAreaBorder).Position;
        if (Math.Abs(position.X - _lastWindowedOverlayPointerPosition.X) < 1 &&
            Math.Abs(position.Y - _lastWindowedOverlayPointerPosition.Y) < 1)
        {
            return;
        }
        _lastWindowedOverlayPointerPosition = position;

        Serilog.Log.Debug("VideoArea move: EpgVisible={Epg} isFullScreen={FS}",
            ViewModel.IsEpgVisible, _isFullScreen);

        ShowWindowedVideoOverlay();
        _overlayHideTimer.Stop();
        _overlayHideTimer.Start();
    }

    /// <summary>
    /// Колесо мыши над областью видео регулирует громкость (шаг 5% на метку
    /// колеса). Показывает соответствующий режиму оверлей, чтобы изменение
    /// было видно на слайдере. Событие всплывает до RootGrid даже из-под
    /// оверлеев, поэтому один обработчик покрывает и оконный, и полноэкранный
    /// режимы; колесо НЕ над видео (список каналов, EPG) игнорируется.
    /// </summary>
    private void RootGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // EPG-оверлей覆盖 весь экран (скрим + панель): колесо над ним
        // прокручивает список передач, а не меняет громкость.
        if (EpgPanelBorder.Visibility == Visibility.Visible)
        {
            Serilog.Log.Debug("Wheel: EPG visible — BLOCKED");
            return;
        }

        var position = e.GetCurrentPoint(VideoAreaBorder).Position;
        if (position.X < 0 || position.Y < 0 ||
            position.X > VideoAreaBorder.ActualWidth ||
            position.Y > VideoAreaBorder.ActualHeight)
        {
            Serilog.Log.Debug("Wheel: outside VideoAreaBorder ({X:F0},{Y:F0}) vs ({W:F0}x{H:F0}) — ignored",
                position.X, position.Y, VideoAreaBorder.ActualWidth, VideoAreaBorder.ActualHeight);
            return;
        }

        var wheel = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
        if (wheel == 0)
        {
            return;
        }

        // При беззвучном режиме колесо считает от нуля: первое деление вверх
        // даёт 5% и снимает mute (как в привычных плеерах).
        var current = Player.IsMuted ? 0.0 : Player.LastUserVolume ?? Player.Player?.Volume ?? 1.0;
        var target = Math.Clamp(current + (wheel > 0 ? 0.05 : -0.05), 0.0, 1.0);
        if (Math.Abs(target - current) < 0.001)
        {
            return;
        }

        Serilog.Log.Debug("Wheel: VOLUME {Cur:F2} -> {Tgt:F2}", current, target);
        OnVolumeSliderChanged(target);

        if (_isFullScreen)
        {
            ShowFullScreenOverlay();
        }
        else
        {
            ShowWindowedVideoOverlay();
        }
        _overlayHideTimer.Stop();
        _overlayHideTimer.Start();

        e.Handled = true;
    }

}
