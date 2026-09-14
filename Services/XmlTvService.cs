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


public class XmlTvService : IXmlTvService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(3);

    private const int DefaultDaysBack = 3;
    private const int DaysAhead = 3;

    private readonly HttpClient _httpClient;
    private readonly ILogger<XmlTvService> _logger;

    public event Action<string, bool, string?>? SourceLoadFinished;

    public XmlTvService(
        ILogger<XmlTvService> logger,
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

    public async Task<XmlTvLoadResult> LoadAsync(EPGSource source, TimeSpan? maxAge = null, int daysBack = DefaultDaysBack, CancellationToken ct = default)
    {

        var cacheKey = $"xmltv:{source.Url}:{daysBack}";


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


        if (cached != null && maxAge is { } limit && limit != TimeSpan.MaxValue)
        {
            var staleSavedAt = GetSavedAtUtc(cached);
            _logger.LogInformation(
                "Источник {Url}: кэш протух (возраст {Age:F1} ч при лимите {Limit:F0} дн.) — отдаётся сразу, перекачка в фоне.",
                source.Url, (DateTime.UtcNow - staleSavedAt).TotalHours, limit.TotalDays);
            StartBackgroundRefresh(source, cacheKey, daysBack);
            return new XmlTvLoadResult
            {
                Entries = cached.Entries,
                ChannelIcons = cached.ChannelIcons,
                DataSavedAtUtc = staleSavedAt
            };
        }

        var parsed = await DownloadAndParseAsync(source.Url, daysBack, ct);
        SourceLoadFinished?.Invoke(source.Url, true, null);

        await EpgCacheStore.WriteAsync(cacheKey, new CachedXmlTv
        {
            Entries = parsed.Entries,
            ChannelIcons = parsed.ChannelIcons,
            SavedAtUtc = parsed.DataSavedAtUtc,
            ExpiresAt = DateTime.UtcNow.Add(CacheTtl)
        });

        return parsed;
    }


    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _backgroundRefreshes = new(StringComparer.Ordinal);

    private void StartBackgroundRefresh(EPGSource source, string cacheKey, int daysBack)
    {
        if (!_backgroundRefreshes.TryAdd(cacheKey, 0))
        {
            return;
        }


        _ = Task.Run(async () =>
        {
            var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                var parsed = await DownloadAndParseAsync(source.Url, daysBack, cts.Token);
                await EpgCacheStore.WriteAsync(cacheKey, new CachedXmlTv
                {
                    Entries = parsed.Entries,
                    ChannelIcons = parsed.ChannelIcons,
                    SavedAtUtc = parsed.DataSavedAtUtc,
                    ExpiresAt = DateTime.UtcNow.Add(CacheTtl)
                });
                _logger.LogInformation(
                    "Источник {Url}: фоновая перекачка завершена, распарсено программ: {Programs}.",
                    source.Url, parsed.Entries.Count);
                SourceLoadFinished?.Invoke(source.Url, true, null);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Источник {Url}: фоновая перекачка отменена (таймаут).", source.Url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Источник {Url}: фоновая перекачка не удалась — кэш обновится при следующем запуске.", source.Url);
                SourceLoadFinished?.Invoke(source.Url, false, ex.Message);
            }
            finally
            {
                cts.Dispose();
                _backgroundRefreshes.TryRemove(cacheKey, out _);
            }
        });
    }


    public async Task<string?> ValidateEpgSourceAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return string.Format(L.T("Epg_Proverka_Status_0"), (int)response.StatusCode);
            }

            var raw = await response.Content.ReadAsStreamAsync(ct);
            var head = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            while (head.Length < buffer.Length &&
                   (read = await raw.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                head.Write(buffer, 0, read);
            }

            var bytes = head.GetBuffer();
            var isGzip = head.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;
            string content;
            if (isGzip)
            {
                head.Position = 0;
                var decompressed = new MemoryStream();
                await using (var gzip = new GZipStream(head, CompressionMode.Decompress))
                {
                    await gzip.CopyToAsync(decompressed, ct);
                }
                content = System.Text.Encoding.UTF8.GetString(
                    decompressed.GetBuffer(), 0, (int)Math.Min(decompressed.Length, 64 * 1024));
            }
            else
            {
                content = System.Text.Encoding.UTF8.GetString(bytes, 0, (int)head.Length);
            }

            if (content.Contains("<tv", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("<channel", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("<!DOCTYPE tv", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return L.T("Epg_Proverka_Ne_XMLTV");
        }
        catch (OperationCanceledException)
        {
            return L.T("Epg_Proverka_Taymaut");
        }
        catch (Exception ex)
        {
            return string.Format(L.T("Epg_Proverka_Oshibka_0"), ex.Message);
        }
    }

    private async Task<XmlTvLoadResult> DownloadAndParseAsync(string url, int daysBack, CancellationToken ct)
    {
        var now = DateTime.Now;
        var windowStart = now.Date.AddDays(-Math.Max(0, daysBack));
        var windowEnd = now.AddDays(DaysAhead + 1);
        var dataSavedAtUtc = DateTime.UtcNow;
        XmlTvLoadResult parsed;
        await using (System.IO.Stream stream = await DownloadAsync(url, ct))
        {
            parsed = await Task.Run(() => ParseXmlTv(stream, windowStart, windowEnd), ct);
        }

        _logger.LogInformation(
            "Источник {Url}: распарсено программ: {Programs}, иконок каналов: {Icons}.",
            url, parsed.Entries.Count, parsed.ChannelIcons.Count);

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


        if (savedAtUtc == default)
        {
            return false;
        }

        var age = DateTime.UtcNow - savedAtUtc;

        if (maxAge is { } limit)
        {
            return limit == TimeSpan.MaxValue || age < limit;
        }


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

        if (stop < windowStart || start > windowEnd)
        {
            reader.Skip();
            return null;
        }

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

            result = dt;
            return true;
        }

        var offsetRaw = parts[1];

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

        if (DateTimeOffset.TryParseExact(raw, "yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dto))
        {
            result = dto.LocalDateTime;
            return true;
        }

        result = dt;
        return true;
    }
}
