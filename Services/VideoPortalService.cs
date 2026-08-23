using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IptvPlayer.Models;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;

/// <summary>
/// Элемент каталога видео-портала: фильм/сериал одной строкой. Group —
/// название категории портала (становится группой в фильтре каналов).
/// StreamUrl заполнен у фильмов (type "stream" — сервер сразу даёт master.m3u8);
/// у сериалов (type "multistream") ссылки нет — поток запрашивается по клику
/// командой flick (берётся первый сезон/эпизод). RequestJson — прозрачный
/// request-объект из ответа API, передаётся серверу как есть.
/// </summary>
public class PortalCatalogItem
{
    public string Name { get; set; } = string.Empty;
    public string? Group { get; set; }
    public string? LogoUrl { get; set; }
    public string? StreamUrl { get; set; }
    public string RequestJson { get; set; } = string.Empty;

    /// <summary>Описание из каталога (может отсутствовать у части элементов).</summary>
    public string? Description { get; set; }

    /// <summary>Год выпуска (0 — не указан). Используется сортировкой каталога.</summary>
    public int Year { get; set; }
}

/// <summary>
/// Результат запроса потока: основная ссылка (авто-качество) и варианты
/// качества портала ({"480":url,"720":url,"1080":url,"auto":url} из ответа
/// flick; может отсутствовать — тогда выбор качества недоступен).
/// </summary>
public class PortalStreamResult
{
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string> Variants { get; set; } = new();
}

/// <summary>Один эпизод сериала (или единственный фильм) из ответа flick.</summary>
public class PortalEpisode
{
    public string Title { get; set; } = string.Empty;
    public string StreamUrl { get; set; } = string.Empty;
    public Dictionary<string, string> Variants { get; set; } = new();

    /// <summary>request-объект эпизода (прозрачная команда, если URL устареет).</summary>
    public string RequestJson { get; set; } = string.Empty;
}

/// <summary>
/// Разобранный ответ flick: эпизоды (у фильма — один) плюс шапка сериала
/// (название/описание/постер приходят в корне ответа и переиспользуются
/// диалогом выбора серий).
/// </summary>
public class PortalFlickResult
{
    public string SerialTitle { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? PosterUrl { get; set; }
    public List<PortalEpisode> Episodes { get; set; } = new();
}

public interface IVideoPortalService
{
    /// <summary>
    /// Загружает весь каталог портала: manifest → категории → страницы
    /// элементов по каждой категории (по 300, столько отдаёт сервер).
    /// Сетевые запросы — только здесь и в ResolveStreamAsync; кэширование
    /// делает вызывающий (MainPage через PlaylistCacheService, как для M3U).
    /// </summary>
    Task<List<PortalCatalogItem>> LoadCatalogAsync(PlaylistSource source, CancellationToken ct = default);

    /// <summary>
    /// Запрашивает у портала эпизоды элемента (команда flick): сериалу возвращает
    /// список серий с готовыми ссылками (у фильма — один элемент с вариантами
    /// качества). Вызывается при клике; ссылки одноразовые, не кэшируются.
    /// </summary>
    Task<PortalFlickResult> ResolveEpisodesAsync(PlaylistSource source, string requestJson, CancellationToken ct = default);
}

/// <summary>
/// Клиент видео-портала (источник типа "portal"). Протокол изучен по живому
/// серверу: ключ передаётся ПОЛЕМ "key" в теле каждого POST-запроса
/// (без query-параметров), команда определяет эндпоинт — {cmd}.json:
///   manifest: {"key":K} → {type:"videoportal", items:[{type:"category",
///             title, request:{cmd:"flicks", fid, offset, limit, ...}}]}
///   flicks:   {key, cmd:"flicks", fid, offset, limit} → {type:"category",
///             count:N, items:[...30..300 записей..., {type:"next"}]}
///   элемент:  {type:"stream"|"multistream", title, img, fid,
///             url (у фильмов), request:{cmd:"flick", fid} (у сериалов)}
///   flick:    {key, cmd:"flick", fid} → {type:"multistream", items:[{type:
///             "stream", url, ...}] — сезоны/эпизоды; первый = по умолчанию}
/// Каждый запрос/ответ логируется (с обрезкой до 8 КБ) — протокол развивается
/// без переделки клиента: неизвестные поля игнорируются, request-объекты
/// передаются как есть.
/// </summary>
public class VideoPortalService : IVideoPortalService
{
    private const int MaxLoggedChars = 8192;
    private const int PageSize = 300;
    private const int MaxPagesPerCategory = 500;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly ProcessSpeedMonitor _speedMonitor;
    private readonly HttpClient _httpClient;
    private readonly ILogger<VideoPortalService> _logger;

    public VideoPortalService(
        ProcessSpeedMonitor speedMonitor,
        ILogger<VideoPortalService> logger,
        HttpClient? httpClient = null)
    {
        _speedMonitor = speedMonitor;
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

        using var manifest = await PostAsync(source, "manifest.json", $"{{\"key\":\"{key}\"}}", ct);

        var categories = FindArray(manifest.RootElement, "items");
        if (categories is not { } categoryArray)
        {
            _logger.LogWarning(
                "Портал {Url}: в manifest нет массива items — каталог пуст (см. лог ответа выше).", source.Url);
            return result;
        }

        foreach (var category in categoryArray.EnumerateArray())
        {
            if (category.ValueKind != JsonValueKind.Object ||
                !string.Equals(GetString(category, "type"), "category", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var categoryTitle = GetString(category, "title") ?? L.T("Без категории", "Uncategorized");
            var requestJson = GetObjectAsJson(category, "request");
            if (requestJson == null)
            {
                _logger.LogWarning("Портал: категория «{Category}» без request-объекта — пропущена.", categoryTitle);
                continue;
            }

            // «Продолжить просмотр» (fid 10001) — персональная история
            // просмотров: её элементы дублируют фильмы из обычных категорий
            // и засоряют поиск.
            using (var requestDoc = JsonDocument.Parse(requestJson))
            {
                if (GetInt(requestDoc.RootElement, "fid") == 10001)
                {
                    _logger.LogInformation("Портал: категория «{Category}» пропущена (история просмотров).", categoryTitle);
                    continue;
                }
            }

            await LoadCategoryAsync(source, key, requestJson, categoryTitle, result, ct);
        }

        _logger.LogInformation("Портал {Url}: каталог загружен, элементов: {Count}.", source.Url, result.Count);
        return result;
    }

    private async Task LoadCategoryAsync(
        PlaylistSource source, string key, string requestJson, string categoryTitle,
        List<PortalCatalogItem> result, CancellationToken ct)
    {
        var offset = 0;
        var total = (int?)null;
        var pages = 0;

        while (pages++ < MaxPagesPerCategory)
        {
            var pageRequest = MergeKey(WithPaging(requestJson, offset, PageSize), key);
            using var response = await PostAsync(source, CommandEndpoint(pageRequest), pageRequest, ct);

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
                    continue; // Вложенные категории не замечены — на всякий случай.
                }

                var name = GetString(item, "title");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // Жанров API портала не отдаёт (фильтры из manifest сервер
                // игнорирует), единственная классификация элемента — год.
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
                    Year = year ?? 0
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

        // У сериала эпизоды — в items[] (type "stream" с готовым url); у фильма
        // items нет — единственный поток лежит в корне ответа.
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
                    Title = GetString(item, "title") ?? L.T("Эпизод", "Episode"),
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
                Title = result.SerialTitle is { Length: > 0 } t ? t : L.T("Воспроизвести", "Play"),
                StreamUrl = rootUrl,
                Variants = variants,
                RequestJson = requestJson
            });
        }

        if (result.Episodes.Count == 0)
        {
            throw new InvalidOperationException(L.T(
                "Портал не вернул ссылку на поток (см. лог).",
                "Portal returned no stream URL (see log)."));
        }

        return result;
    }

    /// <summary>Копирует объект variants (качество → url), если он есть в ответе.</summary>
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

    // ===================== Протокол =====================

    /// <summary>
    /// Ключ в коротком виде ("6ee2c415..."): пользователь мог вставить и
    /// полный формат "portal::[key:6ee2c415...]" — обёртку снимаем.
    /// </summary>
    private static string NormalizeKey(PlaylistSource source)
    {
        if (string.IsNullOrWhiteSpace(source.PortalKey))
        {
            throw new InvalidOperationException(L.T(
                "У источника-портала не задан ключ доступа.", "Portal source has no access key."));
        }

        var key = source.PortalKey.Trim();
        var prefix = "portal::[key:";
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key.EndsWith(']'))
        {
            key = key[prefix.Length..^1];
        }

        return key;
    }

    /// <summary>Эндпоинт команды: request вида {"cmd":"flicks",...} → flicks.json.</summary>
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
        }
        catch (JsonException)
        {
        }

        return "manifest.json";
    }

    /// <summary>Подменяет offset/limit в request-объекте (пагинация категорий).</summary>
    private static string WithPaging(string requestJson, int offset, int limit) =>
        MergeFields(requestJson, new Dictionary<string, JsonElement>
        {
            ["offset"] = JsonSerializer.SerializeToElement(offset),
            ["limit"] = JsonSerializer.SerializeToElement(limit)
        });

    /// <summary>Добавляет в request-объект поле "key" (авторизация каждого запроса).</summary>
    private static string MergeKey(string requestJson, string key) =>
        MergeFields(requestJson, new Dictionary<string, JsonElement>
        {
            ["key"] = JsonSerializer.SerializeToElement(key)
        });

    /// <summary>
    /// Пересобирает request-объект с заменёнными полями. JsonElement
    /// сериализуется с исходными именами полей — протокол не искажается.
    /// </summary>
    private static string MergeFields(string requestJson, Dictionary<string, JsonElement> overrides)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var rewritten = doc.RootElement.EnumerateObject()
                .Where(p => !overrides.ContainsKey(p.Name))
                .ToDictionary(p => p.Name, p => p.Value);
            foreach (var (name, value) in overrides)
            {
                rewritten[name] = value;
            }

            return JsonSerializer.Serialize(rewritten, JsonOptions);
        }
        catch (JsonException)
        {
            return requestJson;
        }
    }

    private async Task<JsonDocument> PostAsync(
        PlaylistSource source, string endpoint, string bodyJson, CancellationToken ct)
    {
        // Трафик портала — не видео: замер скорости чтения процесса на время
        // запроса замораживается, как у XmlTvService.
        using var pause = _speedMonitor.PauseScope();

        var url = BuildUrl(source.Url, endpoint);
        _logger.LogInformation("Портал → POST {Url} тело: {Body}", url, Truncate(bodyJson));

        using var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("Портал ← {Url}: {Body}", url, Truncate(body));

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

    // ===================== Мягкий разбор ответа =====================

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
}
