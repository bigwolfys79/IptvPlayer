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
/// Горячие клавиши и ввод номера канала цифрами.
/// Вынесено из MainPage.xaml.cs (MVVM-этап 3: разбиение code-behind по зонам).
/// </summary>
public sealed partial class MainPage
{
    // ===================== Горячие клавиши =====================

    // Ввод номера канала цифрами, как в телевизоре: до 4 цифр, коммит по
    // Enter или таймауту 3 с, Backspace стирает последнюю, Esc отменяет.
    private string _channelNumberInput = string.Empty;
    private readonly DispatcherTimer _channelNumberInputTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    // Подписка горячих клавиш на корень XamlRoot (см. конструктор) — один раз.
    private bool _hotkeysAttached;

    /// <summary>
    /// Единая точка обработки клавиатуры. Повешена на корневой элемент XamlRoot
    /// (см. конструктор): туннелирующее событие перехватывает клавиши раньше
    /// кнопок (пробел не «нажимает» сфокусированную кнопку), срабатывает и
    /// когда фокуса внутри страницы нет, но не мешает открытым ContentDialog
    /// (см. проверку ниже). Пары: Space — пауза архива; ↑/↓ и PgUp/PgDn —
    /// соседний канал; M — без звука; F/F11 — полный экран; Esc — выход из
    /// него; Ctrl+F — поиск; Ctrl+J — статистика; цифры — ввод номера канала.
    /// Буквы приходят как VK-коды латиницы независимо от раскладки («М» на
    /// русской клавиатуре — тот же VirtualKey.M; проверено таблицей раскладки).
    /// </summary>
    private void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Открытый ContentDialog (настройки и др.) живёт в слое того же корня —
        // его клавиши (стрелки по радиокнопкам, ввод в поля) не должны
        // запускать горячие клавиши приложения.
        if (IsFocusedWithin(element => element is ContentDialog))
        {
            return;
        }

        // Ctrl-комбинации работают и когда фокус уже в поле ввода.
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(CoreVirtualKeyStates.Down))
        {
            switch (e.Key)
            {
                case VirtualKey.F:
                    // Фокус в поиск (в fullscreen поиска нет — панель свёрнута).
                    if (!_isFullScreen)
                    {
                        ChannelSearchBox.Focus(FocusState.Keyboard);
                        SelectAllInSearchBox();
                    }
                    e.Handled = true;
                    return;

                case VirtualKey.J:
                    // Оверлей статистики потока — как в VLC.
                    ToggleStatsOverlay();
                    e.Handled = true;
                    return;

                case VirtualKey.M:
                    // Мини-плеер: компактное окно поверх всех окон.
                    ToggleMiniPlayer();
                    e.Handled = true;
                    return;
            }
        }

        // Набор текста (поиск) — остальные клавиши уходят в поле ввода.
        if (IsTextInputFocused())
        {
            return;
        }

        // Идёт ввод номера канала: цифры, Enter, Backspace и Esc обслуживают
        // его в первую очередь.
        if (_channelNumberInput.Length > 0)
        {
            switch (e.Key)
            {
                case VirtualKey.Enter:
                    CommitChannelNumber();
                    e.Handled = true;
                    return;
                case VirtualKey.Back:
                    _channelNumberInput = _channelNumberInput[..^1];
                    if (_channelNumberInput.Length == 0)
                    {
                        CancelChannelNumber();
                    }
                    else
                    {
                        UpdateChannelNumberOverlay();
                    }
                    e.Handled = true;
                    return;
                case VirtualKey.Escape:
                    CancelChannelNumber();
                    e.Handled = true;
                    return;
            }
        }

        var digit = DigitFromKey(e.Key);
        if (digit >= 0)
        {
            HandleChannelNumberDigit(digit);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Back:
                // «Предыдущий канал» — как кнопка «назад» пульта. Ввод номера
                // канала и текстовые поля перехватили Backspace выше.
                ViewModel.GoToPreviousChannelCommand.Execute(null);
                e.Handled = true;
                break;

            case VirtualKey.Space:
                // Пауза живого эфира намеренно не поддерживается — как и кнопка
                // паузы в панелях. Пробел работает на архиве и на VOD портала
                // (VOD перематывается/паузится самим движком без рестарта).
                if ((Player.IsArchivePlaying || Player.IsVodPlaying) && Player.Player != null)
                {
                    ViewModel.ToggleArchivePauseCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case VirtualKey.M:
                Player.ToggleMute();
                e.Handled = true;
                break;

            case VirtualKey.V:
                // Режим отображения: вписать → растянуть → обрезать → …
                CycleVideoStretch();
                e.Handled = true;
                break;

            case VirtualKey.F or VirtualKey.F11:
                SetFullScreenMode(!_isFullScreen);
                e.Handled = true;
                break;

            case VirtualKey.Escape:
                // Esc в fullscreen сначала закрывает открытое EPG-окно
                // (пока курсор над ним, оверлей с кнопками не показывается),
                // повторное нажатие — выход из полноэкранного режима.
                if (ViewModel.IsEpgVisible)
                {
                    ViewModel.IsEpgVisible = false;
                    ApplyEpgVisibility();
                    e.Handled = true;
                }
                else if (_isFullScreen)
                {
                    SetFullScreenMode(false);
                    e.Handled = true;
                }
                break;

            case VirtualKey.PageUp or VirtualKey.Up
                or VirtualKey.PageDown or VirtualKey.Down:
                // Стрелки и PgUp/PgDn заняты у элементов, которые ими управляются
                // (список каналов/передач, слайдер перемотки, комбобокс групп) —
                // там каналы клавишами не переключаем.
                if (IsNavigationControlFocused())
                {
                    return;
                }
                ZapToAdjacentChannel(
                    e.Key is VirtualKey.PageDown or VirtualKey.Down ? +1 : -1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Переключение на соседний канал текущего (отфильтрованного) списка с
    /// заходом по кругу. В fullscreen заодно показывается полноэкранный оверлей
    /// — название канала живёт в его шапке, без оверлея переключение вслепую.
    /// </summary>
    private void ZapToAdjacentChannel(int offset)
    {
        var channels = ViewModel.DisplayedChannels;
        if (channels.Count == 0)
        {
            return;
        }

        var index = ViewModel.SelectedChannel is { } current ? channels.IndexOf(current) : -1;
        if (index < 0)
        {
            // Выбранный канал вне фильтра: шаг вперёд даёт первый, назад —
            // последний (телевизорная семантика обхода по кругу).
            index = offset >= 0 ? -1 : 0;
        }
        var next = (index + offset + channels.Count) % channels.Count;

        var channel = channels[next];
        ViewModel.SelectAndPlayChannelCommand.Execute(channel);
        ChannelsListView.ScrollIntoView(channel);

        if (_isFullScreen)
        {
            OverlayChannelsListView.ScrollIntoView(channel);
            ShowFullScreenOverlay();
            _overlayHideTimer.Stop();
            _overlayHideTimer.Start();
        }
    }

    // ===================== Ввод номера канала =====================

    private void HandleChannelNumberDigit(int digit)
    {
        if (_channelNumberInput.Length >= 4)
        {
            return;
        }

        _channelNumberInput += digit.ToString();
        UpdateChannelNumberOverlay();

        _channelNumberInputTimer.Stop();
        _channelNumberInputTimer.Start();
    }

    /// <summary>
    /// Обновляет оверлей ввода: крупные цифры + имя канала, который разрешается
    /// этим номером (по мере набора), либо диапазон «1..N», если номера ещё
    /// нет в списке.
    /// </summary>
    private void UpdateChannelNumberOverlay()
    {
        var channels = ViewModel.DisplayedChannels;
        ChannelNumberText.Text = _channelNumberInput;

        ChannelNumberName.Text =
            int.TryParse(_channelNumberInput, out var n) && n >= 1 && n <= channels.Count
                ? channels[n - 1].Name
                : string.Format(L.T("Iz_0"), channels.Count, channels.Count);

        ChannelNumberOverlay.Visibility = Visibility.Visible;
    }

    private void CommitChannelNumber()
    {
        _channelNumberInputTimer.Stop();
        var input = _channelNumberInput;
        _channelNumberInput = string.Empty;
        ChannelNumberOverlay.Visibility = Visibility.Collapsed;

        if (int.TryParse(input, out var n) &&
            n >= 1 && n <= ViewModel.DisplayedChannels.Count)
        {
            var channel = ViewModel.DisplayedChannels[n - 1];
            ViewModel.SelectAndPlayChannelCommand.Execute(channel);
            ChannelsListView.ScrollIntoView(channel);

            if (_isFullScreen)
            {
                OverlayChannelsListView.ScrollIntoView(channel);
            }
        }
    }

    private void CancelChannelNumber()
    {
        _channelNumberInputTimer.Stop();
        _channelNumberInput = string.Empty;
        ChannelNumberOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>Цифра верхнего ряда или numpad (в любой раскладке), иначе −1.</summary>
    private static int DigitFromKey(VirtualKey key)
    {
        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
        {
            return (int)key - (int)VirtualKey.Number0;
        }
        if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9)
        {
            return (int)key - (int)VirtualKey.NumberPad0;
        }
        return -1;
    }

    // ===================== Фокус и границы перехвата =====================

    /// <summary>Фокус в поле текстового ввода (поиск) — клавиши идут туда.</summary>
    private bool IsTextInputFocused() =>
        IsFocusedWithin(element => element is TextBox or AutoSuggestBox);

    /// <summary>
    /// Фокус на элементе, которым управляют стрелки/PgUp/PgDn (списки, слайдер
    /// перемотки, комбобокс) — навигационные клавиши оставляем ему.
    /// </summary>
    private bool IsNavigationControlFocused() =>
        IsFocusedWithin(element =>
            element is ListView or Slider or ComboBox or TextBox or AutoSuggestBox);

    private bool IsFocusedWithin(Func<DependencyObject, bool> match) =>
        FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused &&
        AnyAncestorOrSelf(focused, match);

    /// <summary>Обход цепочки предков visual-дерева (включая сам элемент).</summary>
    private static bool AnyAncestorOrSelf(DependencyObject element, Func<DependencyObject, bool> match)
    {
        while (element != null)
        {
            if (match(element))
            {
                return true;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    /// <summary>Выделяет текст поиска, чтобы новый ввод заменял прежний запрос.</summary>
    private void SelectAllInSearchBox()
    {
        if (FindDescendant<TextBox>(ChannelSearchBox) is { } box)
        {
            box.SelectAll();
        }
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T matched)
            {
                return matched;
            }
            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }

}
