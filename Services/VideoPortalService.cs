using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IptvPlayer.Models;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;


public class PortalCatalogItem
{
    public string Name { get; set; } = string.Empty;
    public string? Group { get; set; }
    public string? LogoUrl { get; set; }
    public string? StreamUrl { get; set; }
    public string RequestJson { get; set; } = string.Empty;


    public string? Description { get; set; }


    public int Year { get; set; }


    public string? Genre { get; set; }
}


public class PortalGenreFilter
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FilterRequestJson { get; set; } = string.Empty;
}


public class PortalCategoryInfo
{
    public int Fid { get; set; }
    public string Title { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
}


public class PortalYearFilter
{
    public string Title { get; set; } = string.Empty;
    public string YearsValue { get; set; } = string.Empty;
}


public class PortalCatalogLoadResult
{
    public List<PortalCatalogItem> Items { get; set; } = new();
    public List<PortalGenreFilter> Genres { get; set; } = new();
    public Dictionary<int, string> CategoryRequests { get; set; } = new();
}


public class PortalStreamResult
{
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string> Variants { get; set; } = new();
}


public class PortalEpisode
{
    public string Title { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;
    public Dictionary<string, string> Variants { get; set; } = new();


    public string RequestJson { get; set; } = string.Empty;
}


public class PortalFlickResult
{
    public string SerialTitle { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? PosterUrl { get; set; }
    public List<PortalEpisode> Episodes { get; set; } = new();
}

public interface IVideoPortalService
{


    Task<List<PortalCatalogItem>> LoadCatalogAsync(PlaylistSource source, CancellationToken ct = default);


    Task<(List<PortalGenreFilter> Genres, List<PortalYearFilter> Years, List<PortalCategoryInfo> Categories)> LoadManifestInfoAsync(PlaylistSource source, CancellationToken ct = default);


    Task<List<PortalCatalogItem>> LoadFilteredAsync(
        PlaylistSource source, int fid, int? genreId, string? yearOrRange,
        CancellationToken ct = default);


    Task<PortalFlickResult> ResolveEpisodesAsync(PlaylistSource source, string requestJson, CancellationToken ct = default);
}


public class VideoPortalService : IVideoPortalService
{
    private const int MaxLoggedChars = 8192;
    private const int PageSize = 300;
    private const int MaxPagesPerCategory = 500;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly HttpClient _httpClient;
    private readonly ILogger<VideoPortalService> _logger;

    private static readonly Dictionary<string, (string Json, DateTime LoadedAt)> _manifestCache = new();
    private static readonly object _manifestCacheLock = new();
    private static readonly TimeSpan ManifestCacheTtl = TimeSpan.FromMinutes(5);

    public VideoPortalService(
        ILogger<VideoPortalService> logger,
        HttpClient? httpClient = null)
    {
        _logger = logger;
        _httpClient = httpClient ?? CreateDefaultHttpClient();
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) IptvPlayer/1.0");
        return client;
    }

    public async Task<List<PortalCatalogItem>> LoadCatalogAsync(PlaylistSource source, CancellationToken ct = default)
    {
        var result = new List<PortalCatalogItem>();
        var key = NormalizeKey(source);

        using var manifest = await GetManifestAsync(source, key, ct);

        var genres = ParseGenreFilters(manifest.RootElement);
        _logger.LogInformation("Портал: жанров из manifest: {Count}.", genres.Count);

        var categories = FindArray(manifest.RootElement, "items");
        if (categories is not { } categoryArray)
        {
            _logger.LogWarning(
                "Портал {Url}: в manifest нет массива items — каталог пуст (см. лог ответа выше).", source.Url);
            return result;
        }


        var semaphore = new SemaphoreSlim(4);
        var categoryTasks = new List<Task>();

        foreach (var category in categoryArray.EnumerateArray())
        {
            if (category.ValueKind != JsonValueKind.Object ||
                !string.Equals(GetString(category, "type"), "category", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var categoryTitle = GetString(category, "title") ?? L.T("Bez_Kategorii");
            var requestJson = GetObjectAsJson(category, "request");
            if (requestJson == null)
            {
                _logger.LogWarning("Портал: категория «{Category}» без request-объекта — пропущена.", categoryTitle);
                continue;
            }

            using (var requestDoc = JsonDocument.Parse(requestJson))
            {
                var fid = GetInt(requestDoc.RootElement, "fid");
                if (fid == 10001)
                {
                    _logger.LogInformation("Портал: категория «{Category}» пропущена (история просмотров).", categoryTitle);
                    continue;
                }
            }

            var cat = categoryTitle;
            var req = requestJson;
            categoryTasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    await LoadCategoryAsync(source, key, req, cat, null, result, ct);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        await Task.WhenAll(categoryTasks);
        _logger.LogInformation("Портал {Url}: каталог загружен, элементов: {Count}.", SecretProtector.Mask(source.Url), result.Count);
        return result;
    }


    public async Task<(List<PortalGenreFilter> Genres, List<PortalYearFilter> Years, List<PortalCategoryInfo> Categories)> LoadManifestInfoAsync(
        PlaylistSource source, CancellationToken ct = default)
    {
        var key = NormalizeKey(source);

        using var manifest = await GetManifestAsync(source, key, ct);
        var genres = ParseGenreFilters(manifest.RootElement);
        var years = ParseYearFilters(manifest.RootElement);

        var categories = new List<PortalCategoryInfo>();
        var categoryArray = FindArray(manifest.RootElement, "items");
        if (categoryArray is { } arr)
        {
            foreach (var category in arr.EnumerateArray())
            {
                if (category.ValueKind != JsonValueKind.Object ||
                    !string.Equals(GetString(category, "type"), "category", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var title = GetString(category, "title");
                var requestJson = GetObjectAsJson(category, "request");
                if (requestJson == null) continue;

                using var reqDoc = JsonDocument.Parse(requestJson);
                var fid = GetInt(reqDoc.RootElement, "fid") ?? 0;
                if (fid <= 0 || fid == 10001) continue;

                categories.Add(new PortalCategoryInfo
                {
                    Fid = fid,
                    Title = title ?? L.T("Bez_Kategorii"),
                    RequestJson = requestJson
                });
            }
        }

        return (genres, years, categories);
    }


    public async Task<List<PortalCatalogItem>> LoadFilteredAsync(
        PlaylistSource source, int fid, int? genreId, string? yearOrRange,
        CancellationToken ct = default)
    {
        var key = NormalizeKey(source);
        var result = new List<PortalCatalogItem>();

        string filterRequest;
        var hasYear = !string.IsNullOrEmpty(yearOrRange);
        var hasGenre = genreId.HasValue;

        if (hasGenre || hasYear)
        {

            var genreValue = genreId.GetValueOrDefault();
            var sb = new System.Text.StringBuilder("{");
            sb.Append($"\"key\":\"{key}\"");
            sb.Append(",\"filter\":\"on\"");
            if (hasGenre)
            {
                sb.Append($",\"genre\":{genreValue}");
            }
            if (hasYear)
            {
                sb.Append($",\"years\":\"{yearOrRange}\"");
            }
            sb.Append(",\"offset\":0,\"limit\":0}");
            filterRequest = sb.ToString();
        }
        else
        {

            filterRequest = $"{{\"key\":\"{key}\",\"cmd\":\"flicks\",\"fid\":{fid},\"offset\":0,\"limit\":0}}";
        }

        var label = BuildFilterLabel(genreId, yearOrRange);
        _logger.LogInformation(
            "Портал: запрос фильтра — genre={Genre}, year={Year}, fid={Fid}, mode={Mode}.",
            genreId?.ToString() ?? "-", yearOrRange ?? "-", fid,
            (hasGenre || hasYear) ? "filter" : "category");

        await LoadCategoryAsync(source, key, filterRequest, label,
            hasGenre ? GetGenreTitle(genreId.GetValueOrDefault()) : null, result, ct);
        return result;
    }

    private static string BuildFilterLabel(int? genreId, string? yearOrRange)
    {
        var parts = new List<string>();


        if (genreId.HasValue) parts.Add(GetGenreTitle(genreId.GetValueOrDefault()));
        if (!string.IsNullOrEmpty(yearOrRange)) parts.Add(yearOrRange);
        return parts.Count > 0 ? string.Join(" ", parts) : "Все";
    }

    private static string GetGenreTitle(int genreId) => genreId switch
    {
        1 => "биография", 2 => "боевик", 3 => "вестерны", 4 => "военные",
        5 => "детективы", 6 => "документальные", 7 => "драмы", 8 => "исторические",
        9 => "комедии", 10 => "криминальные", 11 => "мелодрамы", 12 => "мистические",
        13 => "мультфильмы", 14 => "мюзиклы", 15 => "приключения", 16 => "семейные",
        17 => "спортивные", 18 => "тв-передачи", 19 => "триллеры", 20 => "ужасы",
        21 => "фантастика", 22 => "фэнтези", 24 => "телекарапузики", 25 => "обучающие",
        26 => "короткометражный", 27 => "юмор", 40 => "Новогодний",
        _ => $"жанр {genreId}"
    };


    private static bool IsGenreableCategory(string requestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var fid = GetInt(doc.RootElement, "fid");
            return fid is 1 or 2;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task LoadCategoryWithGenresAsync(
        PlaylistSource source, string key, string requestJson, string categoryTitle,
        List<PortalGenreFilter> genres, List<PortalCatalogItem> result, CancellationToken ct)
    {

        var semaphore = new SemaphoreSlim(4);
        var genreResults = new List<(string GenreTitle, List<PortalCatalogItem> Items)>();

        var genreTasks = genres.Select(async genre =>
        {
            if (ct.IsCancellationRequested) return ((string GenreTitle, List<PortalCatalogItem> Items)?)null;

            var genreRequest = MergeFields(requestJson, new Dictionary<string, JsonElement>
            {
                ["filter"] = JsonSerializer.SerializeToElement("on"),
                ["genre"] = JsonSerializer.SerializeToElement(genre.Id)
            });

            var items = new List<PortalCatalogItem>();
            await semaphore.WaitAsync(ct);
            try
            {
                await LoadCategoryAsync(source, key, genreRequest, categoryTitle, genre.Title, items, ct);
            }
            finally
            {
                semaphore.Release();
            }

            _logger.LogInformation(
                "Портал: категория «{Category}» — жанр «{Genre}»: {Count} элементов.",
                categoryTitle, genre.Title, items.Count);

            return (genre.Title, items);
        }).ToList();

        var completedGenreResults = await Task.WhenAll(genreTasks);


        var seenFids = new HashSet<int>();
        foreach (var genreResult in completedGenreResults)
        {
            if (genreResult == null) continue;
            foreach (var item in genreResult.Value.Items)
            {
                if (string.IsNullOrEmpty(item.Genre))
                {
                    item.Genre = genreResult.Value.GenreTitle;
                }

                if (!string.IsNullOrEmpty(item.RequestJson))
                {
                    using var reqDoc = JsonDocument.Parse(item.RequestJson);
                    if (GetInt(reqDoc.RootElement, "fid") is { } itemFid)
                    {
                        seenFids.Add(itemFid);
                    }
                }
                result.Add(item);
            }
        }


        var allGenreRequest = MergeKey(requestJson, key);
        var allGenreItems = new List<PortalCatalogItem>();
        await semaphore.WaitAsync(ct);
        try
        {
            await LoadCategoryAsync(source, key, allGenreRequest, categoryTitle, null, allGenreItems, ct);
        }
        finally
        {
            semaphore.Release();
        }

        var added = 0;
        foreach (var item in allGenreItems)
        {
            if (!string.IsNullOrEmpty(item.RequestJson))
            {
                using var reqDoc = JsonDocument.Parse(item.RequestJson);
                if (GetInt(reqDoc.RootElement, "fid") is { } itemFid && seenFids.Contains(itemFid))
                {
                    continue;
                }
            }
            result.Add(item);
            added++;
        }

        if (added > 0)
        {
            _logger.LogInformation(
                "Портал: категория «{Category}» — без жанра: {Count} элементов.",
                categoryTitle, added);
        }
    }

    private async Task LoadCategoryAsync(
        PlaylistSource source, string key, string requestJson, string categoryTitle,
        string? genre, List<PortalCatalogItem> result, CancellationToken ct)
    {
        var offset = 0;
        var total = (int?)null;
        var pages = 0;

        while (pages++ < MaxPagesPerCategory)
        {
            var pageRequest = MergeKey(WithPaging(requestJson, offset, PageSize), key);

            JsonDocument response;
            try
            {
                response = await PostAsync(source, CommandEndpoint(pageRequest), pageRequest, ct);
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(
                    "Портал: категория «{Category}» — сервер вернул невалидный JSON " +
                    "(часто последняя пустая страница): {Error}. Сохраняем {Count} уже загруженных элементов.",
                    categoryTitle, ex.Message, result.Count);
                return;
            }
            using (response)
            {
                total ??= GetInt(response.RootElement, "count");
                var items = FindArray(response.RootElement, "items");
                if (items is not { } itemArray)
                {
                    _logger.LogWarning("Портал: категория «{Category}» — в ответе нет массива items.", categoryTitle);
                    return;
                }

                var added = 0;
                var hasNext = false;
                foreach (var item in itemArray.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var type = GetString(item, "type");
                    if (string.Equals(type, "next", StringComparison.OrdinalIgnoreCase))
                    {
                        hasNext = true;
                        continue;
                    }

                    if (string.Equals(type, "category", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var name = GetString(item, "title");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var year = GetInt(item, "year");
                    if (year > 0)
                    {
                        name = $"{name} ({year})";
                    }

                    result.Add(new PortalCatalogItem
                    {
                        Name = name!,
                        Group = categoryTitle,
                        LogoUrl = GetString(item, "img") ?? GetString(item, "imglr"),
                        StreamUrl = GetString(item, "url"),
                        RequestJson = GetObjectAsJson(item, "request") ?? string.Empty,
                        Description = GetString(item, "description"),
                        Year = year ?? 0,
                        Genre = genre
                    });
                    added++;
                }

                if (added == 0 || !hasNext)
                {
                    return;
                }

                offset += added;

                if (total.HasValue && offset >= total.Value)
                {
                    return;
                }

                _logger.LogInformation(
                    "Портал: категория «{Category}» — загружено {Loaded}/{Total}.", categoryTitle, offset, total);
            }
        }

        _logger.LogWarning(
            "Портал: категория «{Category}» прервана после {MaxPages} страниц (защита от бесконечной пагинации).",
            categoryTitle, MaxPagesPerCategory);
    }

    public async Task<PortalFlickResult> ResolveEpisodesAsync(PlaylistSource source, string requestJson, CancellationToken ct = default)
    {
        var key = NormalizeKey(source);
        var body = MergeKey(requestJson, key);
        using var response = await PostAsync(source, CommandEndpoint(body), body, ct);
        var root = response.RootElement;

        var result = new PortalFlickResult
        {
            SerialTitle = GetString(root, "title") ?? string.Empty,
            Description = GetString(root, "description"),
            PosterUrl = GetString(root, "img") ?? GetString(root, "imglr")
        };

        if (FindArray(root, "items") is { } itemArray)
        {
            foreach (var item in itemArray.EnumerateArray())
            {
                var url = GetString(item, "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var variants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                CopyVariants(item, variants);
                result.Episodes.Add(new PortalEpisode
                {
                    Title = GetString(item, "title") ?? L.T("Epizod"),
                    StreamUrl = url!,
                    Variants = variants,
                    RequestJson = GetObjectAsJson(item, "request") ?? string.Empty
                });
            }
        }

        if (result.Episodes.Count == 0 && GetString(root, "url") is { Length: > 0 } rootUrl)
        {
            var variants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            CopyVariants(root, variants);
            result.Episodes.Add(new PortalEpisode
            {
                Title = result.SerialTitle is { Length: > 0 } t ? t : L.T("Vosproizvesti"),
                StreamUrl = rootUrl,
                Variants = variants,
                RequestJson = requestJson
            });
        }

        if (result.Episodes.Count == 0)
        {
            throw new InvalidOperationException(L.T("Portal_Ne_Vernul_Ssylku_Na_Potok"));
        }

        return result;
    }


    private static void CopyVariants(JsonElement element, Dictionary<string, string> variants)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("variants", out var v) ||
            v.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in v.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.Value.GetString()) &&
                !variants.ContainsKey(property.Name))
            {
                variants[property.Name] = property.Value.GetString()!;
            }
        }
    }


    private static string NormalizeKey(PlaylistSource source)
    {
        if (string.IsNullOrWhiteSpace(source.PortalKey))
        {
            throw new InvalidOperationException(L.T("U_Istochnika_Portala_Ne_Zadan_Klyuch"));
        }

        var key = source.PortalKey.Trim();
        var prefix = "portal::[key:";
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key.EndsWith(']'))
        {
            key = key[prefix.Length..^1];
        }

        return key;
    }


    private static string CommandEndpoint(string requestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            if (doc.RootElement.TryGetProperty("cmd", out var cmd) &&
                cmd.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(cmd.GetString()))
            {
                return cmd.GetString()!.Trim() + ".json";
            }

            if (doc.RootElement.TryGetProperty("filter", out var filter) &&
                filter.ValueKind == JsonValueKind.String &&
                string.Equals(filter.GetString(), "on", StringComparison.OrdinalIgnoreCase))
            {
                return "flicks.json";
            }
        }
        catch (JsonException)
        {
        }

        return "manifest.json";
    }


    private static string WithPaging(string requestJson, int offset, int limit) =>
        MergeFields(requestJson, new Dictionary<string, JsonElement>
        {
            ["offset"] = JsonSerializer.SerializeToElement(offset),
            ["limit"] = JsonSerializer.SerializeToElement(limit)
        });


    private static string MergeKey(string requestJson, string key) =>
        MergeFields(requestJson, new Dictionary<string, JsonElement>
        {
            ["key"] = JsonSerializer.SerializeToElement(key)
        });


    private static string MergeFields(string requestJson, Dictionary<string, JsonElement> overrides)
    {
        try
        {
            var inputBytes = Encoding.UTF8.GetBytes(requestJson);
            using var doc = JsonDocument.Parse(inputBytes);
            var output = new MemoryStream(inputBytes.Length);
            var writer = new Utf8JsonWriter(output);

            writer.WriteStartObject();

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (overrides.TryGetValue(prop.Name, out var overrideValue))
                {
                    writer.WritePropertyName(prop.Name);
                    overrideValue.WriteTo(writer);
                }
                else
                {
                    writer.WritePropertyName(prop.Name);
                    prop.Value.WriteTo(writer);
                }
            }

            foreach (var (name, value) in overrides)
            {
                if (!doc.RootElement.TryGetProperty(name, out _))
                {
                    writer.WritePropertyName(name);
                    value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (JsonException)
        {
            return requestJson;
        }
    }


    private async Task<JsonDocument> GetManifestAsync(PlaylistSource source, string key, CancellationToken ct)
    {
        var cacheKey = source.Url ?? string.Empty;
        lock (_manifestCacheLock)
        {
            if (_manifestCache.TryGetValue(cacheKey, out var cached) &&
                (DateTime.UtcNow - cached.LoadedAt) < ManifestCacheTtl)
            {
                return JsonDocument.Parse(cached.Json);
            }
        }

        var manifest = await PostAsync(source, "manifest.json", $"{{\"key\":\"{key}\"}}", ct);

        lock (_manifestCacheLock)
        {
            _manifestCache[cacheKey] = (manifest.RootElement.GetRawText(), DateTime.UtcNow);
        }

        return manifest;
    }

    private async Task<JsonDocument> PostAsync(
        PlaylistSource source, string endpoint, string bodyJson, CancellationToken ct)
    {
        var url = BuildUrl(source.Url, endpoint);
        _logger.LogInformation("Портал → POST {Url} тело: {Body}", SecretProtector.Mask(url),
            Truncate(SecretProtector.Mask(bodyJson)));

        using var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);

        _logger.LogInformation("Портал ← {Url}: получено {Bytes} байт.",
            SecretProtector.Mask(url), body.Length);
        _logger.LogDebug("Портал ← {Url}: {Body}", SecretProtector.Mask(url),
            Truncate(SecretProtector.Mask(body)));

        return JsonDocument.Parse(body);
    }

    private static string BuildUrl(string baseUrl, string endpoint)
    {
        if (!baseUrl.EndsWith('/'))
        {
            baseUrl += "/";
        }

        return $"{baseUrl}{endpoint}";
    }

    private static JsonElement? FindArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            return value;
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var i)
            ? i
            : null;

    private static string? GetObjectAsJson(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Serialize(value)
            : null;

    private static string Truncate(string text) =>
        text.Length <= MaxLoggedChars ? text : text[..MaxLoggedChars] + "…(обрезано)";

    private static List<PortalGenreFilter> ParseGenreFilters(JsonElement manifest)
    {
        var genres = new List<PortalGenreFilter>();
        if (manifest.ValueKind != JsonValueKind.Object ||
            !manifest.TryGetProperty("controls", out var controls) ||
            controls.ValueKind != JsonValueKind.Object ||
            !controls.TryGetProperty("filters", out var filters) ||
            filters.ValueKind != JsonValueKind.Array)
        {
            return genres;
        }

        foreach (var filter in filters.EnumerateArray())
        {
            if (filter.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(GetString(filter, "type"), "enum", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(GetString(filter, "title"), "Жанр", StringComparison.OrdinalIgnoreCase)) continue;

            if (FindArray(filter, "items") is not { } genreArray) continue;

            foreach (var genreItem in genreArray.EnumerateArray())
            {
                if (genreItem.ValueKind != JsonValueKind.Object) continue;

                var title = GetString(genreItem, "title");
                var filterRequest = GetObjectAsJson(genreItem, "request");
                if (title == null || filterRequest == null) continue;

                var genreId = 0;
                try
                {
                    using var reqDoc = JsonDocument.Parse(filterRequest);
                    genreId = GetInt(reqDoc.RootElement, "genre") ?? 0;
                }
                catch (JsonException) { }

                if (genreId > 0)
                {
                    genres.Add(new PortalGenreFilter
                    {
                        Id = genreId,
                        Title = title,
                        FilterRequestJson = filterRequest
                    });
                }
            }
        }

        return genres;
    }


    private static List<PortalYearFilter> ParseYearFilters(JsonElement manifest)
    {
        var years = new List<PortalYearFilter>();
        if (manifest.ValueKind != JsonValueKind.Object ||
            !manifest.TryGetProperty("controls", out var controls) ||
            controls.ValueKind != JsonValueKind.Object ||
            !controls.TryGetProperty("filters", out var filters) ||
            filters.ValueKind != JsonValueKind.Array)
        {
            return years;
        }

        foreach (var filter in filters.EnumerateArray())
        {
            if (filter.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(GetString(filter, "type"), "enum", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(GetString(filter, "title"), "Год", StringComparison.OrdinalIgnoreCase)) continue;

            if (FindArray(filter, "items") is not { } yearArray) continue;

            foreach (var yearItem in yearArray.EnumerateArray())
            {
                if (yearItem.ValueKind != JsonValueKind.Object) continue;

                var title = GetString(yearItem, "title");
                if (string.IsNullOrWhiteSpace(title)) continue;

                string yearsValue = title;
                var filterRequest = GetObjectAsJson(yearItem, "request");
                if (filterRequest != null)
                {
                    try
                    {
                        using var reqDoc = JsonDocument.Parse(filterRequest);
                        var y = GetString(reqDoc.RootElement, "years");
                        if (!string.IsNullOrWhiteSpace(y)) yearsValue = y;
                    }
                    catch (JsonException) { }
                }

                years.Add(new PortalYearFilter
                {
                    Title = title,
                    YearsValue = yearsValue
                });
            }
        }

        return years;
    }
}
