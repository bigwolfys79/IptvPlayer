using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IptvPlayer.Models;
using IptvPlayer.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace IptvPlayer.Services
{
    /// <summary>
    /// Раньше GetEPGEntriesAsync возвращал 2 захардкоженных "Sample Program" /
    /// "Next Program" — теперь реально скачивает и парсит XMLTV из источников,
    /// сохранённых в настройках, сливает несколько источников (первый в
    /// списке источников имеет приоритет при пересечении по времени для
    /// одного канала) и сопоставляет программы с каналами по
    /// ChannelViewModel.TvgId (а не по int Id, которого в XMLTV нет).
    ///
    /// Часть провайдеров плейлистов вообще не проставляет tvg-id в #EXTINF
    /// (например lunexas.top — есть только tvg-rec, служебный флаг записи).
    /// Для таких каналов используется резервное сопоставление по
    /// нормализованному названию канала (см. EpgNameNormalizer) —
    /// сравнивается название из M3U с display-name из XMLTV.
    /// </summary>
    public class EPGService : IEPGService
    {

        private static bool LogPerChannelDiagnostics => App.TempDiagnosticsEnabled;

        private readonly IChannelRepository _channelRepository;
        private readonly ISettingsService _settingsService;
        private readonly IXmlTvService _xmlTvService;
        private readonly ILogger<EPGService> _logger;

        private Dictionary<string, List<EPGEntry>> _entriesByChannelId = new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, List<EPGEntry>> _entriesByNormalizedName = new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> _iconsByChannelId = new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, string> _tvgIdByStrictName = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _tvgIdByLenientName = new(StringComparer.OrdinalIgnoreCase);
        private bool _nameMapLoadAttempted;

        private sealed class NameMapEntry
        {
            [JsonPropertyName("n")]
            public string N { get; set; } = string.Empty;

            [JsonPropertyName("i")]
            public string I { get; set; } = string.Empty;

            [JsonPropertyName("l")]
            public string L { get; set; } = string.Empty;
        }

        private sealed class NameMapDocument
        {
            [JsonPropertyName("entries")]
            public List<NameMapEntry> Entries { get; set; } = new();
        }

        private Dictionary<string, string> _logoByTvgId = new(StringComparer.OrdinalIgnoreCase);

        private bool _epgLoaded;
        private DateTime _lastSuccessfulLoad = DateTime.MinValue;
        private readonly TimeSpan _minReloadInterval = TimeSpan.FromMinutes(5);

        private bool _skipLogged;

        private Task? _loadingTask;
        private readonly object _loadingTaskGate = new();

        private System.Threading.CancellationTokenSource _loadCts = new();

        private readonly DispatcherQueue? _uiDispatcher;

        public EPGService(
            IChannelRepository channelRepository,
            ISettingsService settingsService,
            IXmlTvService xmlTvService,
            ILogger<EPGService> logger)
        {
            _channelRepository = channelRepository;
            _settingsService = settingsService;
            _xmlTvService = xmlTvService;
            _logger = logger;
            _uiDispatcher = DispatcherQueue.GetForCurrentThread();
        }

        public Task<List<ChannelViewModel>> GetChannelsAsync()
        {
            return _channelRepository.GetAllChannelsAsync();
        }

        public async Task<List<EPGEntry>> GetEPGEntriesAsync(int channelId)
        {
            await EnsureEpgLoadedAsync();

            if (channelId < 0)
            {
                return new List<EPGEntry>();
            }

            var channel = await _channelRepository.GetChannelByIdAsync(channelId);
            if (channel == null)
            {

                if (LogPerChannelDiagnostics)
                {
                    _logger.LogWarning(
                        "Канал с id={ChannelId} не найден в ChannelRepository — EPG для него не может быть найден.",
                        channelId);
                }
                return new List<EPGEntry>();
            }

            if (channel.IsPortalItem)
            {
                return new List<EPGEntry>();
            }

            var (entries, method) = MatchChannel(channel);

            switch (method)
            {
                case MatchMethod.None:
                    if (LogPerChannelDiagnostics)
                    {
                        if (string.IsNullOrWhiteSpace(channel.TvgId))
                        {
                            _logger.LogWarning(
                                "У канала \"{Name}\" (id={ChannelId}) пустой TvgId, и по названию тоже не " +
                                "нашлось совпадения в XMLTV — программы не будут показаны для этого канала.",
                                channel.Name, channelId);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "У канала \"{Name}\" tvg-id=\"{TvgId}\", но такого id нет ни в одном из " +
                                "загруженных XMLTV-источников, и по названию тоже не нашлось совпадения " +
                                "(всего разных id в XMLTV: {IdCount}). Проверьте написание tvg-id " +
                                "или названия канала.",
                                channel.Name, channel.TvgId, _entriesByChannelId.Count);
                        }
                    }
                    return new List<EPGEntry>();

                case MatchMethod.Name:

                    if (LogPerChannelDiagnostics)
                    {
                        _logger.LogInformation(
                            "Канал \"{Name}\" (id={ChannelId}) сопоставлен с EPG по названию (tvg-id " + "{TvgIdState}).",
                            channel.Name, channelId,
                            string.IsNullOrWhiteSpace(channel.TvgId) ? "отсутствует" : $"\"{channel.TvgId}\" не найден в XMLTV");
                    }
                    return entries;

                default:
                    return entries;
            }
        }

        public async Task<EPGEntry?> GetCurrentProgramAsync(int channelId)
        {
            await EnsureEpgLoadedAsync();

            var channel = await _channelRepository.GetChannelByIdAsync(channelId);
            if (channel == null || channel.IsPortalItem)
            {
                return null;
            }

            var (entries, _) = MatchChannel(channel);
            if (entries.Count == 0)
            {
                return null;
            }

            var now = DateTime.Now;
            return entries.FirstOrDefault(e => e.StartTime <= now && now < e.EndTime);
        }

        public async Task RefreshEPGAsync()
        {
            EpgCacheStore.ClearAll();
            _epgLoaded = false;

            Task? inFlight;
            lock (_loadingTaskGate)
            {
                inFlight = _loadingTask;
            }
            if (inFlight != null)
            {
                _loadCts.Cancel();
                try
                {
                    await inFlight;
                }
                catch
                {


                }
                _loadCts.Dispose();
                _loadCts = new System.Threading.CancellationTokenSource();
            }

            await EnsureEpgLoadedAsync(force: true);
            await GetChannelsAsync();
        }

        /// <summary>
        /// Перечитывает EPG с текущими источниками (активного плейлиста), не
        /// очищая дисковый кэш источников — XmlTvService отдаёт свежие файлы
        /// с диска без перекачки. Вызывается при переключении плейлиста и при
        /// изменении его источников: общий фид epg.one не качается заново.
        /// </summary>
        public async Task ReloadSourcesAsync()
        {
            _epgLoaded = false;

            Task? inFlight;
            lock (_loadingTaskGate)
            {
                inFlight = _loadingTask;
            }
            if (inFlight != null)
            {
                _loadCts.Cancel();
                try
                {
                    await inFlight;
                }
                catch
                {

                }
                _loadCts.Dispose();
                _loadCts = new System.Threading.CancellationTokenSource();
            }

            await EnsureEpgLoadedAsync(force: true);
        }

        private enum MatchMethod
        {
            None,
            TvgId,
            NameMap,
            Name
        }

        /// <summary>
        /// Порядок путей — от самого надёжного к самому приблизительному:
        /// 1) точное совпадение TvgId из плейлиста с id канала в XMLTV;
        /// 2) таблица "имя -> tvg-id" от epg.one (строгий ключ с таймшифтом,
        ///    затем мягкий) — надёжна тем, что собрана из этого же плейлиста;
        /// 3) индекс нормализованных имён XMLTV (срезаем HD/таймшифт/коды
        ///    стран и сравниваем то, что осталось).
        /// </summary>
        private (List<EPGEntry> Entries, MatchMethod Method) MatchChannel(ChannelViewModel channel)
        {

            if (!string.IsNullOrWhiteSpace(channel.TvgId) &&
                _entriesByChannelId.TryGetValue(channel.TvgId, out var byId))
            {
                return (byId, MatchMethod.TvgId);
            }

            var strictKey = EpgNameNormalizer.NormalizePreservingTimeshift(channel.Name);
            if (!string.IsNullOrEmpty(strictKey) &&
                _tvgIdByStrictName.TryGetValue(strictKey, out var strictId) &&
                _entriesByChannelId.TryGetValue(strictId, out var strictEntries))
            {
                return (strictEntries, MatchMethod.NameMap);
            }

            var lenientKey = EpgNameNormalizer.Normalize(channel.Name);
            if (!string.IsNullOrEmpty(lenientKey) &&
                _tvgIdByLenientName.TryGetValue(lenientKey, out var lenientId) &&
                _entriesByChannelId.TryGetValue(lenientId, out var lenientEntries))
            {
                return (lenientEntries, MatchMethod.NameMap);
            }

            if (!string.IsNullOrEmpty(lenientKey) &&
                _entriesByNormalizedName.TryGetValue(lenientKey, out var byName))
            {
                return (byName, MatchMethod.Name);
            }

            return (new List<EPGEntry>(), MatchMethod.None);
        }


        /// <summary>
        /// Загружает таблицу "имя -> tvg-id" (Assets/epg-name-map.json).
        /// Вызывается один раз за сессию до первого сопоставления; отсутствие
        /// или битость файла не фатально — просто останутся пути по tvg-id из
        /// плейлиста и по индексу имён.
        /// </summary>
        private void LoadTvgIdNameMap()
        {
            if (_nameMapLoadAttempted)
            {
                return;
            }
            _nameMapLoadAttempted = true;

            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "epg-name-map.json");
                if (!File.Exists(path))
                {
                    _logger.LogWarning(
                        "Нет файла {Path} — сопоставление пойдёт только по tvg-id плейлиста и индексу имён.",
                        path);
                    return;
                }

                var doc = JsonSerializer.Deserialize<NameMapDocument>(File.ReadAllText(path));
                if (doc == null)
                {
                    return;
                }

                var lenientRawLength = new Dictionary<string, int>();
                foreach (var entry in doc.Entries)
                {
                    if (string.IsNullOrEmpty(entry.N) || string.IsNullOrEmpty(entry.I))
                    {
                        continue;
                    }

                    var strictKey = EpgNameNormalizer.NormalizePreservingTimeshift(entry.N);
                    if (strictKey.Length > 0)
                    {
                        _tvgIdByStrictName.TryAdd(strictKey, entry.I);
                    }

                    var lenientKey = EpgNameNormalizer.Normalize(entry.N);
                    if (lenientKey.Length > 0 &&
                        (!_tvgIdByLenientName.TryGetValue(lenientKey, out var currentId) ||
                         entry.N.Length < lenientRawLength[lenientKey]))
                    {
                        _tvgIdByLenientName[lenientKey] = entry.I;
                        lenientRawLength[lenientKey] = entry.N.Length;
                    }

                    if (!string.IsNullOrEmpty(entry.L))
                    {
                        _logoByTvgId.TryAdd(entry.I, entry.L);
                    }
                }

                _logger.LogInformation(
                    "Таблица имя->tvg-id (epg.one/setup-playlist): {Entries} записей, " +
                    "строгих ключей {Strict}, мягких {Lenient}, логотипов {Logos}.",
                    doc.Entries.Count, _tvgIdByStrictName.Count, _tvgIdByLenientName.Count, _logoByTvgId.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось прочитать epg-name-map.json.");
            }
        }

        /// <summary>
        /// Скачивает и сливает все включённые источники из настроек, если это
        /// ещё не было сделано в текущей сессии (или если force = true, как
        /// после явного RefreshEPGAsync). Каждый XmlTvService.LoadAsync сам
        /// кэширует сырые данные по TTL, так что повторные вызовы внутри TTL
        /// не бьют по сети.
        ///
        /// Дополнительная защита от двойной загрузки: если успешно загружались
        /// менее _minReloadInterval назад и force=false, пропускаем перезагрузку.
        /// Это предотвращает случайные двойные вызовы при старте приложения.
        /// </summary>
        /// <summary>
        /// Публичная точка входа: не запускает вторую параллельную загрузку,
        /// если одна уже идёт — все конкурентные вызовы дожидаются того же
        /// Task'а (см. комментарий у _loadingTask/_loadingTaskGate выше).
        /// </summary>
        private Task EnsureEpgLoadedAsync(bool force = false)
        {
            var now = DateTime.Now;

            lock (_loadingTaskGate)
            {
                if (_loadingTask != null)
                {

                    return _loadingTask;
                }

                if (_epgLoaded && !force && (now - _lastSuccessfulLoad) < _minReloadInterval)
                {
                    if (!_skipLogged)
                    {
                        _skipLogged = true;
                        _logger.LogInformation(
                            "Пропуск перезагрузки EPG: успешно загружено {LoadedAt:HH:mm:ss}, интервал {Interval} не прошёл.",
                            _lastSuccessfulLoad, _minReloadInterval);
                    }
                    return Task.CompletedTask;
                }

                if (_epgLoaded && !force)
                {
                    return Task.CompletedTask;
                }

                _loadingTask = DoEnsureEpgLoadedAsync();
                return _loadingTask;
            }
        }

        private async Task DoEnsureEpgLoadedAsync()
        {
            var ct = _loadCts.Token;
            try
            {


                LoadTvgIdNameMap();

                var settings = await _settingsService.LoadAsync().ConfigureAwait(false);
                var enabledSources = settings.GetActiveEpgSources()
                    .Where(s => s.IsEnabled).ToList();

                var sourceSets = new[] { settings.EpgSources }
                    .Concat(settings.Playlists.Select(p => p.EpgSources))
                    .Select(set => set.Where(s => s.IsEnabled).Select(s => s.Url).ToArray())
                    .Where(urls => urls.Length > 0)
                    .Select(urls => EpgCacheStore.MergedKeyFor(urls));
                EpgCacheStore.CleanupOrphans(
                    settings.EpgSources
                        .Concat(settings.Playlists.SelectMany(p => p.EpgSources))


                        .Select(s => $"xmltv:{s.Url}:{settings.EpgArchiveDaysBack}")
                        .Concat(sourceSets)
                        .Distinct(StringComparer.Ordinal));

                TimeSpan maxAge = settings.EpgRefreshDays > 0
                    ? TimeSpan.FromDays(settings.EpgRefreshDays)
                    : TimeSpan.MaxValue;

                int archiveDaysBack = settings.EpgArchiveDaysBack;

                if (enabledSources.Count == 0)
                {
                    var totalSources = settings.GetActiveEpgSources().Count;
                    if (totalSources > 0)
                    {
                        _logger.LogWarning(
                            "Нет ни одного включённого источника EPG (всего в настройках: {Total}). " +
                            "EPG будет пустым, пока в настройках не добавите/не включите хотя бы один источник.",
                            totalSources);
                    }
                    else
                    {

                        _logger.LogInformation(
                            "EPG не загружается: у активного плейлиста нет источников EPG.");
                    }
                }

                if (enabledSources.Count > 0 &&
                    await TryLoadMergedCacheAsync(enabledSources, maxAge).ConfigureAwait(false))
                {
                    await ApplyMissingLogosAsync().ConfigureAwait(false);
                    await LogMatchSummaryAsync().ConfigureAwait(false);
                    return;
                }

                var loadTasks = enabledSources.Select(async source =>
                {
                    try
                    {
                        return await _xmlTvService.LoadAsync(source, maxAge, archiveDaysBack, ct);
                    }
                    catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        _logger.LogInformation("Загрузка EPG отменена (источник {Url}).", SecretProtector.Mask(source.Url));
                        return (XmlTvLoadResult?)null;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Источник EPG недоступен/битый: {Url}", SecretProtector.Mask(source.Url));
                        return (XmlTvLoadResult?)null;
                    }
                }).ToList();

                var results = await Task.WhenAll(loadTasks).ConfigureAwait(false);
                var sourceResults = results.Where(r => r != null).Select(r => r!).ToList();

                var (byChannel, iconsByChannelId, nameIndex) = await Task.Run(() => EpgSourceMerger.Merge(sourceResults, _logger)).ConfigureAwait(false);

                if (sourceResults.Count == enabledSources.Count)
                {
                    var urls = enabledSources.Select(s => s.Url).ToArray();
                    _ = EpgCacheStore.WriteRecordAsync(
                        EpgCacheStore.MergedKeyFor(urls),
                        new MergedEpgCache
                        {
                            SourceUrls = urls.ToList(),
                            SourceSavedAtUtc = sourceResults.Select(r => r.DataSavedAtUtc).ToList(),
                            ByChannel = byChannel,
                            IconsByChannelId = iconsByChannelId
                        });
                }

                _entriesByChannelId = byChannel;
                _entriesByNormalizedName = nameIndex;
                _iconsByChannelId = iconsByChannelId;
                _epgLoaded = true;
                _lastSuccessfulLoad = DateTime.Now;
                _skipLogged = false;

                var totalEntries = byChannel.Values.Sum(list => list.Count);
                _logger.LogInformation(
                    "Загружено источников: {Sources}, каналов с программами: {Channels}, всего программ: {Entries}. " +
                    "Если у вас каналы в плейлисте, но здесь 0 каналов с программами — " +
                    "проверьте, что ChannelViewModel.TvgId совпадает с channel id в вашем XMLTV-файле.",
                    enabledSources.Count, byChannel.Count, totalEntries);

                await ApplyMissingLogosAsync().ConfigureAwait(false);

                await LogMatchSummaryAsync().ConfigureAwait(false);
            }
            finally
            {

                lock (_loadingTaskGate)
                {
                    _loadingTask = null;
                }
            }
        }

        /// <summary>
        /// Быстрый путь загрузки EPG: читает кэш слитого результата
        /// (MergedEpgCache) и, если он валиден, заполняет индексы без
        /// чтения кэшей источников и без слияния. Валидность: набор URL
        /// совпадает с включёнными источниками (в том же порядке) и
        /// периодичность обновления (maxAge) ещё не истекла ни по одному
        /// источнику — ровно то же условие, по которому XmlTvService взял
        /// бы кэш источника без сети, поэтому данные идентичны полному
        /// пути. false — промах (идти полным путём).
        /// </summary>
        private async Task<bool> TryLoadMergedCacheAsync(
            List<Models.EPGSource> enabledSources, TimeSpan maxAge)
        {
            var urls = enabledSources.Select(s => s.Url).ToArray();
            var cached = await EpgCacheStore.ReadRecordAsync<MergedEpgCache>(
                EpgCacheStore.MergedKeyFor(urls)).ConfigureAwait(false);
            if (cached is null ||
                cached.FormatVersion != MergedEpgCache.CurrentFormatVersion ||
                cached.SourceUrls.Count != urls.Length ||
                cached.SourceSavedAtUtc.Count != urls.Length)
            {
                return false;
            }

            for (var i = 0; i < urls.Length; i++)
            {
                if (!string.Equals(cached.SourceUrls[i], urls[i], StringComparison.Ordinal))
                {
                    return false;
                }

                var savedAt = cached.SourceSavedAtUtc[i];
                if (savedAt == default ||
                    (maxAge != TimeSpan.MaxValue && DateTime.UtcNow - savedAt >= maxAge))
                {
                    return false;
                }
            }

            var byChannel = new Dictionary<string, List<Models.EPGEntry>>(
                cached.ByChannel, StringComparer.OrdinalIgnoreCase);
            var icons = new Dictionary<string, string>(
                cached.IconsByChannelId, StringComparer.OrdinalIgnoreCase);
            var nameIndex = await Task.Run(
                () => EpgSourceMerger.BuildNameIndex(byChannel, _logger)).ConfigureAwait(false);

            _entriesByChannelId = byChannel;
            _entriesByNormalizedName = nameIndex;
            _iconsByChannelId = icons;
            _epgLoaded = true;
            _lastSuccessfulLoad = DateTime.Now;
            _skipLogged = false;

            _logger.LogInformation(
                "Слитый EPG взят из кэша слияния: источников {Sources}, каналов с программами: {Channels}, всего программ: {Entries} — без загрузки источников и слияния.",
                cached.SourceUrls.Count,
                byChannel.Count,
                byChannel.Values.Sum(list => list.Count));

            return true;
        }

        /// <summary>
        /// Подставляет логотип из XMLTV (&lt;icon src&gt;) каналам, у которых
        /// нет tvg-logo в плейлисте. Мутирует те же объекты ChannelViewModel,
        /// что лежат в ChannelRepository — GetChannelsAsync() теперь отдаёт
        /// их напрямую (см. комментарий там), так что изменение LogoUrl
        /// долетает до UI через INotifyPropertyChanged без дополнительной
        /// инвалидации какого-либо кэша.
        ///
        /// Мутации выполняются строго на UI-потоке (см. _uiDispatcher):
        /// подбор кандидатов — чистые словарные поискы — можно делать где
        /// угодно, а вот запись LogoUrl трогает привязки x:Bind.
        /// </summary>
        private async Task ApplyMissingLogosAsync()
        {

            if (_iconsByChannelId.Count == 0 && _logoByTvgId.Count == 0)
            {
                return;
            }

            List<ChannelViewModel> channels;
            try
            {
                channels = await _channelRepository.GetAllChannelsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ApplyMissingLogosAsync: не удалось получить список каналов.");
                return;
            }

            var fills = new List<(ChannelViewModel Channel, string IconUrl)>();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(() =>
            {
                foreach (var channel in channels)
                {
                    if (!string.IsNullOrWhiteSpace(channel.LogoUrl))
                    {
                        continue;
                    }

                    if (channel.IsPortalItem)
                    {
                        continue;
                    }

                    var strictKey = EpgNameNormalizer.NormalizePreservingTimeshift(channel.Name);
                    var lenientKey = EpgNameNormalizer.Normalize(channel.Name);
                    if (!_tvgIdByStrictName.TryGetValue(strictKey, out var strictId))
                    {
                        strictId = string.Empty;
                    }
                    if (!_tvgIdByLenientName.TryGetValue(lenientKey, out var lenientId))
                    {
                        lenientId = string.Empty;
                    }

                    var candidates = new[]
                    {
                        channel.TvgId,
                        strictId,
                        lenientId
                    };

                    foreach (var id in candidates)
                    {
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            continue;
                        }

                        if (_iconsByChannelId.TryGetValue(id, out var iconUrl) ||
                            _logoByTvgId.TryGetValue(id, out iconUrl))
                        {
                            fills.Add((channel, iconUrl));
                            break;
                        }
                    }
                }
            });
            _logger.LogInformation(
                "Подстановка логотипов: подбор по {Count} каналам за {Ms:F0} мс (вне UI-потока).",
                channels.Count, sw.Elapsed.TotalMilliseconds);

            if (fills.Count == 0)
            {
                return;
            }

            void ApplyFills()
            {
                var filled = 0;

                const int ChunkSize = 150;
                var index = 0;

                void ApplyChunk()
                {
                    var end = Math.Min(index + ChunkSize, fills.Count);
                    for (; index < end; index++)
                    {
                        var (channel, iconUrl) = fills[index];

                        if (string.IsNullOrWhiteSpace(channel.LogoUrl))
                        {
                            channel.LogoUrl = iconUrl;
                            filled++;
                        }
                    }

                    if (index < fills.Count)
                    {
                        _uiDispatcher?.TryEnqueue(ApplyChunk);
                        return;
                    }

                    if (filled > 0)
                    {
                        _logger.LogInformation(
                            "Подставлено логотипов (XMLTV icon / таблица epg.one): {Count}.", filled);
                    }
                }

                if (fills.Count > 0)
                {
                    if (_uiDispatcher != null && !_uiDispatcher.HasThreadAccess)
                    {
                        _uiDispatcher.TryEnqueue(ApplyChunk);
                    }
                    else
                    {
                        ApplyChunk();
                    }
                }
            }

            if (_uiDispatcher != null && !_uiDispatcher.HasThreadAccess)
            {
                _uiDispatcher.TryEnqueue(ApplyFills);
            }
            else
            {
                ApplyFills();
            }
        }

        private async Task LogMatchSummaryAsync()
        {
            List<ChannelViewModel> channels;
            try
            {
                channels = await _channelRepository.GetAllChannelsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LogMatchSummaryAsync: не удалось получить список каналов.");
                return;
            }

            if (channels.Count == 0)
            {
                return;
            }

            var withoutTvgId = channels.Count(c => string.IsNullOrWhiteSpace(c.TvgId));
            var matchedById = 0;
            var matchedByMap = 0;
            var matchedByName = 0;
            var unmatched = new List<string>();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(() =>
            {
                foreach (var channel in channels)
                {

                    if (channel.IsPortalItem)
                    {
                        continue;
                    }

                    var (_, method) = MatchChannel(channel);
                    switch (method)
                    {
                        case MatchMethod.TvgId:
                            matchedById++;
                            break;
                        case MatchMethod.NameMap:
                            matchedByMap++;
                            break;
                        case MatchMethod.Name:
                            matchedByName++;
                            break;
                        default:
                            unmatched.Add(channel.Name);
                            break;
                    }
                }
            });
            _logger.LogInformation(
                "Сводка сопоставления: {Count} каналов за {Ms:F0} мс (вне UI-потока).",
                channels.Count, sw.Elapsed.TotalMilliseconds);

            var unmatchedSample = unmatched.Take(10).ToList();
            var sampleXmlTvIds = _entriesByChannelId.Keys.Take(10).ToList();

            _logger.LogInformation(
                "Сопоставление плейлиста с XMLTV: каналов всего {Total}, без tvg-id {WithoutTvgId}, " +
                "сопоставлено по tvg-id {ById}, по таблице имя->tvg-id {ByMap}, " +
                "по названию (резервный путь) {ByName}, не сопоставлено вообще {Unmatched}. {UnmatchedSample}{IdSample}",
                channels.Count, withoutTvgId, matchedById, matchedByMap, matchedByName, unmatched.Count,
                unmatchedSample.Count > 0
                    ? $"Примеры несопоставленных каналов: {string.Join(", ", unmatchedSample.Select(n => $"\"{n}\""))}. "
                    : string.Empty,
                sampleXmlTvIds.Count > 0
                    ? $"Примеры id, которые реально встречаются в загруженном XMLTV: {string.Join(", ", sampleXmlTvIds.Select(id => $"\"{id}\""))}."
                    : "В загруженном XMLTV вообще нет ни одного id каналов.");
        }
    }
}
