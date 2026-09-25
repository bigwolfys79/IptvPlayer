using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IptvPlayer.ViewModels;

namespace IptvPlayer.Services
{


    public class M3UParserService : IM3UParserService
    {
        private static readonly HttpClient _httpClient = CreateHttpClient();

        private static readonly TimeSpan BodyTimeout = TimeSpan.FromMinutes(2);

        // A playlist far above this size is a misbehaving server, not real data
        private const int MaxPlaylistBytes = 64 * 1024 * 1024;

        private static readonly Regex ExtinfStartRegex =
            new(@"^\s*#\s*EXTINF\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static M3UParserService()
        {

            try
            {
                Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            }
            catch
            {


            }
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("*/*");

            return client;
        }


        public async Task<List<ChannelViewModel>> ParseFromUrlAsync(string playlistUrl, CancellationToken ct = default, bool deriveGenreFromGroup = false)
        {
            if (string.IsNullOrWhiteSpace(playlistUrl))
            {
                throw new ArgumentException("URL плейлиста не может быть пустым.", nameof(playlistUrl));
            }

            if (!Uri.TryCreate(playlistUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("Некорректный URL плейлиста.", nameof(playlistUrl));
            }

            HttpResponseMessage? response = null;
            try
            {
                response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException($"Не удалось загрузить плейлист по адресу '{playlistUrl}'.", ex);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                throw new InvalidOperationException($"Превышено время ожидания при загрузке плейлиста '{playlistUrl}'.", ex);
            }

            using (response)
            {
                if (!response!.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(
                        $"Сервер плейлиста вернул ошибку {(int)response.StatusCode} ({response.StatusCode}) для '{playlistUrl}'.");
                }

                // HttpClient.Timeout covers headers only (ResponseHeadersRead) — cap the
                // body download explicitly so a hung server can't stall the load forever
                using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bodyCts.CancelAfter(BodyTimeout);
                await using var stream = await response.Content.ReadAsStreamAsync(bodyCts.Token);
                var bytes = await ReadCappedAsync(stream, MaxPlaylistBytes, bodyCts.Token);

                var content = await Task.Run(() => Decode(bytes));
                var channels = await Task.Run(() => ParseContent(content, deriveGenreFromGroup));

                if (channels.Count == 0)
                {

                    var preview = content.Length > 200 ? content[..200] : content;
                    preview = preview.Replace("\r", " ").Replace("\n", " ").Trim();
                    throw new InvalidOperationException(
                        "В ответе сервера не найдено ни одного канала — похоже, вместо плейлиста " +
                        "пришла страница-заглушка (проверьте ссылку и не блокирует ли провайдер запросы " +
                        $"без авторизации/с этого IP). Начало ответа: \"{preview}\"");
                }

                return channels;
            }
        }


        private static async Task<byte[]> ReadCappedAsync(Stream stream, int maxBytes, CancellationToken ct)
        {
            var ms = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                if (ms.Length + read > maxBytes)
                {
                    throw new InvalidOperationException(
                        $"Плейлист превышает лимит {maxBytes / (1024 * 1024)} МБ — загрузка прервана.");
                }

                ms.Write(buffer, 0, read);
            }

            return ms.ToArray();
        }


        public async Task<List<ChannelViewModel>> ParseFromFileAsync(string filePath, bool deriveGenreFromGroup = false)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Файл плейлиста не найден.", filePath);
            }

            var bytes = await File.ReadAllBytesAsync(filePath);
            var content = await Task.Run(() => Decode(bytes));
            return await Task.Run(() => ParseContent(content, deriveGenreFromGroup));
        }


        public List<ChannelViewModel> ParseContent(string content, bool deriveGenreFromGroup = false)
        {
            var channels = new List<ChannelViewModel>();

            if (string.IsNullOrWhiteSpace(content))
            {
                return channels;
            }

            content = content.TrimStart('\uFEFF');

            string? lastGroup = null;

            var lastGroupFromTitle = false;
            var nextId = 1;

            using var reader = new StringReader(content);
            string? rawLine;
            while ((rawLine = reader.ReadLine()) != null)
            {
                var line = rawLine.Trim();

                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ExtinfStartRegex.IsMatch(line))
                {
                    var channel = ParseExtinf(line, lastGroup, nextId, out var groupFromTitle, deriveGenreFromGroup);
                    lastGroupFromTitle = groupFromTitle;
                    channels.Add(channel);
                    nextId++;
                    continue;
                }

                if (line.StartsWith("#EXTDESC:", StringComparison.OrdinalIgnoreCase))
                {
                    // EXTDESC sits between EXTINF and its URL — applies to the pending entry
                    if (channels.Count > 0 && string.IsNullOrEmpty(channels[^1].StreamUrl))
                    {
                        channels[^1].Description = CleanExtDesc(line["#EXTDESC:".Length..]);
                    }
                    continue;
                }

                if (line.StartsWith("#EXTGRP", StringComparison.OrdinalIgnoreCase))
                {
                    lastGroup = line["#EXTGRP".Length..].Trim().TrimStart(':').Trim();
                    if (channels.Count > 0 && string.IsNullOrEmpty(channels[^1].StreamUrl))
                    {

                        if (!lastGroupFromTitle)
                        {
                            channels[^1].Group = string.IsNullOrWhiteSpace(lastGroup) ? null : lastGroup;
                        }
                    }
                    else if (channels.Count > 0 && string.IsNullOrWhiteSpace(channels[^1].Group))
                    {
                        channels[^1].Group = lastGroup;
                    }
                    continue;
                }


                if (line.StartsWith("#"))
                {
                    continue;
                }


                if (channels.Count > 0 && string.IsNullOrEmpty(channels[^1].StreamUrl))
                {
                    channels[^1].StreamUrl = line;
                }
            }

            return channels.Where(c => !string.IsNullOrWhiteSpace(c.StreamUrl)).ToList();
        }

        private static ChannelViewModel ParseExtinf(string line, string? fallbackGroup, int index, out bool groupFromTitle, bool deriveGenreFromGroup = false)
        {

            var attrStart = line.IndexOf(':');
            var body = attrStart >= 0 ? line[(attrStart + 1)..] : line;

            var comma = body.LastIndexOf(',');
            var name = comma >= 0 ? body[(comma + 1)..].Trim() : body.Trim();
            var attrsPart = comma >= 0 ? body[..comma] : body;

            var attrs = ParseAttributes(attrsPart);
            attrs.TryGetValue("tvg-id", out var tvgId);
            attrs.TryGetValue("tvg-logo", out var logo);
            attrs.TryGetValue("group-title", out var groupTitle);
            groupFromTitle = groupTitle is not null;
            var group = groupTitle ?? fallbackGroup;

            attrs.TryGetValue("tvg-genre", out var genreRaw);
            var genres = ParseGenres(genreRaw);
            if (genres.Count == 0 && deriveGenreFromGroup && !string.IsNullOrWhiteSpace(group))
            {
                // VOD catalogs encode genres as group path segments after a fixed structural prefix
                genres = GenresFromGroupPath(group);
            }

            var year = 0;
            if (attrs.TryGetValue("tvg-year", out var yearRaw) &&
                !int.TryParse(yearRaw.AsSpan().Trim(), out year))
            {
                year = 0;
            }

            attrs.TryGetValue("tvg-rec", out var recRaw);
            recRaw ??= attrs.GetValueOrDefault("catchup-days") ?? attrs.GetValueOrDefault("catchup");
            var catchupDays = 0;
            if (!string.IsNullOrEmpty(recRaw))
            {

                catchupDays = int.TryParse(recRaw, out var days) ? days : 1;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Канал {index}";
            }

            return new ChannelViewModel
            {
                Id = index,
                Name = name.Trim(),
                IsLive = true,
                LogoUrl = logo,
                Group = string.IsNullOrWhiteSpace(group) ? null : group.Trim(),
                TvgId = tvgId,
                CatchupDays = catchupDays,
                Genre = genres.Count > 0 ? string.Join(", ", genres) : null,
                Year = year
            };
        }


        // Closed set of catalog genres; anything else (site prefixes, movie
        // titles in series groups) must not leak into the genre filter
        internal static readonly HashSet<string> KnownGenres = new(StringComparer.OrdinalIgnoreCase)
        {
            "все фильмы", "новинки", "боевик", "биография", "вестерн", "военный",
            "детектив", "детский", "документальные", "драма", "исторические",
            "комедия", "короткометражка", "криминал", "мелодрама", "мистика",
            "мультфильмы", "мюзикл", "приключения", "семейный", "спорт",
            "триллер", "ужасы", "фантастика", "фэнтези"
        };


        // Split "Комедия, Драма" into clean genre tokens, whitelist-filtered
        private static List<string> ParseGenres(string? raw)
        {
            var genres = new List<string>();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return genres;
            }

            foreach (var token in raw.Split(','))
            {
                var genre = token.Trim();
                if (genre.Length > 0 && KnownGenres.Contains(genre) &&
                    !genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                {
                    genres.Add(genre);
                }
            }

            return genres;
        }


        // "Все фильмы / Фильмы / Драма / Комедия" -> ["Драма", "Комедия"]:
        // the first three path segments are the structural prefix, the rest are
        // kept only when they match the known genre list
        private static List<string> GenresFromGroupPath(string group)
        {
            var segments = group.Split('/')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            var genres = segments.Skip(3)
                .Where(s => KnownGenres.Contains(s))
                .ToList();

            if (genres.Count == 0 && segments.Count > 0 && KnownGenres.Contains(segments[^1]))
            {
                genres.Add(segments[^1]);
            }

            return genres;
        }


        // Drop the trailing "[страница: url]" site-link marker from EXTDESC
        private static string CleanExtDesc(string raw)
        {
            var text = raw.Trim();
            if (text.Length == 0)
            {
                return text;
            }

            var idx = text.LastIndexOf("[страница:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && text.EndsWith(']'))
            {
                var inner = text[(idx + "[страница:".Length)..^1].Trim();
                if (inner.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                    inner.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                {
                    text = text[..idx].TrimEnd();
                }
            }

            return text;
        }


        // Single-pass attribute scanner: parses key="value" and key=value pairs once per line
        private static Dictionary<string, string> ParseAttributes(string line)
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(line))
            {
                return attrs;
            }

            var i = 0;
            while (i < line.Length)
            {
                while (i < line.Length && (char.IsWhiteSpace(line[i]) || line[i] == ','))
                {
                    i++;
                }

                var keyStart = i;
                while (i < line.Length && line[i] != '=' && !char.IsWhiteSpace(line[i]))
                {
                    i++;
                }
                var key = line[keyStart..i];
                while (i < line.Length && char.IsWhiteSpace(line[i]))
                {
                    i++;
                }

                if (key.Length == 0 || i >= line.Length || line[i] != '=')
                {
                    // Skip dangling '=' to guarantee loop progress
                    if (key.Length == 0 && i < line.Length && line[i] == '=')
                    {
                        i++;
                    }
                    continue;
                }
                i++;
                while (i < line.Length && char.IsWhiteSpace(line[i]))
                {
                    i++;
                }

                string value;
                if (i < line.Length && (line[i] == '"' || line[i] == '\''))
                {
                    var quote = line[i];
                    var valStart = ++i;
                    while (i < line.Length && line[i] != quote)
                    {
                        i++;
                    }
                    value = line[valStart..i];
                    if (i < line.Length)
                    {
                        i++;
                    }
                }
                else
                {
                    var valStart = i;
                    while (i < line.Length && line[i] != ',' && !char.IsWhiteSpace(line[i]))
                    {
                        i++;
                    }
                    value = line[valStart..i];
                }

                attrs[key] = value;
            }

            return attrs;
        }


        private static string Decode(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes, 3, bytes.Length - 3);
            }

            // UTF-16 BOMs
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            try
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                try
                {
                    return Encoding.GetEncoding(1251).GetString(bytes);
                }
                catch
                {
                    return Encoding.Default.GetString(bytes);
                }
            }
        }
    }
}
