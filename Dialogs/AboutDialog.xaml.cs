using System;
using System.Threading.Tasks;
using IptvPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer.Dialogs;

/// <summary>
/// «О программе»: описание, компоненты, пути настроек/логов, кнопки
/// «Проверить обновления» и «Открыть папку логов».
///
/// Проверка и установка обновлений идут через <see cref="IUpdateService"/> —
/// тот же сценарий, что и при автоматической проверке при старте: найденная
/// версия превращает кнопку в «Скачать и установить», установщик качается во
/// временную папку (с проверкой SHA256), затем вызывается
/// <c>_installHandler</c> (MainPage): диалог «Установить сейчас?», откладывание
/// при активных записях, тихая установка и перезапуск.
/// </summary>
public sealed partial class AboutDialog : UserControl
{
    private readonly IUpdateService _updateService;

    /// <summary>
    /// Запускает установку уже скачанного установщика (диалог согласия,
    /// учёт записей, тихая установка). Диалог «О программе» перед вызовом
    /// закрывается сам — открыть второй ContentDialog поверх нельзя.
    /// </summary>
    private readonly Func<Version, string, Task> _installHandler;

    private ContentDialog? _hostDialog;
    private UpdateInfo? _update;

    public AboutDialog(IUpdateService updateService,
        Func<Version, string, Task> installHandler)
    {
        _updateService = updateService;
        _installHandler = installHandler;
        InitializeComponent();
    }

    public async Task ShowAsync(XamlRoot xamlRoot)
    {
        var v = GetAppVersion();
        VersionText.Text = string.Format(L.T("Versiya_0"), v, v);

        DescriptionText.Text = L.T("IPTV_Pleer_Dlya_Pleylistov_M3U_M3U8");

        FeaturesHeader.Text = L.T("Vozmozhnosti");
        FeaturesText.Text = L.T("About_FeaturesList");

        ComponentsHeader.Text = L.T("Komponente");
        ComponentsText.Text =
            L.T("About_ComponentsList") + "\n" +
            L.T("EPG_XMLTV_Epg_One_Sopostavlenie_Kanalov");

        PathsHeader.Text = L.T("Nastroyki_I_Dannee");
        PathsText.Text =
            string.Format(L.T("Nastroyki_I_Kesh_0_IptvPlayer"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) + "\n" +
            string.Format(L.T("Log_0"), App.LogDirectory, App.LogDirectory);

        // Каждый показ диалога начинается заново: прежний результат проверки
        // внутри одного запуска приложения уже неактуален.
        _update = null;
        CheckUpdateButton.Content = L.T("Proverit_Obnovleniya");
        UpdateStatusText.Visibility = Visibility.Collapsed;
        OpenLogsButton.Content = L.T("Otkryt_Papku_Logov");
        var dialog = new ThemedContentDialog
        {
            XamlRoot = xamlRoot,
            Title = L.T("O_Programme_Lbl"),
            Content = this,
            CloseButtonText = L.T("Zakryt")
        };
        _hostDialog = dialog;
        await dialog.ShowAsync();
    }

    internal static string GetAppVersion()
    {
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            // Unpackaged-сборка (Inno Setup) — берём версию сборки.
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0";
        }
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_update == null)
        {
            await CheckForUpdateAsync();
        }
        else
        {
            await DownloadAndOfferAsync();
        }
    }

    private async Task CheckForUpdateAsync()
    {
        UpdateStatusText.Visibility = Visibility.Visible;
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = L.T("Proveryayu_Obnovleniya");

        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (update == null)
            {
                UpdateStatusText.Text = string.Format(L.T("U_Vas_Poslednyaya_Versiya_0"), GetAppVersion(), GetAppVersion());
                CheckUpdateButton.IsEnabled = true;
                return;
            }

            _update = update;
            UpdateStatusText.Text = string.Format(L.T("Dostupna_Versiya_0_U_Vas_1"), update.Version, GetAppVersion(), update.Version, GetAppVersion());
            CheckUpdateButton.Content = L.T("Skachat_I_Ustanovit");
            CheckUpdateButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Проверка обновлений в «О программе» не удалась.");
            UpdateStatusText.Text = string.Format(L.T("Ne_Udalos_Proverit_Obnovleniya_0"), ex.Message, ex.Message);
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task DownloadAndOfferAsync()
    {
        if (_update == null)
        {
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = L.T("Skachivayu_Ustanovshchik_0");

        try
        {
            var progress = new Progress<double>(p =>
                UpdateStatusText.Text = string.Format(L.T("Skachivayu_Ustanovshchik_0_2"), $"{p:F0}", $"{p:F0}"));
            var setupPath = await _updateService.DownloadAsync(_update, progress);

            UpdateStatusText.Text = string.Format(L.T("Ustanovshchik_Versii_0_Skachan"), _update.Version, _update.Version);

            // Дальше идёт другой ContentDialog («Установить сейчас?»), а два
            // одновременно открытыми быть не могут; даём этому закрыться.
            _hostDialog?.Hide();
            await Task.Delay(50);

            await _installHandler(_update.Version, setupPath);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Скачивание обновления в «О программе» не удалось.");
            UpdateStatusText.Text = string.Format(L.T("Ne_Udalos_Skachat_Obnovlenie_0"), ex.Message, ex.Message);
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(App.LogDirectory);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", App.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Не удалось открыть папку логов.");
        }
    }
}
