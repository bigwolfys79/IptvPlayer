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
        VersionText.Text = L.T($"Версия {v}", $"Version {v}");

        DescriptionText.Text = L.T(
            "IPTV-плеер для плейлистов M3U/M3U8 с EPG, архивом передач, видеотекой, записью и родительским контролем.",
            "IPTV player for M3U/M3U8 playlists with EPG, catch-up archive, video library, recording and parental control.");

        FeaturesHeader.Text = L.T("Возможности", "Features");
        FeaturesText.Text = L.T(
            "• Прямые эфиры и архивы (timeshift) с перемоткой\n" +
            "• Видеотека (видео-портал): фильмы, сериалы, выбор качества\n" +
            "• Программа передач XMLTV, напоминания о передачах\n" +
            "• Запись каналов и передач (ffmpeg, до 3 параллельных)\n" +
            "• Избранное, история, «предыдущий канал» (Backspace)\n" +
            "• Мини-плеер (Ctrl+M), статистика потока (Ctrl+J)\n" +
            "• Полуавтоматическое обновление (GitHub Releases)\n" +
            "• Родительский контроль, тёмная тема, русский/английский",
            "• Live streams and catch-up archives with seeking\n" +
            "• Video library (portal): films, series, quality selection\n" +
            "• XMLTV programme guide, programme reminders\n" +
            "• Channel/programme recording (ffmpeg, up to 3 parallel)\n" +
            "• Favorites, history, previous channel (Backspace)\n" +
            "• Mini player (Ctrl+M), stream stats (Ctrl+J)\n" +
            "• Semi-automatic updates (GitHub Releases)\n" +
            "• Parental control, dark theme, Russian/English");

        ComponentsHeader.Text = L.T("Компоненты", "Components");
        ComponentsText.Text =
            "FFmpeg / FFmpegInteropX (демуксинг и декодирование HEVC, AC-3 и др.)\n" +
            "Windows App SDK (WinUI 3), .NET 8, CommunityToolkit\n" +
            "Serilog (логирование), Inno Setup (установщик)\n" +
            L.T("EPG: XMLTV (epg.one), сопоставление каналов — таблица epg.one/setup-playlist",
                "EPG: XMLTV (epg.one), channel matching via the epg.one/setup-playlist table");

        PathsHeader.Text = L.T("Настройки и данные", "Settings and data");
        PathsText.Text =
            L.T($"Настройки и кэш: {Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\IptvPlayer",
                $"Settings and cache: {Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\IptvPlayer") + "\n" +
            L.T($"Лог: {App.LogDirectory}", $"Log: {App.LogDirectory}");

        // Каждый показ диалога начинается заново: прежний результат проверки
        // внутри одного запуска приложения уже неактуален.
        _update = null;
        CheckUpdateButton.Content = L.T("Проверить обновления", "Check for updates");
        UpdateStatusText.Visibility = Visibility.Collapsed;
        OpenLogsButton.Content = L.T("Открыть папку логов", "Open logs folder");

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = L.T("О программе", "About"),
            Content = this,
            CloseButtonText = L.T("Закрыть", "Close")
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
        UpdateStatusText.Text = L.T("Проверяю обновления...", "Checking for updates...");

        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (update == null)
            {
                UpdateStatusText.Text = L.T(
                    $"У вас последняя версия ({GetAppVersion()}).",
                    $"You are up to date ({GetAppVersion()}).");
                CheckUpdateButton.IsEnabled = true;
                return;
            }

            _update = update;
            UpdateStatusText.Text = L.T(
                $"Доступна версия {update.Version} (у вас {GetAppVersion()}).",
                $"Version {update.Version} is available (you have {GetAppVersion()}).");
            CheckUpdateButton.Content = L.T("Скачать и установить", "Download and install");
            CheckUpdateButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Проверка обновлений в «О программе» не удалась.");
            UpdateStatusText.Text = L.T(
                $"Не удалось проверить обновления: {ex.Message}",
                $"Update check failed: {ex.Message}");
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
        UpdateStatusText.Text = L.T("Скачиваю установщик... 0%", "Downloading installer... 0%");

        try
        {
            var progress = new Progress<double>(p =>
                UpdateStatusText.Text = L.T(
                    $"Скачиваю установщик... {p:F0}%",
                    $"Downloading installer... {p:F0}%"));
            var setupPath = await _updateService.DownloadAsync(_update, progress);

            UpdateStatusText.Text = L.T(
                $"Установщик версии {_update.Version} скачан.",
                $"Installer for version {_update.Version} downloaded.");

            // Дальше идёт другой ContentDialog («Установить сейчас?»), а два
            // одновременно открытыми быть не могут; даём этому закрыться.
            _hostDialog?.Hide();
            await Task.Delay(50);

            await _installHandler(_update.Version, setupPath);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Скачивание обновления в «О программе» не удалось.");
            UpdateStatusText.Text = L.T(
                $"Не удалось скачать обновление: {ex.Message}",
                $"Failed to download the update: {ex.Message}");
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
