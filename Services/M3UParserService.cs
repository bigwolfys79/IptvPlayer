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
    /// <summary>
    /// Парсер M3U/M3U8 плейлистов (расширенный формат с метаданными #EXTINF)
    /// в список каналов приложения.
    ///
    /// Логика разбора портирована из проверенной реализации на другой платформе
    /// (WinPlay/WPF) и адаптирована под интерфейс IM3UParserService/ChannelViewModel
    /// этого проекта.
    /// </summary>
    public class M3UParserService : IM3UParserService
    {
        private static readonly HttpClient _httpClient = CreateHttpClient();

        private readonly ProcessSpeedMonitor _speedMonitor;

        public M3UParserService(ProcessSpeedMonitor speedMonitor)
        {
            _speedMonitor = speedMonitor;
        }

        // Достаточно, чтобы строка НАЧИНАЛАСЬ с "#EXTINF:<число>" — не требуем,
        // чтобы вся строка целиком соответствовала жёсткому шаблону с запятой.
        private static readonly Regex ExtinfStartRegex =
            new(@"^\s*#\s*EXTINF\s*:\s*-?[0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        static M3UParserService()
        {
            // Windows-1251 (частая кодировка русскоязычных плейлистов) на .NET доступна
            // только через провайдер кодовых страниц. Пакет System.Text.Encoding.CodePages
            // должен быть подключен в .csproj — см. примечание в комментарии к Decode().
            try
            {
                Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            }
            catch
            {
                // Провайдер уже зарегистрирован, либо пакет не подключен —
                // тогда Decode() ниже сам подстрахуется через try/catch.
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            // Многие IPTV/Xtream-провайдеры отдают пустую страницу, 403 или редирект,
            // если запрос выглядит "не браузерным" (пустой User-Agent, который .NET шлёт
            // по умолчанию). Представляемся как обычный браузер/плеер.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("*/*");

            return client;
        }

        /// <summary>
        /// Загружает и разбирает плейлист по URL (http/https).
        /// </summary>
        public async Task<List<ChannelViewModel>> ParseFromUrlAsync(string playlistUrl, CancellationToken ct = default)
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

            HttpResponseMessage response;
            // Плейлист качается тем же процессом — его байты не должны
            // попадать в замер скорости потока (ProcessSpeedMonitor).
            using var playlistPause = _speedMonitor.PauseScope();
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
                throw; // переключение плейлиста — не ошибка, просто отменяем скачивание
            }
            catch (TaskCanceledException ex)
            {
                throw new InvalidOperationException($"Превышено время ожидания при загрузке плейлиста '{playlistUrl}'.", ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Сервер плейлиста вернул ошибку {(int)response.StatusCode} ({response.StatusCode}) для '{playlistUrl}'.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);

            // Decode (перебор байтов детектором кодировки) и ParseContent
            // (regex на каждую строку ~4000-строчного плейлиста) — синхронная
            // CPU-работа на сотни миллисекунд; без Task.Run она выполнялась бы
            // прямо на UI-потоке (вызов идёт из диалога настроек) и диалог
            // «замирал» на время разбора. Сеть уже отдана настоящему async I/O
            // выше — здесь остаётся убрать только CPU-часть.
            var content = await Task.Run(() => Decode(bytes));
            var channels = await Task.Run(() => ParseContent(content));

            if (channels.Count == 0)
            {
                // Сервер ответил 200 OK, но каналов не найдено — почти всегда значит,
                // что вместо плейлиста пришла страница-заглушка (блокировка по
                // User-Agent/IP, требуется авторизация, неверная ссылка и т.п.).
                var preview = content.Length > 200 ? content[..200] : content;
                preview = preview.Replace("\r", " ").Replace("\n", " ").Trim();
                throw new InvalidOperationException(
                    "В ответе сервера не найдено ни одного канала — похоже, вместо плейлиста " +
                    "пришла страница-заглушка (проверьте ссылку и не блокирует ли провайдер запросы " +
                    $"без авторизации/с этого IP). Начало ответа: \"{preview}\"");
            }

            return channels;
        }

        /// <summary>
        /// Разбирает плейлист из локального файла (с автоопределением кодировки).
        /// </summary>
        public async Task<List<ChannelViewModel>> ParseFromFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Файл плейлиста не найден.", filePath);
            }

            var bytes = await File.ReadAllBytesAsync(filePath);
            var content = await Task.Run(() => Decode(bytes));
            return await Task.Run(() => ParseContent(content));
        }

        /// <summary>
        /// Разбирает "сырой" текст M3U/M3U8 в список каналов.
        /// </summary>
        public List<ChannelViewModel> ParseContent(string content)
        {
            var channels = new List<ChannelViewModel>();

            if (string.IsNullOrWhiteSpace(content))
            {
                return channels;
            }

            content = content.TrimStart('\uFEFF');

            string? lastGroup = null;
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
                    var channel = ParseExtinf(line, lastGroup, nextId);
                    channels.Add(channel);
                    nextId++;
                    continue;
                }

                // #EXTGRP задаёт группу для канала(ов), у которых нет group-title —
                // сохраняем как "текущую" группу до следующего #EXTGRP или конца файла.
                if (line.StartsWith("#EXTGRP", StringComparison.OrdinalIgnoreCase))
                {
                    lastGroup = line["#EXTGRP".Length..].Trim().TrimStart(':').Trim();
                    if (channels.Count > 0 && string.IsNullOrWhiteSpace(channels[^1].Group))
                    {
                        channels[^1].Group = lastGroup;
                    }
                    continue;
                }

                // Прочие директивы (#EXTVLCOPT, #EXT-X-*, комментарии) — пропускаем.
                if (line.StartsWith("#"))
                {
                    continue;
                }

                // Строка URL относится к последнему каналу, у которого ещё нет StreamUrl.
                if (channels.Count > 0 && string.IsNullOrEmpty(channels[^1].StreamUrl))
                {
                    channels[^1].StreamUrl = line;
                }
            }

            // Каналы без URL не воспроизводимы — исключаем (например, последняя
            // запись #EXTINF в файле, за которой не последовал URL).
            return channels.Where(c => !string.IsNullOrWhiteSpace(c.StreamUrl)).ToList();
        }

        private static ChannelViewModel ParseExtinf(string line, string? fallbackGroup, int index)
        {
            // Отрезаем "#EXTINF:-1" и берём оставшуюся часть строки.
            var attrStart = line.IndexOf(':');
            var body = attrStart >= 0 ? line[(attrStart + 1)..] : line;

            // Название канала — всё, что после ПОСЛЕДНЕЙ запятой (устойчивее, чем
            // "после первой", если внутри атрибутов встречаются запятые).
            var comma = body.LastIndexOf(',');
            var name = comma >= 0 ? body[(comma + 1)..].Trim() : body.Trim();
            var attrsPart = comma >= 0 ? body[..comma] : body;

            var tvgId = GetAttribute(attrsPart, "tvg-id");
            var logo = GetAttribute(attrsPart, "tvg-logo");
            var group = GetAttribute(attrsPart, "group-title") ?? fallbackGroup;

            // Глубина архива передач: провайдеры пишут её по-разному —
            // lunexas/goodstreem использует tvg-rec="7", стандартный вариант
            // IPTV — catchup-days="7" (или просто catchup="default" без
            // указания дней). От значения зависит зелёная точка архива в
            // списке каналов и клик по передаче в EPG.
            var recRaw = GetAttribute(attrsPart, "tvg-rec")
                ?? GetAttribute(attrsPart, "catchup-days")
                ?? GetAttribute(attrsPart, "catchup");
            var catchupDays = 0;
            if (!string.IsNullOrEmpty(recRaw))
            {
                // "default"/"append" без числа — считаем минимальным архивом.
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
                CatchupDays = catchupDays
            };
        }

        /// <summary>Достаёт значение атрибута вида key="value" (или key=value без кавычек).</summary>
        private static string? GetAttribute(string line, string key)
        {
            if (string.IsNullOrEmpty(line))
            {
                return null;
            }

            var m = Regex.Match(line, $@"(?:^|[\s])(?:{Regex.Escape(key)})\s*=\s*""(?<v>[^""]*)""", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return m.Groups["v"].Value;
            }

            m = Regex.Match(line, $@"(?:^|[\s])(?:{Regex.Escape(key)})\s*=\s*(?<v>[^\s,""']+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups["v"].Value : null;
        }

        /// <summary>
        /// Декодирование байт с определением кодировки (UTF-8, UTF-8 BOM, Windows-1251).
        /// ВАЖНО: для распознавания Windows-1251 в проект должен быть подключен пакет
        /// NuGet "System.Text.Encoding.CodePages" — без него ветка 1251 тихо откатится
        /// на Encoding.Default (UTF-8) и кириллица в именах каналов может отображаться
        /// некорректно (сам список каналов при этом всё равно будет найден).
        /// </summary>
        private static string Decode(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes, 3, bytes.Length - 3);
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
