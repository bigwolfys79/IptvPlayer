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
            Title = L.T("Roditelskiy_Kontrol_Lbl"),
            Content = this,
            CloseButtonText = L.T("Zakryt")
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
            var pinNote = locked ? L.T("Parental_StatusLockedNote") : L.T("Parental_StatusUnlockedNote");
            StatusText.Text = Settings.ParentalControlEnabled
                ? string.Format(L.T("Parental_StatusOn"), blockedCount) + " " + pinNote
                : L.T("Vyklyuchen");

            // Заблокировано → только секция разблокировки с PIN.
            var needUnlock = Settings.ParentalControlEnabled && locked;
            UnlockPanel.Visibility = needUnlock ? Visibility.Visible : Visibility.Collapsed;
            EditPanel.Visibility = needUnlock ? Visibility.Collapsed : Visibility.Visible;

            UnlockHint.Text = L.T("Vvedite_PIN_I_Vyberite_Na_Skolko");
            UnlockForeverButton.Content = L.T("Do_Vyklyucheniya");

            EnabledToggle.IsOn = Settings.ParentalControlEnabled;
            EnabledToggle.Header = L.T("Skryvat_Kanaly_Vybrannykh_Grupp");
            EnabledToggle.OnContent = L.T("Vkl");
            EnabledToggle.OffContent = L.T("Vykl");
            EnabledHint.Text = L.T("Kanaly_Ostayutsya_V_Spiske_No_Zapusk");

            GroupsHeader.Text = L.T("Skryvaemye_Gruppy");
            GroupsHint.Text = L.T("Zapusk_Kanalov_Otmechennykh_Grupp_Zaprashivaet_PIN");
            BuildGroupsList();

            PinHeader.Text = L.T("PIN_Kod");
            PinHint.Text = string.IsNullOrEmpty(Settings.ParentalControlPinHash)
                ? L.T("Parental_PinHintNoPin")
                : L.T("Parental_PinHintHasPin");
            NewPinBox.PlaceholderText = L.T("Novyy_PIN_4_Tsifry");
            SetPinButton.Content = L.T("Ustanovit");
            RemovePinButton.Content = L.T("Ubrat_PIN");
            LockNowButton.Content = L.T("Zaprashivat_PIN_Seychas");
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
                Text = L.T("V_Pleyliste_Net_Grupp"),
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
            StatusText.Text = L.T("Nevernyy_PIN");
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
            StatusText.Text = L.T("PIN_Dolzhen_Byt_Ne_Koroche_4");
            return;
        }

        // Смена PIN требует знать старый, если он установлен (секция и так
        // доступна только после разблокировки, но лишний барьер не мешает).
        if (!string.IsNullOrEmpty(Settings.ParentalControlPinHash) &&
            !ParentalControlService.VerifyPin(Settings, PinBox.Password))
        {
            StatusText.Text = L.T("Vvedite_STARYY_PIN_V_Verkhnem_Pole");
            return;
        }

        Settings.ParentalControlPinHash = ParentalControlService.HashPin(pin);
        NewPinBox.Password = string.Empty;
        await _settingsService.SaveAsync(Settings);
        _logger.LogInformation("PIN родительского контроля изменён.");
        StatusText.Text = L.T("PIN_Ustanovlen");
        LoadSection();
    }

    private async void RemovePinButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(Settings.ParentalControlPinHash) &&
            !ParentalControlService.VerifyPin(Settings, PinBox.Password))
        {
            StatusText.Text = L.T("Nevernyy_PIN");
            return;
        }

        Settings.ParentalControlPinHash = null;
        PinBox.Password = string.Empty;
        await _settingsService.SaveAsync(Settings);
        StatusText.Text = L.T("PIN_Ubran_Otklyuchit_Kontrol_Mozhno_Budet");
        LoadSection();
    }
}
