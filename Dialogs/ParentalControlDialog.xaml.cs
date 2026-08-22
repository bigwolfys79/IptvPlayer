using System;
using System.Linq;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer.Dialogs;

/// <summary>
/// Родительский контроль: скрытие каналов выбранных групп (по умолчанию
/// предлагаются «взрослые» — 18+/xxx/adult/эротик и т.п.). Отключение и
/// правка списка защищены PIN (если он установлен); временная
/// разблокировка — на 15/30/45/60 минут или до перезапуска.
/// </summary>
public sealed partial class ParentalControlDialog : UserControl
{
    private readonly MainPageViewModel _viewModel;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<ParentalControlDialog> _logger;

    private ContentDialog? _hostDialog;

    // Список групп перестраивается в LoadSection; галочки мутируют
    // AppSettings.ParentalControlBlockedGroups напрямую.
    private bool _loadingSection;

    public ParentalControlDialog(
        MainPageViewModel viewModel,
        ISettingsService settingsService,
        ILogger<ParentalControlDialog> logger)
    {
        _viewModel = viewModel;
        _settingsService = settingsService;
        _logger = logger;
        InitializeComponent();
    }

    public async Task ShowAsync(XamlRoot xamlRoot)
    {
        LoadSection();

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = L.T("Родительский контроль", "Parental control"),
            Content = this,
            CloseButtonText = L.T("Закрыть", "Close")
        };
        _hostDialog = dialog;
        await dialog.ShowAsync();
    }

    private AppSettings Settings => _viewModel.AppSettings;

    private void LoadSection()
    {
        _loadingSection = true;
        try
        {
            var locked = ParentalControlService.IsLocked(Settings);

            var blockedCount = Settings.ParentalControlBlockedGroups.Count;
            StatusText.Text = Settings.ParentalControlEnabled
                ? L.T(
                    $"Включён. Групп под PIN: {blockedCount}. " + (locked ? "Запуск их каналов требует PIN." : "Сейчас разрешено без PIN."),
                    $"Enabled. Groups behind PIN: {blockedCount}. " + (locked ? "Starting their channels requires the PIN." : "Currently allowed without PIN."))
                : L.T("Выключен.", "Disabled.");

            // Заблокировано → только секция разблокировки с PIN.
            var needUnlock = Settings.ParentalControlEnabled && locked;
            UnlockPanel.Visibility = needUnlock ? Visibility.Visible : Visibility.Collapsed;
            EditPanel.Visibility = needUnlock ? Visibility.Collapsed : Visibility.Visible;

            UnlockHint.Text = L.T(
                "Введите PIN и выберите, на сколько отключить его запрос при запуске каналов.",
                "Enter the PIN and choose how long to stop asking for it when starting channels.");
            UnlockForeverButton.Content = L.T("До выключения", "Until off");

            EnabledToggle.IsOn = Settings.ParentalControlEnabled;
            EnabledToggle.Header = L.T("Скрывать каналы выбранных групп", "Hide channels of selected groups");
            EnabledToggle.OnContent = L.T("Вкл", "On");
            EnabledToggle.OffContent = L.T("Выкл", "Off");
            EnabledHint.Text = L.T(
                "Каналы остаются в списке, но запуск каналов отмеченных групп требует PIN. Галочки «взрослых» групп (18+, xxx и т.п.) ставятся автоматически; список можно изменить вручную.",
                "Channels stay in the list, but starting channels of the checked groups requires the PIN. \"Adult\" groups (18+, xxx, etc.) are checked automatically; the list can be edited manually.");

            GroupsHeader.Text = L.T("Скрываемые группы", "Groups to hide");
            GroupsHint.Text = L.T(
                "Запуск каналов отмеченных групп запрашивает PIN, пока контроль включён и не разблокирован временно.",
                "Starting channels of the checked groups asks for the PIN while control is enabled and not temporarily unlocked.");
            BuildGroupsList();

            PinHeader.Text = L.T("PIN-код", "PIN code");
            PinHint.Text = L.T(
                string.IsNullOrEmpty(Settings.ParentalControlPinHash)
                    ? "Без PIN отключить контроль и разблокировать группы сможет кто угодно в настройках."
                    : "PIN требуется для разблокировки и отключения. Дети не смогут снять скрытие без него.",
                string.IsNullOrEmpty(Settings.ParentalControlPinHash)
                    ? "Without a PIN, anyone can disable the control in settings."
                    : "The PIN is required to unlock or disable. Children cannot remove hiding without it.");
            NewPinBox.PlaceholderText = L.T("Новый PIN (4+ цифры)", "New PIN (4+ digits)");
            SetPinButton.Content = L.T("Установить", "Set");
            RemovePinButton.Content = L.T("Убрать PIN", "Remove PIN");
            LockNowButton.Content = L.T("Запрашивать PIN сейчас", "Ask for PIN now");
        }
        finally
        {
            _loadingSection = false;
        }
    }

    private void BuildGroupsList()
    {
        GroupsList.Children.Clear();

        var groups = _viewModel.Channels
            .Select(c => c.Group)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // «Взрослые» группы — наверх, их ищут в первую очередь.
        groups = groups
            .OrderByDescending(ParentalControlService.LooksLikeAdultGroup)
            .ToList();

        foreach (var group in groups)
        {
            var groupName = group;
            var check = new CheckBox
            {
                Content = groupName,
                IsChecked = ParentalControlService.IsGroupBlocked(Settings, groupName),
                // Выглядит подсказкой, что группу нашёл автоподбор.
                FontWeight = ParentalControlService.LooksLikeAdultGroup(groupName)
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal
            };
            check.Checked += (s, e) => ToggleGroup(groupName, true);
            check.Unchecked += (s, e) => ToggleGroup(groupName, false);
            GroupsList.Children.Add(check);
        }

        if (GroupsList.Children.Count == 0)
        {
            GroupsList.Children.Add(new TextBlock
            {
                Text = L.T("В плейлисте нет групп.", "The playlist has no groups."),
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
        }
    }

    private async void ToggleGroup(string group, bool blocked)
    {
        if (_loadingSection)
        {
            return;
        }

        if (blocked && !ParentalControlService.IsGroupBlocked(Settings, group))
        {
            Settings.ParentalControlBlockedGroups.Add(group);
        }
        else if (!blocked)
        {
            Settings.ParentalControlBlockedGroups.RemoveAll(
                g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
        }

        await _settingsService.SaveAsync(Settings);
        LoadSection();
    }

    private async void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSection)
        {
            return;
        }

        Settings.ParentalControlEnabled = EnabledToggle.IsOn;
        if (EnabledToggle.IsOn &&
            Settings.ParentalControlBlockedGroups.Count == 0)
        {
            // Автопредложение: отмечаем «взрослые» группы плейлиста.
            var suggested = ParentalControlService.SuggestBlockedGroups(
                _viewModel.Channels.Select(c => c.Group));
            Settings.ParentalControlBlockedGroups = suggested;
        }

        await _settingsService.SaveAsync(Settings);
        LoadSection();
    }

    private async void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        var minutes = int.Parse(((FrameworkElement)sender).Tag.ToString()!);
        await UnlockAsync(minutes);
    }

    private async void UnlockForeverButton_Click(object sender, RoutedEventArgs e)
    {
        await UnlockAsync(null);
    }

    private async Task UnlockAsync(int? minutes)
    {
        if (!ParentalControlService.VerifyPin(Settings, PinBox.Password))
        {
            StatusText.Text = L.T("Неверный PIN.", "Wrong PIN.");
            return;
        }

        ParentalControlService.Unlock(Settings, minutes);
        await _settingsService.SaveAsync(Settings);
        PinBox.Password = string.Empty;
        LoadSection();
    }

    private async void LockNowButton_Click(object sender, RoutedEventArgs e)
    {
        ParentalControlService.Lock(Settings);
        await _settingsService.SaveAsync(Settings);
        LoadSection();
    }

    private async void SetPinButton_Click(object sender, RoutedEventArgs e)
    {
        var pin = NewPinBox.Password;
        if (pin.Length < 4)
        {
            StatusText.Text = L.T("PIN должен быть не короче 4 символов.", "The PIN must be at least 4 characters.");
            return;
        }

        // Смена PIN требует знать старый, если он установлен (секция и так
        // доступна только после разблокировки, но лишний барьер не мешает).
        if (!string.IsNullOrEmpty(Settings.ParentalControlPinHash) &&
            !ParentalControlService.VerifyPin(Settings, PinBox.Password))
        {
            StatusText.Text = L.T("Введите СТАРЫЙ PIN в верхнем поле перед сменой.", "Enter the OLD PIN in the field above before changing.");
            return;
        }

        Settings.ParentalControlPinHash = ParentalControlService.HashPin(pin);
        NewPinBox.Password = string.Empty;
        await _settingsService.SaveAsync(Settings);
        _logger.LogInformation("PIN родительского контроля изменён.");
        StatusText.Text = L.T("PIN установлен.", "PIN set.");
        LoadSection();
    }

    private async void RemovePinButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(Settings.ParentalControlPinHash) &&
            !ParentalControlService.VerifyPin(Settings, PinBox.Password))
        {
            StatusText.Text = L.T("Неверный PIN.", "Wrong PIN.");
            return;
        }

        Settings.ParentalControlPinHash = null;
        PinBox.Password = string.Empty;
        await _settingsService.SaveAsync(Settings);
        StatusText.Text = L.T("PIN убран — отключить контроль можно будет без пароля.", "PIN removed — the control can now be disabled without a password.");
        LoadSection();
    }
}
