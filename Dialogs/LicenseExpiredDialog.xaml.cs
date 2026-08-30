using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using IptvPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace IptvPlayer.Dialogs;

/// <summary>
/// Диалог при истечении пробного периода коммерческого использования.
/// Кроме контактов разработчика содержит офлайн-активацию: пользователь
/// копирует HWID, получает от разработчика подписанную лицензию (текстом
/// или .lic-файлом) и вставляет её — сервер активации не нужен.
/// ShowAsync возвращает true, если лицензия была активирована успешно —
/// приложение продолжает запуск.
/// </summary>
public sealed partial class LicenseExpiredDialog : UserControl
{
    private ContentDialog? _hostDialog;
    private bool _activated;

    public LicenseExpiredDialog()
    {
        InitializeComponent();
    }

    /// <summary>Показывает диалог; true — лицензия активирована, запуск продолжать.</summary>
    public async Task<bool> ShowAsync(XamlRoot xamlRoot, int daysRemaining = 0)
    {
        TitleText.Text = L.T("License_TrialExpired_Title");
        MessageText.Text = L.T("License_TrialExpired_Message");

        if (daysRemaining > 0)
        {
            DaysText.Text = string.Format(L.T("License_DaysRemaining_0"), daysRemaining);
        }
        else
        {
            DaysText.Text = L.T("License_TrialExpired_FullMessage");
        }

        // === Офлайн-активация ===
        HwidLabel.Text = L.T("License_HardwareId");
        HwidText.Text = LicenseService.GetHwidCode();
        CopyHwidButton.Click += OnCopyHwidClick;
        ActivateButton.Content = L.T("License_Activate_Button");
        ActivateButton.Click += OnActivateClick;
        ImportFileButton.Content = L.T("License_ImportFile");
        ImportFileButton.Click += OnImportFileClick;

        ContactLabel.Text = L.T("License_ContactDeveloper");
        EmailLink.Content = "bigwolfys@gmail.com";
        EmailLink.Click += OnEmailLinkClick;

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = null,
            Content = this,
            CloseButtonText = L.T("Zakryt")
        };
        _hostDialog = dialog;
        await dialog.ShowAsync();
        return _activated;
    }

    private async void OnActivateClick(object sender, RoutedEventArgs e)
    {
        await ActivateAsync(KeyInput.Text);
    }

    private async void OnImportFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            picker.FileTypeFilter.Add(".lic");
            picker.FileTypeFilter.Add(".txt");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var text = await Windows.Storage.FileIO.ReadTextAsync(file);
            KeyInput.Text = text;
            await ActivateAsync(text);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось прочитать файл лицензии.");
            ShowStatus(L.T("License_FileReadFailed"), isError: true);
        }
    }

    private async Task ActivateAsync(string licenseText)
    {
        if (string.IsNullOrWhiteSpace(licenseText))
        {
            ShowStatus(L.T("License_EmptyKey"), isError: true);
            return;
        }

        ActivateButton.IsEnabled = false;
        ImportFileButton.IsEnabled = false;
        try
        {
            var result = LicenseService.Activate(licenseText);
            if (result.Success)
            {
                _activated = true;
                ShowStatus(L.T("License_ActivationSuccess"), isError: false);
                await Task.Delay(800);
                _hostDialog?.Hide();
                return;
            }

            var message = result.Error switch
            {
                LicenseService.ActivationError.Expired => L.T("License_Error_Expired"),
                LicenseService.ActivationError.WrongMachine => L.T("License_Error_WrongMachine"),
                _ => L.T("License_Error_Invalid")
            };
            ShowStatus(message, isError: true);
        }
        finally
        {
            ActivateButton.IsEnabled = true;
            ImportFileButton.IsEnabled = true;
        }
    }

    private void ShowStatus(string message, bool isError)
    {
        KeyStatusText.Text = message;
        KeyStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            isError
                ? Microsoft.UI.Colors.OrangeRed
                : Microsoft.UI.Colors.ForestGreen);
        KeyStatusText.Visibility = Visibility.Visible;
    }

    private void OnCopyHwidClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(HwidText.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            ShowStatus(L.T("License_HwidCopied"), isError: false);
        }
        catch (COMException ex)
        {
            // Буфер обмена может быть занят другим процессом — не падаем.
            Serilog.Log.Warning(ex, "Не удалось скопировать HWID в буфер обмена.");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось скопировать HWID.");
        }
    }

    private void OnEmailLinkClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var email = "bigwolfys@gmail.com";
            var subject = Uri.EscapeDataString("IptvPlayer - License Inquiry");
            var body = Uri.EscapeDataString(
                $"IptvPlayer v{AboutDialog.GetAppVersion()}\n" +
                $"Usage: Commercial\n" +
                $"Hardware ID: {LicenseService.GetHwidCode()}\n\n" +
                $"Please send me a license key.");

            Process.Start(new ProcessStartInfo(
                $"mailto:{email}?subject={subject}&body={body}")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось открыть почтовый клиент.");
        }
    }
}
