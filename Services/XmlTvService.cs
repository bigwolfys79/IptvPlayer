using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using IptvPlayer.Models;
using Microsoft.Extensions.Logging;

namespace IptvPlayer.Services;

/// <summary>
/// Скачивает и парсит один XMLTV-источник. Формат programme@start/@stop —
/// "yyyyMMddHHmmss zzz" (например "20260814120000 +0300"), опционально без
/// пробела перед смещением или вовсе без смещения (тогда считаем локальным).
///
/// Кэш — дисковый (EpgCacheStore, MemoryPack+Brotli) с TTL внутри
/// обёртки CachedXmlTv: TTL проверяется здесь, а не в самом хранилище.
/// </summary>
public class XmlTvService : IXmlTvService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(3);

    // Окно, за пределами которого программы не нужны ни одному экрану
    // приложения (текущая программа, ближайшие N дней в EPG-сетке). Раньше
    // ParseXmlTv разбирал и держал в памяти ВСЕ программы источника (в логе
    // видно: 124819 + 380450 = 505129 штук на 2 источника), хотя реально
    // используется в разы меньше. Идея окна взята из EpgService другого
    // проекта — там фильтрация стоит прямо в цикле XmlReader, до разбора
    // title/desc, что и даёт основную экономию (не тратим время на текстовые
    // поля программ, которые всё равно отбросим).
    //
    // DaysBack = 3 синхронизирован с окном EpgViewModel.BackwardSpan (72ч):
    // с появлением timeshift-архива прошедшие передачи стали нужны не только
    // для справки — по ним теперь можно кликнуть и запустить воспроизведение
    // с начала, поэтому сутки "в прошлое" перестали хватать.
    private const int DaysBack = 3;
    private const int DaysAhead = 3;

    private readonly HttpClient _httpClient;
    private readonly ILogger<XmlTvService> _logger;

    public XmlTvService(
        ILogger<XmlTvService> logger,
        HttpClient? httpClient = null)
    {
        _logger = logger;
        _httpClient = httpClient ?? CreateDefaultHttpClient();
    }

    /// <summary>
    /// Раньше HttpClient создавался как "new HttpClient()" совсем без
    /// заголовков. Часть раздатчиков XMLTV (в т.ч. epg.one) отдают 403 или
    /// пустой ответ на запросы без User-Agent, приняв их за бот/скрипт —
    /// такой запрос падал на EnsureSuccessStatusCode() внутри DownloadAsync,
    /// EPGService ловил исключение на уровне источника и просто пропускал
    /// его (см. EPGService.EnsureEpgLoadedAsync), так что EPG по этому
    /// источнику молча оставался пустым. Плюс дефолтный Timeout в 100 секунд
    /// может не хватать на большие фиды (например russia3.xml на
    /// медленном канале) — увеличиваем его здесь же.
    /// </summary>
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

    public async Task<XmlTvLoadResult> LoadAsync(EPGSource source, TimeSpan? maxAge = null, CancellationToken ct = default)
    {
        var cacheKey = $"xmltv:{source.Url}";

        // Быстрый бинарный кэш (MemoryPack+Brotli).
        var cached = await EpgCacheStore.ReadAsync(cacheKey);

        if (cached != null && IsCacheFresh(cached, maxAge))
        {
            if (maxAge is { } age && age != TimeSpan.MaxValue)
            {
                _logger.LogInformation(
                    "Источник {Url}: кэш свежий (возраст {Age:F1} ч при лимите {Limit:F0} дн.) — без скачивания.",
                    source.Url, (DateTime.UtcNow - GetSavedAtUtc(cached)).TotalHours, age.TotalDays);
            }

            return new XmlTvLoadResult
            {
                Entries = cached.Entries,
                ChannelIcons = cached.ChannelIcons,
                DataSavedAtUtc = GetSavedAtUtc(cached)
            };
        }

        // Скачивание (DownloadAsync) — настоящее async I/O, оно UI-поток не
        // блокирует. А вот распаковка GZip и разбор XML внутри ParseXmlTv —
        // синхронная CPU-нагрузка; без Task.Run она выполнялась бы прямо на
        // потоке вызывающего (при клике на "Обновить EPG" — на UI-потоке) и
        // морозила интерфейс на время парсинга большого XMLTV-файла.
        XmlTvLoadResult parsed;
        var now = DateTime.Now;
        var windowStart = now.Date.AddDays(-DaysBack);
        var windowEnd = now.AddDays(DaysAhead + 1);
        var dataSavedAtUtc = DateTime.UtcNow;
        await using (System.IO.Stream stream = await DownloadAsync(source.Url, ct))
        {
            parsed = await Task.Run(() => ParseXmlTv(stream, windowStart, windowEnd), ct);
        }

        _logger.LogInformation(
            "Источник {Url}: распарсено программ: {Programs}, иконок каналов: {Icons}.",
            source.Url, parsed.Entries.Count, parsed.ChannelIcons.Count);

        await EpgCacheStore.WriteAsync(cacheKey, new CachedXmlTv
        {
            Entries = parsed.Entries,
            ChannelIcons = parsed.ChannelIcons,
            SavedAtUtc = dataSavedAtUtc,
            ExpiresAt = DateTime.UtcNow.Add(CacheTtl)
        });

        return new XmlTvLoadResult
        {
            Entries = parsed.Entries,
            ChannelIcons = parsed.ChannelIcons,
            DataSavedAtUtc = dataSavedAtUtc
        };
    }

    private async Task<System.IO.Stream> DownloadAsync(string url, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStreamAsync(ct);

        var buffer = new MemoryStream();
        await raw.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        // Раньше "гзипованность" определялась исключительно по тому, что URL
        // заканчивается на ".gz" — а на практике многие провайдеры отдают
        // сжатый XMLTV по адресу без такого расширения (например, ссылка на
        // php-скрипт с параметрами) либо наоборот отдают обычный .xml без
        // сжатия. Определяем по магическим байтам gzip (0x1F 0x8B) — это
        // надёжно независимо от того, что написано в URL.
        var bytes = buffer.GetBuffer();
        var isGzip = buffer.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;
        if (!isGzip)
        {
            return buffer;
        }

        var decompressed = new MemoryStream();
        await using (var gzip = new GZipStream(buffer, CompressionMode.Decompress))
        {
            await gzip.CopyToAsync(decompressed, ct);
        }
        decompressed.Position = 0;
        return decompressed;
    }

    /// <summary>
    /// Момент сохранения кэш-записи. Записи, созданные до появления поля
    /// SavedAtUtc, хранят только ExpiresAt (UtcNow + 3ч на момент записи) —
    /// для них время сохранения восстановимо как ExpiresAt - CacheTtl.
    /// </summary>
    private static DateTime GetSavedAtUtc(CachedXmlTv cached)
    {
        if (cached.SavedAtUtc != default)
        {
            return cached.SavedAtUtc;
        }

        if (cached.ExpiresAt == default)
        {
            return default;
        }

        return cached.ExpiresAt - CacheTtl;
    }

    private static bool IsCacheFresh(CachedXmlTv cached, TimeSpan? maxAge)
    {
        var savedAtUtc = GetSavedAtUtc(cached);

        // Битая запись без вменяемых меток времени — перекачиваем.
        if (savedAtUtc == default)
        {
            return false;
        }

        var age = DateTime.UtcNow - savedAtUtc;

        if (maxAge is { } limit)
        {
            return limit == TimeSpan.MaxValue || age < limit;
        }

        // maxAge не задан — прежнее поведение: фиксированный 3-часовой TTL.
        return cached.ExpiresAt > DateTime.UtcNow;
    }

    private static XmlTvLoadResult ParseXmlTv(System.IO.Stream stream, DateTime windowStart, DateTime windowEnd)
    {
        var result = new List<EPGEntry>();
        var channelNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var channelIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreWhitespace = true
        };

        using var reader = XmlReader.Create(stream, readerSettings);

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.Name == "channel")
            {
                ReadChannel(reader, channelNames, channelIcons);
            }
            else if (reader.Name == "programme")
            {
                var entry = ReadProgramme(reader, channelNames, windowStart, windowEnd);
                if (entry != null)
                {
                    result.Add(entry);
                }
            }
        }

        return new XmlTvLoadResult { Entries = result, ChannelIcons = channelIcons };
    }

    private static void ReadChannel(
        XmlReader reader, Dictionary<string, string> channelNames, Dictionary<string, string> channelIcons)
    {
        var id = reader.GetAttribute("id");

        // Без ReadSubtree по той же причине, что и в ReadProgramme: чтение
        // контента на subtree-ридере съедает следующих соседей — icon после
        // display-name никогда не дочитывался. Выход ровно на </channel>.
        var channelDepth = reader.Depth;
        string? displayName = null;

        if (!reader.IsEmptyElement)
        {
            while (reader.Depth > channelDepth || reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.Name == "display-name" && displayName == null)
                    {
                        displayName = reader.ReadElementContentAsString();
                        continue;
                    }

                    if (reader.Name == "icon")
                    {
                        // <icon src="http://epg.one/img/8900.png" /> — лого канала из
                        // самого XMLTV-источника. Используется как резервный источник
                        // LogoUrl для каналов без tvg-logo в плейлисте (см.
                        // EPGService.ApplyMissingLogosAsync).
                        var src = reader.GetAttribute("src");
                        if (!string.IsNullOrWhiteSpace(src) && !string.IsNullOrEmpty(id))
                        {
                            channelIcons.TryAdd(id, src.Trim());
                        }
                    }
                }

                if (!reader.Read())
                {
                    break;
                }
            }
        }

        if (!string.IsNullOrEmpty(id))
        {
            channelNames[id] = displayName ?? id;
        }
    }

    private static EPGEntry? ReadProgramme(
        XmlReader reader, Dictionary<string, string> channelNames, DateTime windowStart, DateTime windowEnd)
    {
        var channelId = reader.GetAttribute("channel");
        var startRaw = reader.GetAttribute("start");
        var stopRaw = reader.GetAttribute("stop");

        if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(startRaw) || string.IsNullOrEmpty(stopRaw)
            || !TryParseXmlTvDate(startRaw, out var start) || !TryParseXmlTvDate(stopRaw, out var stop))
        {
            reader.Skip();
            return null;
        }

        // Программа вне окна (прошлое старше DaysBack, будущее дальше
        // DaysAhead) не нужна ни одному экрану — пропускаем reader.Skip()
        // ДО чтения subtree с title/desc/category, чтобы не тратить время на
        // текстовые поля, которые всё равно отбросим. Именно это, а не сам
        // факт исключения записи из списка, даёт основную экономию на
        // источниках с полумиллионом программ.
        if (stop < windowStart || start > windowEnd)
        {
            reader.Skip();
            return null;
        }

        // ВАЖНО: без ReadSubtree. ReadElementContentAsString() на ридере из
        // ReadSubtree() «съедает» оставшихся соседей — читался только первый
        // текстовый ребёнок programme (title), desc/category терялись всегда.
        // Обходим детей по основному ридеру и выходим ровно на </programme>,
        // чтобы внешний цикл ParseXmlTv корректно продолжил с следующего узла.
        var programmeDepth = reader.Depth;
        string title = string.Empty;
        string description = string.Empty;
        string? category = null;

        if (!reader.IsEmptyElement)
        {
            while (reader.Depth > programmeDepth || reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    var handled = false;
                    switch (reader.Name)
                    {
                        case "title" when string.IsNullOrEmpty(title):
                            title = reader.ReadElementContentAsString();
                            handled = true;
                            break;
                        case "desc" when string.IsNullOrEmpty(description):
                            description = reader.ReadElementContentAsString();
                            handled = true;
                            break;
                        case "category" when category == null:
                            category = reader.ReadElementContentAsString();
                            handled = true;
                            break;
                    }

                    // ReadElementContentAsString уже продвинул ридер за элемент.
                    if (handled)
                    {
                        continue;
                    }
                }

                if (!reader.Read())
                {
                    break;
                }
            }
        }

        var entry = new EPGEntry
        {
            EventId = $"{channelId}_{startRaw}",
            ChannelId = channelId,
            ChannelName = channelNames.TryGetValue(channelId, out var name) ? name : channelId,
            ProgramName = title,
            Description = description,
            Category = category,
            StartTime = start,
            EndTime = stop
        };

        return entry;
    }

    private static bool TryParseXmlTvDate(string raw, out DateTime result)
    {
        raw = raw.Trim();
        result = default;

        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        if (!DateTime.TryParseExact(parts[0], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dt))
        {
            return false;
        }

        if (parts.Length < 2)
        {
            // Смещения нет вовсе — считаем, что время уже локальное.
            result = dt;
            return true;
        }

        var offsetRaw = parts[1];

        // Основной формат XMLTV — "+0300"/"-0500", БЕЗ двоеточия. Раньше тут
        // стоял DateTimeOffset.TryParseExact с custom-форматом "zzz" — а "zzz"
        // в .NET требует смещение С двоеточием ("+03:00"). На реальных фидах,
        // которые почти всегда шлют без двоеточия, парсинг проваливался для
        // КАЖДОЙ программы — ReadProgramme() возвращал null всегда, EPG
        // оставался пустым без единого исключения в логе. Разбираем смещение
        // вручную по позициям символов вместо TryParseExact с форматной строкой.
        if (offsetRaw.Length == 5 &&
            (offsetRaw[0] == '+' || offsetRaw[0] == '-') &&
            int.TryParse(offsetRaw.AsSpan(1, 2), out var offsetHours) &&
            int.TryParse(offsetRaw.AsSpan(3, 2), out var offsetMinutes))
        {
            var offset = new TimeSpan(offsetHours, offsetMinutes, 0);
            if (offsetRaw[0] == '-')
            {
                offset = -offset;
            }

            result = DateTime.SpecifyKind(dt - offset, DateTimeKind.Utc).ToLocalTime();
            return true;
        }

        // На случай источника, который всё-таки шлёт смещение с двоеточием
        // ("+03:00") — пробуем и такой вариант отдельно.
        if (DateTimeOffset.TryParseExact(raw, "yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dto))
        {
            result = dto.LocalDateTime;
            return true;
        }

        // Смещение не распозналось (мусор/неизвестный формат) — лучше
        // показать программу с потенциально не тем временем, чем не
        // показать вообще ни одной программы во всём канале.
        result = dt;
        return true;
    }
}
