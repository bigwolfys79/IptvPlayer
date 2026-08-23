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
/// Проверка обновлений — из GitHub Releases репозитория проекта (по
/// умолчанию) или по своему URL (AppSettings.UpdateCheckUrl): ожидается
/// JSON {"version": "1.7.0", "url": "https://.../setup.exe"} либо ответ
/// GitHub API /releases/latest (tag_name + assets[].browser_download_url).
/// Если версия больше текущей — кнопка «Скачать обновление» открывает
/// установщик в браузере.
/// </summary>
public sealed partial class AboutDialog : UserControl
{
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>GitHub Releases проекта — источник обновлений по умолчанию.</summary>
    private const string DefaultUpdateUrl =
        "https://api.github.com/repos/bigwolfys79/IptvPlayer/releases/latest";

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

        var url = string.IsNullOrWhiteSpace(_settings.UpdateCheckUrl)
            ? DefaultUpdateUrl
            : _settings.UpdateCheckUrl;

        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = L.T("Проверяю обновления...", "Checking for updates...");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // GitHub API требует User-Agent.
            request.Headers.UserAgent.ParseAdd("IptvPlayer-UpdateCheck");
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            var current = Version.Parse(GetAppVersion());
            string? availableText;
            string? downloadUrl;

            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("tag_name", out var tag))
                {
                    // Формат GitHub API /releases/latest.
                    availableText = tag.GetString()?.TrimStart('v', 'V');
                    downloadUrl = root.TryGetProperty("assets", out var assets) && assets.GetArrayLength() > 0
                        ? assets[0].TryGetProperty("browser_download_url", out var assetUrl)
                            ? assetUrl.GetString()
                            : null
                        : root.TryGetProperty("html_url", out var html) ? html.GetString() : null;
                }
                else
                {
                    // Простой формат {"version": "...", "url": "..."}.
                    availableText = root.TryGetProperty("version", out var v) ? v.GetString() : null;
                    downloadUrl = root.TryGetProperty("url", out var u) ? u.GetString() : null;
                }
            }

            var available = Version.Parse(availableText ?? "0.0.0");

            if (available > current && !string.IsNullOrEmpty(downloadUrl))
            {
                _updateUrl = downloadUrl;
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
