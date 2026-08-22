using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IptvPlayer.Dialogs;

/// <summary>
/// «О программе»: описание, компоненты, пути настроек/логов, кнопки
/// «Проверить обновления» и «Открыть папку логов».
///
/// Проверка обновлений: GET по AppSettings.UpdateCheckUrl — ожидается JSON
/// {"version": "1.7.0", "url": "https://.../setup.exe"}. Если версия в JSON
/// больше текущей — предлагаем открыть ссылку в браузере. URL не задан —
/// сообщаем, где его прописать (settings.json), чтобы не хардкодить чужой
/// сервер в программе.
/// </summary>
public sealed partial class AboutDialog : UserControl
{
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    private readonly AppSettings _settings;

    private ContentDialog? _hostDialog;
    private string? _updateUrl;

    public AboutDialog(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
    }

    public async Task ShowAsync(XamlRoot xamlRoot)
    {
        var v = GetAppVersion();
        VersionText.Text = L.T($"Версия {v}", $"Version {v}");

        DescriptionText.Text = L.T(
            "IPTV-плеер для плейлистов M3U/M3U8 с программой передач (EPG), архивом передач, записью и родительским контролем.",
            "IPTV player for M3U/M3U8 playlists with an electronic programme guide (EPG), catch-up archive, recording and parental control.");

        FeaturesHeader.Text = L.T("Возможности", "Features");
        FeaturesText.Text = L.T(
            "• Воспроизведение прямых эфиров и архивов (timeshift) с перемоткой\n" +
            "• Программа передач XMLTV из нескольких источников, напоминания\n" +
            "• Запись каналов и передач (ffmpeg, до 3 параллельных)\n" +
            "• Избранное, история, «предыдущий канал» (Backspace)\n" +
            "• Родительский контроль: группы за PIN с временной разблокировкой\n" +
            "• Тёмная/светлая тема, русский/английский интерфейс",
            "• Live streams and catch-up archives with seeking\n" +
            "• XMLTV programme guide from multiple sources, reminders\n" +
            "• Channel/programme recording (ffmpeg, up to 3 parallel)\n" +
            "• Favorites, history, previous channel (Backspace)\n" +
            "• Parental control: groups behind a PIN with timed unlock\n" +
            "• Dark/light theme, Russian/English interface");

        ComponentsHeader.Text = L.T("Компоненты", "Components");
        ComponentsText.Text =
            "FFmpeg / FFmpegInteropX (демуксинг и декодирование HEVC, AC-3 и др.)\n" +
            "Windows App SDK (WinUI 3), .NET 8\n" +
            L.T("EPG: XMLTV (epg.one), сопоставление каналов — таблица epg.one/setup-playlist",
                "EPG: XMLTV (epg.one), channel matching via the epg.one/setup-playlist table");

        PathsHeader.Text = L.T("Настройки и данные", "Settings and data");
        PathsText.Text =
            L.T($"Настройки и кэш: {Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\IptvPlayer",
                $"Settings and cache: {Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\\IptvPlayer") + "\n" +
            L.T($"Лог: {App.LogDirectory}", $"Log: {App.LogDirectory}");

        CheckUpdateButton.Content = L.T("Проверить обновления", "Check for updates");
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
        UpdateStatusText.Visibility = Visibility.Visible;

        if (string.IsNullOrWhiteSpace(_settings.UpdateCheckUrl))
        {
            UpdateStatusText.Text = L.T(
                "Проверка обновлений не настроена: укажите UpdateCheckUrl в settings.json " +
                "(JSON вида {\"version\":\"1.7.0\",\"url\":\"https://…/setup.exe\"}).",
                "Update check is not configured: set UpdateCheckUrl in settings.json " +
                "(JSON like {\"version\":\"1.7.0\",\"url\":\"https://…/setup.exe\"}).");
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = L.T("Проверяю обновления...", "Checking for updates...");
        try
        {
            var json = await Http.GetStringAsync(_settings.UpdateCheckUrl);
            var info = JsonSerializer.Deserialize<UpdateInfo>(json);
            var current = Version.Parse(GetAppVersion());
            var available = Version.Parse(info?.Version ?? "0.0.0");

            if (available > current && !string.IsNullOrEmpty(info?.Url))
            {
                _updateUrl = info.Url;
                UpdateStatusText.Text = L.T(
                    $"Доступна версия {available} (у вас {current}).",
                    $"Version {available} is available (you have {current}).");
                CheckUpdateButton.Content = L.T("Скачать обновление", "Download update");
                CheckUpdateButton.IsEnabled = true;
                CheckUpdateButton.Click -= CheckUpdateButton_Click;
                CheckUpdateButton.Click += DownloadUpdateButton_Click;
            }
            else
            {
                UpdateStatusText.Text = L.T(
                    $"У вас последняя версия ({current}).",
                    $"You are up to date ({current}).");
                CheckUpdateButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Проверка обновлений по {Url} не удалась.", _settings.UpdateCheckUrl);
            UpdateStatusText.Text = L.T(
                $"Не удалось проверить обновления: {ex.Message}",
                $"Update check failed: {ex.Message}");
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateUrl == null)
        {
            return;
        }
        // Скачивание установщика — в браузер: он докачает и запустит,
        // отдельный http-клиент с прогрессом тут избыточен.
        await Windows.System.Launcher.LaunchUriAsync(new Uri(_updateUrl));
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

    private sealed class UpdateInfo
    {
        public string? Version { get; set; }
        public string? Url { get; set; }
    }
}
