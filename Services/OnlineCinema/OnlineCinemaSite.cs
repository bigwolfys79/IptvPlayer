using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace IptvPlayer.Services;

// One online-cinema source site. Implementations live per site in separate
// files; the playlist URL picks the adapter at runtime
public interface IOnlineCinemaSite
{
    string Id { get; }

    string BaseUrl { get; }

    // Playlist display name (localized)
    string DisplayName { get; }

    // Quick search via the DLE lightsearch AJAX — not every site has it
    bool SupportsSearch { get; }

    IReadOnlyDictionary<string, string> Categories { get; }

    string CategoryUrl(string category, int page);

    string YearUrl(int year, int page = 1);

    // True when YearUrl returns a real per-year listing; false when it
    // silently falls back to the plain category URL
    bool HasYearListing { get; }

    string UserAgent { get; }

    void ApplyBrowserHeaders(HttpRequestMessage msg);

    void ApplyAjaxHeaders(HttpRequestMessage msg);

    bool IsValidPageUrl(string url);

    bool LooksLikeChallenge(string html);

    // Embed player iframes from the film page HTML, best-parseable first
    List<string> ExtractEmbedUrls(string filmHtml);

    (List<OnlineCinemaItem> Items, int TotalPages) ParseCategoryPageHtml(string html, string category);

    List<OnlineCinemaItem> ParseLightSearchHtml(string html, string category);

    // Runs in the page context (WebView2 fallback); returns a plain
    // JSON-serializable object
    string ParseCategoryPageJs { get; }

    OnlineCinemaItem? ToItem(JsonElement el, string category);

    string SearchCategory { get; }

    string? LightSearchUrl { get; }

    string SearchPageUrl(string query);

    // Plain-HTTP search page (GET) for sites without the lightsearch AJAX;
    // null when only the POST lightsearch exists. Must return a page the
    // category parser understands
    string? SearchRequestUrl(string query);
}


// Registry of the supported cinema sites; the playlist URL host selects one.
// Custom hosts fall back to a live-probed generic DLE adapter (profiles are
// persisted in settings under OnlineCinemaSiteProfiles)
public static class OnlineCinemaSites
{
    public static readonly IOnlineCinemaSite Kinogo = new KinogoOnlineSite();

    public static readonly IOnlineCinemaSite KinogoIs = new KinogoIsOnlineSite();

    public static IReadOnlyList<IOnlineCinemaSite> All => [Kinogo, KinogoIs];

    public static IOnlineCinemaSite Default => Kinogo;

    // Generic adapters are created on demand from persisted profiles
    private static readonly Dictionary<string, IOnlineCinemaSite> GenericCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Drops a cached generic adapter — call after its stored profile was
    // replaced, or the next ByUrl returns the adapter built from the old one
    public static void InvalidateGeneric(string host)
    {
        lock (GenericCache)
        {
            GenericCache.Remove(host);
        }
    }

    // Site adapter matching the URL host (playlist Url / film page URL);
    // null when the host is unknown. Custom hosts resolve through the
    // probed-site profiles stored in settings
    public static IOnlineCinemaSite? ByUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var host = uri.Host;
        var builtin = All.FirstOrDefault(s =>
            Uri.TryCreate(s.BaseUrl, UriKind.Absolute, out var baseUri) &&
            baseUri.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        if (builtin != null)
        {
            return builtin;
        }

        var profile = SettingsService.Current?.OnlineCinemaSiteProfiles
            .FirstOrDefault(p => Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var u) &&
                                 u.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
        if (profile == null)
        {
            return null;
        }

        lock (GenericCache)
        {
            if (!GenericCache.TryGetValue(host, out var site))
            {
                site = new GenericDleSite(profile);
                GenericCache[host] = site;
            }

            return site;
        }
    }
}


// Adapter over the original kinogo.online parser (static KinogoSite)
public sealed class KinogoOnlineSite : IOnlineCinemaSite
{
    public string Id => KinogoSite.Id;

    public string BaseUrl => KinogoSite.BaseUrl;

    public string DisplayName => L.T("OnlineCinema_Tip_Lbl");

    public bool SupportsSearch => true;

    public IReadOnlyDictionary<string, string> Categories => KinogoSite.Categories;

    public string CategoryUrl(string category, int page) => KinogoSite.CategoryUrl(category, page);

    public string YearUrl(int year, int page = 1) => KinogoSite.YearUrl(year, page);

    public bool HasYearListing => true;

    public string UserAgent => KinogoSite.UserAgent;

    public void ApplyBrowserHeaders(HttpRequestMessage msg) => KinogoSite.ApplyBrowserHeaders(msg);

    public void ApplyAjaxHeaders(HttpRequestMessage msg) => KinogoSite.ApplyAjaxHeaders(msg);

    public bool IsValidPageUrl(string url) => KinogoSite.IsValidPageUrl(url);

    public bool LooksLikeChallenge(string html) => KinogoSite.LooksLikeChallenge(html);

    public List<string> ExtractEmbedUrls(string filmHtml) => KinogoSite.ExtractEmbedUrls(filmHtml);

    public (List<OnlineCinemaItem> Items, int TotalPages) ParseCategoryPageHtml(
        string html, string category) => KinogoSite.ParseCategoryPageHtml(html, category);

    public List<OnlineCinemaItem> ParseLightSearchHtml(
        string html, string category) => KinogoSite.ParseLightSearchHtml(html, category);

    public string ParseCategoryPageJs => KinogoSite.ParseCategoryPageJs;

    public OnlineCinemaItem? ToItem(JsonElement el, string category) => KinogoSite.ToItem(el, category);

    public string SearchCategory => KinogoSite.SearchCategory;

    public string? LightSearchUrl => KinogoSite.LightSearchUrl;

    public string SearchPageUrl(string query) => KinogoSite.SearchPageUrl(query);

    public string? SearchRequestUrl(string query) => null;
}
