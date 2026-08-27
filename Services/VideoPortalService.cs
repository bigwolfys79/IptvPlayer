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

    /// <summary>Жанр из фильтра manifest (null — жанр не определён).</summary>
    public string? Genre { get; set; }
}

/// <summary>Элемент фильтра жанров из manifest.controls.filters.</summary>
public class PortalGenreFilter
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FilterRequestJson { get; set; } = string.Empty;
}

/// <summary>Категория видео-портала из manifest (fid → название типа контента).</summary>
public class PortalCategoryInfo
{
    public int Fid { get; set; }
    public string Title { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty;
}

/// <summary>
/// Элемент фильтра годов из manifest.controls.filters.
/// Title — то, что видит пользователь в комбобоксе (например,
/// «2024» или «2021-2026»). YearsValue — строка, передаваемая
/// в request.years на сервер (совпадает с Title для этого фильтра).
/// </summary>
public class PortalYearFilter
{
    public string Title { get; set; } = string.Empty;
    public string YearsValue { get; set; } = string.Empty;
}

/// <summary>Результат загрузки каталога: элементы + жанры + request JSON категорий.</summary>
public class PortalCatalogLoadResult
{
    public List<PortalCatalogItem> Items { get; set; } = new();
    public List<PortalGenreFilter> Genres { get; set; } = new();
    public Dictionary<int, string> CategoryRequests { get; set; } = new();
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
    /// Загружает жанры, года и все категории из manifest для серверных фильтров.
    /// Возвращает (genreFilters, yearFilters, categories) — категории с fid и заголовками.
    /// </summary>
    Task<(List<PortalGenreFilter> Genres, List<PortalYearFilter> Years, List<PortalCategoryInfo> Categories)> LoadManifestInfoAsync(PlaylistSource source, CancellationToken ct = default);

    /// <summary>
    /// Загружает одну категорию с фильтром по жанру (по запросу пользователя).
    /// Вызывается при выборе жанра в ComboBox.
    /// </summary>
    Task<List<PortalCatalogItem>> LoadCategoryByGenreAsync(
        PlaylistSource source, string categoryRequestJson, int genreId, string genreTitle,
        CancellationToken ct = default);

    /// <summary>
    /// Загружает одну категорию с фильтром по году (по запросу пользователя).
    /// yearOrRange: "2025" или "2021-2026".
    /// </summary>
    Task<List<PortalCatalogItem>> LoadCategoryByYearAsync(
        PlaylistSource source, string categoryRequestJson, string yearOrRange,
        CancellationToken ct = default);

    /// <summary>
    /// Загружает одну категорию с комбинированным фильтром по жанру и году.
    /// </summary>
    Task<List<PortalCatalogItem>> LoadCategoryByGenreAndYearAsync(
        PlaylistSource source, string categoryRequestJson, int genreId, string genreTitle,
        string yearOrRange, CancellationToken ct = default);

    /// <summary>
    /// Прямой запрос фильтра (без categoryRequestJson): строит запрос из
    /// fid категории и параметров фильтра. Используется при смене фильтра
    /// жанра/года в UI — вместо загрузки всего каталога.
    /// </summary>
    Task<List<PortalCatalogItem>> LoadFilteredAsync(
        PlaylistSource source, int fid, int? genreId, string? yearOrRange,
        CancellationToken ct = default);

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

        var genres = ParseGenreFilters(manifest.RootElement);
        _logger.LogInformation("Портал: жанров из manifest: {Count}.", genres.Count);

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

            using (var requestDoc = JsonDocument.Parse(requestJson))
            {
                var fid = GetInt(requestDoc.RootElement, "fid");
                if (fid == 10001)
                {
                    _logger.LogInformation("Портал: категория «{Category}» пропущена (история просмотров).", categoryTitle);
                    continue;
                }
            }

            await LoadCategoryAsync(source, key, requestJson, categoryTitle, null, result, ct);
        }

        _logger.LogInformation("Портал {Url}: каталог загрушен, элементов: {Count}.", SecretProtector.Mask(source.Url), result.Count);
        return result;
    }

    /// <summary>
    /// Загружает жанры и все категории из manifest для серверных фильтров.
    /// Возвращает (genreFilters, categories) — категории с fid и заголовками.
    /// </summary>
    public async Task<(List<PortalGenreFilter> Genres, List<PortalYearFilter> Years, List<PortalCategoryInfo> Categories)> LoadManifestInfoAsync(
        PlaylistSource source, CancellationToken ct = default)
    {
        var key = NormalizeKey(source);

        using var manifest = await PostAsync(source, "manifest.json", $"{{\"key\":\"{key}\"}}", ct);
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
                    Title = title ?? L.T("Без категории", "Uncategorized"),
                    RequestJson = requestJson
                });
            }
        }

        return (genres, years, categories);
    }

    /// <summary>Загружает одну категорию с фильтром по жанру (по запросу пользователя).</summary>
    public async Task<List<PortalCatalogItem>> LoadCategoryByGenreAsync(
        PlaylistSource source, string categoryRequestJson, int genreId, string genreTitle,
        CancellationToken ct = default)
    {
        var key = NormalizeKey(source);
        var result = new List<PortalCatalogItem>();

        var genreRequest = MergeFields(categoryRequestJson, new Dictionary<string, JsonElement>
        {
            ["filter"] = JsonSerializer.SerializeToElement("on"),
            ["genre"] = JsonSerializer.SerializeToElement(genreId)
        });

        await LoadCategoryAsync(source, key, genreRequest, genreTitle, genreTitle, result, ct);
        return result;
    }

    /// <summary>Загружает одну категорию с фильтром по году (по запросу пользователя).</summary>
    public async Task<List<PortalCatalogItem>> LoadCategoryByYearAsync(
        PlaylistSource source, string categoryRequestJson, string yearOrRange,
        CancellationToken ct = default)
    {
        var key = NormalizeKey(source);
        var result = new List<PortalCatalogItem>();

        var yearRequest = MergeFields(categoryRequestJson, new Dictionary<string, JsonElement>
        {
            ["filter"] = JsonSerializer.SerializeToElement("on"),
            ["years"] = JsonSerializer.SerializeToElement(yearOrRange)
        });

        await LoadCategoryAsync(source, key, yearRequest, yearOrRange, null, result, ct);
        return result;
    }

    /// <summary>Загружает одну категорию с комбинированным фильтром по жанру и году.</summary>
    public async Task<List<PortalCatalogItem>> LoadCategoryByGenreAndYearAsync(
        PlaylistSource source, string categoryRequestJson, int genreId, string genreTitle,
        string yearOrRange, CancellationToken ct = default)
    {
        var key = NormalizeKey(source);
        var result = new List<PortalCatalogItem>();

        var combinedRequest = MergeFields(categoryRequestJson, new Dictionary<string, JsonElement>
        {
            ["filter"] = JsonSerializer.SerializeToElement("on"),
            ["genre"] = JsonSerializer.SerializeToElement(genreId),
            ["years"] = JsonSerializer.SerializeToElement(yearOrRange)
        });

        var label = $"{genreTitle} ({yearOrRange})";
        await LoadCategoryAsync(source, key, combinedRequest, label, genreTitle, result, ct);
        return result;
    }

    /// <summary>
    /// Прямой запрос фильтра (без categoryRequestJson): строит запрос из
    /// fid категории и параметров фильтра. Используется при смене фильтра
    /// жанра/года в UI — вместо загрузки всего каталога.
    /// </summary>
    public async Task<List<PortalCatalogItem>> LoadFilteredAsync(
        PlaylistSource source, int fid, int? genreId, string? yearOrRange,
        CancellationToken ct = default)
    {
        var key = NormalizeKey(source);
        var result = new List<PortalCatalogItem>();

        // Согласно документации OttPlayer (genre.md §3 vs §5/§9),
        // сервер различает ДВА режима запросов по составу полей:
        //
        //   • Категория (без фильтра):  {key, cmd:"flicks", fid:N, offset, limit}
        //     — возвращает ВСЕ элементы категории.
        //
        //   • Фильтр:                    {key, filter:"on", genre?:G, years?:"Y", offset, limit}
        //     — БЕЗ cmd и fid. Сервер использует сессионный контекст
        //       (предыдущий flicks-запрос категории) и применяет фильтр.
        //
        // Раньше мы отправляли cmd+fid+filter в одном теле — сервер
        // воспринимал это как запрос категории и молча игнорировал
        // filter/genre/years. Лог подтверждал: при выборе жанра «ужасы»
        // сервер возвращал те же 12938 элементов, что и без фильтра.
        string filterRequest;
        var hasYear = !string.IsNullOrEmpty(yearOrRange);
        var hasGenre = genreId.HasValue;

        if (hasGenre || hasYear)
        {
            // Режим фильтра — тело без cmd и fid (документация §5/§9).
            // Локальная переменная genreValue достаётся через GetValueOrDefault()
            // — это безопасный доступ к Nullable<int> без предупреждения CS8629
            // (GetValueOrDefault документированно возвращает default(int)=0,
            // если HasValue=false, но мы используем genreValue только когда
            // hasGenre=true, проверив это выше).
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
            // Фильтр не выбран — обычная загрузка категории (документация §3).
            // Это, по сути, повтор того, что делает LoadCatalogAsync на старте,
            // но только для одной выбранной категории fid.
            filterRequest = $"{{\"key\":\"{key}\",\"cmd\":\"flicks\",\"fid\":{fid},\"offset\":0,\"limit\":0}}";
        }

        var label = BuildFilterLabel(genreId, yearOrRange);
        _logger.LogInformation(
            "Портал: запрос фильтра — genre={Genre}, year={Year}, fid={Fid}, mode={Mode}.",
            genreId?.ToString() ?? "-", yearOrRange ?? "-", fid,
            (hasGenre || hasYear) ? "filter" : "category");

        // GetValueOrDefault безопасно достаёт значение из Nullable<int>;
        // мы передаём его в GetGenreTitle только когда hasGenre=true.
        await LoadCategoryAsync(source, key, filterRequest, label,
            hasGenre ? GetGenreTitle(genreId.GetValueOrDefault()) : null, result, ct);
        return result;
    }

    private static string BuildFilterLabel(int? genreId, string? yearOrRange)
    {
        var parts = new List<string>();
        // GetValueOrDefault безопасно достаёт значение из Nullable<int>
        // без предупреждения CS8629 (используем только когда HasValue).
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

    /// <summary>Категории, для которых стоит загружать жанры (fid=1 фильмы, fid=2 сериалы).</summary>
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
        var seenFids = new HashSet<int>();
        foreach (var genre in genres)
        {
            if (ct.IsCancellationRequested) break;

            var genreRequest = MergeFields(requestJson, new Dictionary<string, JsonElement>
            {
                ["filter"] = JsonSerializer.SerializeToElement("on"),
                ["genre"] = JsonSerializer.SerializeToElement(genre.Id)
            });

            var countBefore = result.Count;
            await LoadCategoryAsync(source, key, genreRequest, categoryTitle, genre.Title, result, ct);

            for (var i = countBefore; i < result.Count; i++)
            {
                var item = result[i];
                if (string.IsNullOrEmpty(item.Genre))
                {
                    item.Genre = genre.Title;
                }

                if (!string.IsNullOrEmpty(item.RequestJson))
                {
                    using var reqDoc = JsonDocument.Parse(item.RequestJson);
                    if (GetInt(reqDoc.RootElement, "fid") is { } itemFid)
                    {
                        seenFids.Add(itemFid);
                    }
                }
            }

            _logger.LogInformation(
                "Портал: категория «{Category}» — жанр «{Genre}»: {Count} элементов.",
                categoryTitle, genre.Title, result.Count - countBefore);
        }

        var allGenreRequest = MergeKey(requestJson, key);
        var beforeAll = result.Count;
        await LoadCategoryAsync(source, key, allGenreRequest, categoryTitle, null, result, ct);

        var added = 0;
        for (var i = beforeAll; i < result.Count; i++)
        {
            if (!string.IsNullOrEmpty(result[i].RequestJson))
            {
                using var reqDoc = JsonDocument.Parse(result[i].RequestJson);
                if (GetInt(reqDoc.RootElement, "fid") is { } itemFid && seenFids.Contains(itemFid))
                {
                    result.RemoveAt(i);
                    i--;
                    continue;
                }
            }

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

            // OttPlayer-сервер иногда возвращает битый JSON на последней
            // пустой странице пагинации — массив items открывается запятой
            // без первого элемента: "items":[,{"type":"next",...}].
            // System.Text.Json в этом случае бросает JsonReaderException,
            // и без try/catch исключение пробрасывалось до самого верха
            // (LoadFilteredFromServerAsync), теряя ВСЕ уже загруженные на
            // предыдущих страницах элементы. Ловим здесь и выходим из
            // пагинации, сохраняя накопленный результат — пользователь
            // получит 427/435 мюзиклов вместо 0.
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
            } // end using (response)
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

    /// <summary>
    /// Эндпоинт команды по телу запроса:
    ///   • {"cmd":"flicks",...}      → flicks.json  (загрузка категории, §3)
    ///   • {"filter":"on",...}        → flicks.json  (фильтр, §5/§9 — без cmd)
    ///   • {"cmd":"flick",...}        → flick.json   (один элемент, §4)
    ///   • {"cmd":"search",...}       → search.json  (поиск, §8)
    ///   • прочее                     → manifest.json
    /// </summary>
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

            // Запрос фильтра не содержит "cmd", но имеет "filter":"on".
            // По документации OttPlayer это тоже идёт на flicks.json —
            // сервер различает режимы по составу полей тела, а не по URL.
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

    private static readonly string DumpDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IptvPlayer", "portal_dump");

    private async Task<JsonDocument> PostAsync(
        PlaylistSource source, string endpoint, string bodyJson, CancellationToken ct)
    {
        // Трафик портала — не видео: замер скорости чтения процесса на время
        // запроса замораживается, как у XmlTvService.
        using var pause = _speedMonitor.PauseScope();

        var url = BuildUrl(source.Url, endpoint);
        _logger.LogInformation("Портал → POST {Url} тело: {Body}", SecretProtector.Mask(url),
            Truncate(SecretProtector.Mask(bodyJson)));

        using var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        _logger.LogInformation("Портал ← {Url}: {Body}", url, Truncate(body));

        DumpJson(endpoint, SecretProtector.Mask(bodyJson), body);

        return JsonDocument.Parse(body);
    }

    private static void DumpJson(string endpoint, string requestJson, string responseJson)
    {
        try
        {
            Directory.CreateDirectory(DumpDir);
            var safeName = string.Concat(endpoint.Where(c => char.IsLetterOrDigit(c) || c == '_'));
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path = Path.Combine(DumpDir, $"{timestamp}_{safeName}.json");
            var text = $"// REQUEST: {endpoint}\n// BODY:\n{requestJson}\n\n// RESPONSE:\n{responseJson}\n";
            File.WriteAllText(path, text, Encoding.UTF8);
        }
        catch
        {
            // Дамп не критичен — не падаем
        }
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

    // ===================== Парсинг жанров =====================

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

    // ===================== Парсинг годов =====================

    /// <summary>
    /// Извлекает список годов/диапазонов из manifest.controls.filters,
    /// где filter.title == "Год". Каждый элемент описан как
    /// { title: "2024" | "2021-2026", request: { filter: "on", years: "..." } }.
    /// Title используется как подпись в комбобоксе, YearsValue —
    /// как значение request.years при загрузке отфильтрованной категории.
    /// </summary>
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

                // YearsValue лежит в request.years; для этого фильтра он
                // совпадает с title («2024» или «2021-2026»). Если поле
                // вдруг отсутствует — используем title как запасной вариант.
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
