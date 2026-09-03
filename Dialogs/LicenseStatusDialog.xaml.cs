using System;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer.Dialogs;

/// <summary>
/// Диалог «Лицензия» (отдельный пункт меню, перед «О программе»): показывает
/// текущий статус — личное использование, коммерческая лицензия (на кого и
/// на какой срок) или идущий пробный период. Для неактивированной
/// коммерческой копии даёт кнопку принудительной активации — тот же
/// офлайн-диалог, что при истечении триала, без ожидания его конца.
/// </summary>
public sealed partial class LicenseStatusDialog : UserControl
{
    private ContentDialog? _hostDialog;
    private XamlRoot? _xamlRoot;

    public LicenseStatusDialog()
    {
        InitializeComponent();
    }

    public async Task ShowAsync(XamlRoot xamlRoot)
    {
        _xamlRoot = xamlRoot;
        Refresh();

        var dialog = new ThemedContentDialog
        {
            XamlRoot = xamlRoot,
            Title = L.T("License_Dialog_Title"),
            Content = this,
            CloseButtonText = L.T("Zakryt")
        };
        _hostDialog = dialog;
        await dialog.ShowAsync();
    }

    /// <summary>Перечитывает статус лицензии и заполняет тексты.</summary>
    private void Refresh()
    {
        var license = LicenseService.CheckLicense();
        ActivateButton.Visibility = Visibility.Collapsed;

        if (license.UsageType == UsageType.Personal)
        {
            StatusHeader.Text = L.T("License_Status_Personal_Header");
            StatusText.Text = L.T("License_Status_Personal_Text");
            return;
        }

        if (license.IsActivated)
        {
            StatusHeader.Text = L.T("License_Status_Activated_Header");
            StatusText.Text = string.Join(Environment.NewLine,
                string.Format(L.T("License_Licensee_0"), license.Licensee),
                license.ExpiryUtc.HasValue
                    ? string.Format(L.T("License_Expiry_Until_0"),
                        license.ExpiryUtc.Value.ToLocalTime().ToString("yyyy-MM-dd"))
                    : L.T("License_Expiry_Lifetime"));
            if (license.IsExpired)
            {
                // Истекла — активация снова актуальна: показываем кнопку.
                StatusText.Text += Environment.NewLine + L.T("License_Status_Expired");
                ShowActivateButton();
            }
            return;
        }

        // Коммерческий триал: идёт или уже истёк.
        StatusHeader.Text = L.T("License_Status_Trial_Header");
        StatusText.Text = license.DaysRemaining > 0
            ? string.Format(L.T("License_DaysRemaining_0"), license.DaysRemaining)
            : L.T("License_TrialExpired_FullMessage");
        ShowActivateButton();
    }

    private void ShowActivateButton()
    {
        ActivateButton.Content = L.T("License_Manage_Button");
        ActivateButton.Visibility = Visibility.Visible;
    }

    private async void ActivateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_xamlRoot == null) return;

        // Два ContentDialog одновременно открытыми быть не могут — закрываем
        // свой и даём ему закрыться, затем показываем диалог активации.
        _hostDialog?.Hide();
        await Task.Delay(50);

        var activation = new LicenseExpiredDialog();
        await activation.ShowAsync(_xamlRoot, manageMode: true);

        // После активации показываем обновлённый статус заново.
        await ShowAsync(_xamlRoot);
    }
}
