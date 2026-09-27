using IptvPlayer.Models;

namespace IptvPlayer.Services;

// Live view of the user-selected online-cinema parsing methods. Services read
// it directly — the settings dialog mutates the shared AppSettings instance
// (SettingsService.Current), so changes apply without restarts
public static class OnlineCinemaMethods
{
    private static AppSettings? S => SettingsService.Current;

    // curl.exe goes first: its TLS fingerprint passes the site WAF
    public static bool UseCurl => S?.OnlineCinemaUseCurl ?? true;

    // Plain .NET HttpClient — the second tier
    public static bool UseHttpClient => S?.OnlineCinemaUseHttpClient ?? true;

    // Hidden WebView2 — the last resort; off also disables the browser
    // fallback of stream resolution
    public static bool UseWebView2 => S?.OnlineCinemaUseWebView2 ?? true;

    // Any pure-HTTP tier available at all
    public static bool AnyHttp => UseCurl || UseHttpClient;
}
