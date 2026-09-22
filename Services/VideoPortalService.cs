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

    // Portal's own flick id ("fid" field), used to intersect genre/year filter results with a category
    public long ItemId { get; set; }


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
        CancellationToken ct = default, IReadOnlySet<long>? categoryIds = null);


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
        var categoryTasks = new List<Task<List<PortalCatalogItem>?>>();

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
                    return await LoadCategoryAsync(source, key, req, cat, null, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad category must not discard the whole catalog
                    _logger.LogWarning(ex, "Портал: категория «{Category}» не загрузилась — пропускаем.", cat);
                    return null;
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        var loaded = await Task.WhenAll(categoryTasks);
        foreach (var items in loaded)
        {
            if (items is { Count: > 0 })
            {
                result.AddRange(items);
            }
        }

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
        CancellationToken ct = default, IReadOnlySet<long>? categoryIds = null)
    {
        var key = NormalizeKey(source);

        string filterRequest;
        var hasYear = !string.IsNullOrEmpty(yearOrRange);
        var hasGenre = genreId.HasValue;

        if (hasGenre || hasYear)
        {

            var genreValue = genreId.GetValueOrDefault();
            var sb = new System.Text.StringBuilder("{");
            sb.Append($"\"key\":{JsonSerializer.Serialize(key)}");
            sb.Append(",\"filter\":\"on\"");
            if (hasGenre)
            {
                sb.Append($",\"genre\":{genreValue}");
            }
            if (hasYear)
            {
                sb.Append($",\"years\":{JsonSerializer.Serialize(yearOrRange)}");
            }
            sb.Append(",\"offset\":0,\"limit\":0}");
            filterRequest = sb.ToString();
        }
        else
        {

            filterRequest = $"{{\"key\":{JsonSerializer.Serialize(key)},\"cmd\":\"flicks\",\"fid\":{fid},\"offset\":0,\"limit\":0}}";
        }

        var label = BuildFilterLabel(genreId, yearOrRange);
        _logger.LogInformation(
            "Портал: запрос фильтра — genre={Genre}, year={Year}, fid={Fid}, mode={Mode}.",
            genreId?.ToString() ?? "-", yearOrRange ?? "-", fid,
            (hasGenre || hasYear) ? "filter" : "category");

        var items = await LoadCategoryAsync(source, key, filterRequest, label,
            hasGenre ? GetGenreTitle(genreId.GetValueOrDefault()) : null, ct, parallelPages: true);

        // Portal ignores fid when filter params are present (and vice versa), so constrain
        // the filter result to the selected category client-side by flick id
        if ((hasGenre || hasYear) && fid > 0)
        {
            var fromSnapshot = categoryIds is { Count: > 0 };
            var ids = fromSnapshot ? categoryIds! : await GetCategoryItemIdsAsync(source, key, fid, ct);
            var before = items.Count;
            items = FilterByCategoryIds(items, ids!);
            _logger.LogInformation(
                "Портал: фильтр ограничен категорией fid={Fid}: {Before} → {After} (источник id: {Source}).",
                fid, before, items.Count, fromSnapshot ? "снапшот" : "сеть");
        }

        return items;
    }


    public static List<PortalCatalogItem> FilterByCategoryIds(List<PortalCatalogItem> items, IReadOnlySet<long> categoryIds)
    {
        if (categoryIds.Count == 0)
        {
            // Category fetch failed or returned nothing — keep the unfiltered result
            return items;
        }

        return items.Where(i => i.ItemId <= 0 || categoryIds.Contains(i.ItemId)).ToList();
    }


    // Extracts the portal's own flick id ("fid") from a request object like {"cmd":"flick","fid":144314}
    public static long? TryExtractItemId(string? requestJson)
    {
        if (string.IsNullOrEmpty(requestJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            return GetLong(doc.RootElement, "fid");
        }
        catch (JsonException)
        {
            return null;
        }
    }


    private static readonly Dictionary<(string Url, int Fid), (HashSet<long> Ids, DateTime LoadedAt)> _categoryIdsCache = new();
    private static readonly object _categoryIdsCacheLock = new();
    private static readonly TimeSpan CategoryIdsCacheTtl = TimeSpan.FromMinutes(30);
    private const int CategoryIdsPageSize = 1000;
    private const int MaxCategoryIdsItems = 200000;

    private async Task<HashSet<long>> GetCategoryItemIdsAsync(PlaylistSource source, string key, int fid, CancellationToken ct)
    {
        var cacheKey = (source.Url ?? string.Empty, fid);
        lock (_categoryIdsCacheLock)
        {
            if (_categoryIdsCache.TryGetValue(cacheKey, out var cached) &&
                (DateTime.UtcNow - cached.LoadedAt) < CategoryIdsCacheTtl)
            {
                return cached.Ids;
            }
        }

        var ids = new HashSet<long>();
        var offset = 0;
        while (offset < MaxCategoryIdsItems)
        {
            var request = $"{{\"key\":{JsonSerializer.Serialize(key)},\"cmd\":\"flicks\",\"fid\":{fid}," +
                          $"\"offset\":{offset},\"limit\":{CategoryIdsPageSize}}}";

            JsonDocument response;
            try
            {
                response = await PostAsync(source, "flicks.json", request, ct);
            }
            catch (System.Text.Json.JsonException ex) when (offset > 0)
            {
                // Server sometimes emits broken JSON deep in pagination — keep partial id set
                _logger.LogWarning(
                    "Портал: id-набор категории fid={Fid} оборван на offset={Offset} (невалидный JSON): {Error}.",
                    fid, offset, ex.Message);
                break;
            }
            using (response)
            {
                var total = GetInt(response.RootElement, "count");
                if (FindArray(response.RootElement, "items") is not { } itemArray)
                {
                    break;
                }

                var added = 0;
                var hasNext = false;
                foreach (var item in itemArray.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (string.Equals(GetString(item, "type"), "next", StringComparison.OrdinalIgnoreCase))
                    {
                        hasNext = true;
                        continue;
                    }

                    if (GetLong(item, "fid") is { } id)
                    {
                        ids.Add(id);
                        added++;
                    }
                }

                if (added == 0 || !hasNext)
                {
                    break;
                }

                offset += added;
                if (total.HasValue && offset >= total.Value)
                {
                    break;
                }
            }
        }

        _logger.LogInformation(
            "Портал: собран id-набор категории fid={Fid}: {Count} элементов.",
            fid, ids.Count);

        lock (_categoryIdsCacheLock)
        {
            _categoryIdsCache[cacheKey] = (ids, DateTime.UtcNow);
        }

        return ids;
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


    private sealed record CategoryPage(List<PortalCatalogItem> Items, int? Total, bool HasNext);

    private async Task<List<PortalCatalogItem>> LoadCategoryAsync(
        PlaylistSource source, string key, string requestJson, string categoryTitle,
        string? genre, CancellationToken ct, bool parallelPages = false)
    {
        if (parallelPages)
        {
            return await LoadCategoryParallelAsync(source, key, requestJson, categoryTitle, genre, ct);
        }

        var result = new List<PortalCatalogItem>();
        var offset = 0;
        var total = (int?)null;
        var pages = 0;

        while (pages++ < MaxPagesPerCategory)
        {
            var page = await FetchCategoryPageAsync(source, key, requestJson, offset, categoryTitle, genre, ct);
            result.AddRange(page.Items);
            total ??= page.Total;

            var added = page.Items.Count;
            if (added == 0 || !page.HasNext)
            {
                return result;
            }

            offset += added;

            if (total.HasValue && offset >= total.Value)
            {
                return result;
            }

            _logger.LogInformation(
                "Портал: категория «{Category}» — загружено {Loaded}/{Total}.", categoryTitle, offset, total);
        }

        _logger.LogWarning(
            "Портал: категория «{Category}» прервана после {MaxPages} страниц (защита от бесконечной пагинации).",
            categoryTitle, MaxPagesPerCategory);
        return result;
    }


    // Filter results can be large; sequential paging was latency-bound, so pages load concurrently
    private static readonly SemaphoreSlim FilterPageSemaphore = new(4);

    private async Task<List<PortalCatalogItem>> LoadCategoryParallelAsync(
        PlaylistSource source, string key, string requestJson, string categoryTitle,
        string? genre, CancellationToken ct)
    {
        var first = await FetchCategoryPageAsync(source, key, requestJson, 0, categoryTitle, genre, ct);
        var total = first.Total;
        if (first.Items.Count == 0 || !first.HasNext || total is not > PageSize)
        {
            return first.Items;
        }

        var pageCount = (int)Math.Ceiling(total.Value / (double)PageSize);
        if (pageCount > MaxPagesPerCategory)
        {
            pageCount = MaxPagesPerCategory;
        }

        var pages = new List<PortalCatalogItem>[pageCount];
        pages[0] = first.Items;
        var tasks = new Task[pageCount - 1];
        for (var p = 1; p < pageCount; p++)
        {
            var offset = p * PageSize;
            var index = p;
            tasks[p - 1] = Task.Run(async () =>
            {
                await FilterPageSemaphore.WaitAsync(ct);
                try
                {
                    pages[index] =
                        (await FetchCategoryPageAsync(source, key, requestJson, offset, categoryTitle, genre, ct)).Items;
                }
                finally
                {
                    FilterPageSemaphore.Release();
                }
            }, ct);
        }

        await Task.WhenAll(tasks);

        var result = new List<PortalCatalogItem>(total.Value);
        foreach (var page in pages)
        {
            if (page is { Count: > 0 })
            {
                result.AddRange(page);
            }
        }

        return result;
    }

    private async Task<CategoryPage> FetchCategoryPageAsync(
        PlaylistSource source, string key, string requestJson, int offset,
        string categoryTitle, string? genre, CancellationToken ct)
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
                "(часто последняя пустая страница): {Error}.",
                categoryTitle, ex.Message);
            return new CategoryPage(new List<PortalCatalogItem>(), null, false);
        }

        using (response)
        {
            var total = GetInt(response.RootElement, "count");
            if (FindArray(response.RootElement, "items") is not { } itemArray)
            {
                _logger.LogWarning("Портал: категория «{Category}» — в ответе нет массива items.", categoryTitle);
                return new CategoryPage(new List<PortalCatalogItem>(), total, false);
            }

            var (items, hasNext) = ParseCategoryItems(itemArray, categoryTitle, genre);
            return new CategoryPage(items, total, hasNext);
        }
    }

    private static (List<PortalCatalogItem> Items, bool HasNext) ParseCategoryItems(
        JsonElement itemArray, string categoryTitle, string? genre)
    {
        var items = new List<PortalCatalogItem>();
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

            items.Add(new PortalCatalogItem
            {
                Name = name!,
                Group = categoryTitle,
                LogoUrl = GetString(item, "img") ?? GetString(item, "imglr"),
                StreamUrl = GetString(item, "url"),
                RequestJson = GetObjectAsJson(item, "request") ?? string.Empty,
                Description = GetString(item, "description"),
                Year = year ?? 0,
                Genre = genre,
                ItemId = GetLong(item, "fid") ?? 0
            });
        }

        return (items, hasNext);
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

        var manifest = await PostAsync(source, "manifest.json", $"{{\"key\":{JsonSerializer.Serialize(key)}}}", ct);

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

    private static long? GetLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var l)
            ? l
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
