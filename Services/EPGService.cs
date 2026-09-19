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


    public class EPGService : IEPGService
    {

        private static bool LogPerChannelDiagnostics => App.TempDiagnosticsEnabled;

        private readonly IChannelRepository _channelRepository;
        private readonly ISettingsService _settingsService;
        private readonly IXmlTvService _xmlTvService;
        private readonly IPlaylistCacheService _playlistCacheService;
        private readonly ILogger<EPGService> _logger;

        private Dictionary<string, List<EPGEntry>> _entriesByChannelId = new(StringComparer.OrdinalIgnoreCase);

        // Per-channel match result cache: avoids repeated normalizer runs on misses
        private Dictionary<int, (List<EPGEntry> Entries, MatchMethod Method)>? _matchCache;

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

        private Task? _loadingTask;
        private readonly object _loadingTaskGate = new();

        private System.Threading.CancellationTokenSource _loadCts = new();

        // Cancel in-flight load and swap CTS under the gate (fixes dispose race)
        private void CancelAndReplaceLoadCts()
        {
            System.Threading.CancellationTokenSource old;
            lock (_loadingTaskGate)
            {
                old = _loadCts;
                _loadCts = new System.Threading.CancellationTokenSource();
            }
            try
            {
                old.Cancel();
            }
            catch (ObjectDisposedException) { }
            old.Dispose();
        }

        private readonly DispatcherQueue? _uiDispatcher;

        public EPGService(
            IChannelRepository channelRepository,
            ISettingsService settingsService,
            IXmlTvService xmlTvService,
            IPlaylistCacheService playlistCacheService,
            ILogger<EPGService> logger)
        {
            _channelRepository = channelRepository;
            _settingsService = settingsService;
            _xmlTvService = xmlTvService;
            _playlistCacheService = playlistCacheService;
            _logger = logger;
            _uiDispatcher = DispatcherQueue.GetForCurrentThread();
            _xmlTvService.SourceLoadFinished += OnSourceLoadFinished;
        }

        private readonly System.Threading.SemaphoreSlim _statusUpdateGate = new(1, 1);


        private async void OnSourceLoadFinished(string url, bool success, string? error)
        {
            try
            {
                await _statusUpdateGate.WaitAsync().ConfigureAwait(false);
                var settings = await _settingsService.LoadAsync().ConfigureAwait(false);
                var changed = false;

                foreach (var source in settings.EpgSources
                             .Concat(settings.Playlists.SelectMany(p => p.EpgSources))
                             .Where(s => string.Equals(s.Url, url, StringComparison.Ordinal)))
                {
                    if (success)
                    {
                        if (source.LastError != null || source.LastSuccessAt is null)
                        {
                            source.LastError = null;
                            source.LastSuccessAt = DateTimeOffset.Now;
                            changed = true;
                        }
                    }
                    else if (!string.Equals(source.LastError, error, StringComparison.Ordinal))
                    {
                        source.LastError = error;
                        changed = true;
                    }
                }

                if (changed)
                {
                    await _settingsService.SaveAsync(settings).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось записать статус EPG-источника {Url}.", SecretProtector.Mask(url));
            }
            finally
            {
                _statusUpdateGate.Release();
            }
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
                CancelAndReplaceLoadCts();
                try
                {
                    await inFlight;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Предыдущая загрузка EPG завершилась с ошибкой.");
                }
            }

            await EnsureEpgLoadedAsync(force: true);
            await GetChannelsAsync();
        }


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
                CancelAndReplaceLoadCts();
                try
                {
                    await inFlight;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Предыдущая загрузка EPG завершилась с ошибкой.");
                }
            }

            await EnsureEpgLoadedAsync(force: true);
        }

        private enum MatchMethod
        {
            None,
            TvgId,
            Alias,
            NameMap,
            Name
        }


        private Dictionary<string, string> _epgChannelIdByStreamUrl = new(StringComparer.Ordinal);


        private (List<EPGEntry> Entries, MatchMethod Method) MatchChannel(ChannelViewModel channel)
        {
            if (_matchCache is { } cache && cache.TryGetValue(channel.Id, out var cachedMatch))
            {
                return cachedMatch;
            }

            var result = MatchChannelCore(channel);
            StoreMatch(channel, result);
            return result;
        }

        private (List<EPGEntry> Entries, MatchMethod Method) MatchChannelCore(ChannelViewModel channel)
        {
            if (!string.IsNullOrWhiteSpace(channel.TvgId) &&
                _entriesByChannelId.TryGetValue(channel.TvgId, out var byId))
            {
                return (byId, MatchMethod.TvgId);
            }

            if (!string.IsNullOrEmpty(channel.StreamUrl) &&
                _epgChannelIdByStreamUrl.TryGetValue(channel.StreamUrl, out var aliasedId) &&
                _entriesByChannelId.TryGetValue(aliasedId, out var aliasedEntries))
            {
                return (aliasedEntries, MatchMethod.Alias);
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

        private void StoreMatch(ChannelViewModel channel, (List<EPGEntry> Entries, MatchMethod Method) result)
        {
            (_matchCache ??= new Dictionary<int, (List<EPGEntry>, MatchMethod)>())[channel.Id] = result;
        }


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


        private Task EnsureEpgLoadedAsync(bool force = false)
        {
            lock (_loadingTaskGate)
            {
                if (_loadingTask != null)
                {

                    return _loadingTask;
                }

                if (_epgLoaded && !force)
                {
                    return Task.CompletedTask;
                }

                if (force)
                {
                    _logger.LogInformation("Перезагрузка EPG по запросу (force).");
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

                // File I/O off the caller thread
                var mapTask = Task.Run(LoadTvgIdNameMap);

                var settings = await _settingsService.LoadAsync().ConfigureAwait(false);
                var enabledSources = settings.GetActiveEpgSources()
                    .Where(s => s.IsEnabled).ToList();

                var sourceSets = new[] { settings.EpgSources }
                    .Concat(settings.Playlists.Select(p => p.EpgSources))
                    .Select(set => set.Where(s => s.IsEnabled).Select(s => s.Url).ToArray())
                    .Where(urls => urls.Length > 0)
                    .Select(urls => EpgCacheStore.MergedKeyFor(urls));
                // File I/O off the caller thread
                await Task.Run(() =>
                    EpgCacheStore.CleanupOrphans(
                        settings.EpgSources
                            .Concat(settings.Playlists.SelectMany(p => p.EpgSources))


                            .Select(s => $"xmltv:{s.Url}:{settings.EpgArchiveDaysBack}")
                            .Concat(sourceSets)
                            .Distinct(StringComparer.Ordinal))).ConfigureAwait(false);
                await mapTask.ConfigureAwait(false);

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
                    await LoadEpgAliasesAsync().ConfigureAwait(false);
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
                        OnSourceLoadFinished(source.Url, false, ex.Message);
                        return (XmlTvLoadResult?)null;
                    }
                }).ToList();

                var results = await Task.WhenAll(loadTasks).ConfigureAwait(false);
                var sourceResults = results.Where(r => r != null).Select(r => r!).ToList();

                if (enabledSources.Count > 0 && sourceResults.Count == 0)
                {
                    // All sources failed — leave _epgLoaded unset so the next call retries
                    _logger.LogWarning(
                        "Ни один из {Count} источников EPG не загрузился — EPG останется пустым, будет повторная попытка.",
                        enabledSources.Count);
                    return;
                }

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
                _matchCache = null;
                _iconsByChannelId = iconsByChannelId;
                _epgLoaded = true;

                var totalEntries = byChannel.Values.Sum(list => list.Count);
                _logger.LogInformation(
                    "Загружено источников: {Sources}, каналов с программами: {Channels}, всего программ: {Entries}. " +
                    "Если у вас каналы в плейлисте, но здесь 0 каналов с программами — " +
                    "проверьте, что ChannelViewModel.TvgId совпадает с channel id в вашем XMLTV-файле.",
                    enabledSources.Count, byChannel.Count, totalEntries);

                await ApplyMissingLogosAsync().ConfigureAwait(false);

                await LoadEpgAliasesAsync().ConfigureAwait(false);

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
            _matchCache = null;
            _iconsByChannelId = icons;
            _epgLoaded = true;

            _logger.LogInformation(
                "Слитый EPG взят из кэша слияния: источников {Sources}, каналов с программами: {Channels}, всего программ: {Entries} — без загрузки источников и слияния.",
                cached.SourceUrls.Count,
                byChannel.Count,
                byChannel.Values.Sum(list => list.Count));

            return true;
        }


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


        private async Task LoadEpgAliasesAsync()
        {
            var aliases = await _playlistCacheService.GetEpgAliasesAsync().ConfigureAwait(false);
            if (aliases.Count == 0)
            {
                return;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var alias in aliases)
            {
                if (_entriesByChannelId.ContainsKey(alias.XmlTvId))
                {
                    map[alias.StreamUrl] = alias.XmlTvId;
                }
            }

            _epgChannelIdByStreamUrl = map;
            _logger.LogInformation(
                "EPG-псевдонимы: в БД {Total}, применимо к текущим источникам {Usable}.",
                aliases.Count, map.Count);
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
            var matchedByAlias = 0;
            var matchedByMap = 0;
            var matchedByName = 0;
            var unmatched = new List<string>();
            var learned = new List<PlaylistDatabaseService.EpgAlias>();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(() =>
            {
                foreach (var channel in channels)
                {

                    if (channel.IsPortalItem)
                    {
                        continue;
                    }

                    var (entries, method) = MatchChannel(channel);
                    switch (method)
                    {
                        case MatchMethod.TvgId:
                            matchedById++;
                            break;
                        case MatchMethod.Alias:
                            matchedByAlias++;
                            break;
                        case MatchMethod.NameMap:
                            matchedByMap++;
                            TryLearnAlias(learned, channel, entries);
                            break;
                        case MatchMethod.Name:
                            matchedByName++;
                            TryLearnAlias(learned, channel, entries);
                            break;
                        default:
                            unmatched.Add(channel.Name);
                            break;
                    }
                }
            });

            if (learned.Count > 0)
            {
                await _playlistCacheService.UpsertEpgAliasesAsync(learned).ConfigureAwait(false);
                _logger.LogInformation(
                    "Выучено новых EPG-псевдонимов: {Learned} (перезаписывается существующий ключ).",
                    learned.Count);
            }

            _logger.LogInformation(
                "Сводка сопоставления: {Count} каналов за {Ms:F0} мс (вне UI-потока).",
                channels.Count, sw.Elapsed.TotalMilliseconds);

            var unmatchedSample = unmatched.Take(10).ToList();
            var sampleXmlTvIds = _entriesByChannelId.Keys.Take(10).ToList();

            _logger.LogInformation(
                "Сопоставление плейлиста с XMLTV: каналов всего {Total}, без tvg-id {WithoutTvgId}, " +
                "сопоставлено по tvg-id {ById}, по выученным псевдонимам {ByAlias}, " +
                "по таблице имя->tvg-id {ByMap}, " +
                "по названию (резервный путь) {ByName}, не сопоставлено вообще {Unmatched}. {UnmatchedSample}{IdSample}",
                channels.Count, withoutTvgId, matchedById, matchedByAlias, matchedByMap, matchedByName, unmatched.Count,
                unmatchedSample.Count > 0
                    ? $"Примеры несопоставленных каналов: {string.Join(", ", unmatchedSample.Select(n => $"\"{n}\""))}. "
                    : string.Empty,
                sampleXmlTvIds.Count > 0
                    ? $"Примеры id, которые реально встречаются в загруженном XMLTV: {string.Join(", ", sampleXmlTvIds.Select(id => $"\"{id}\""))}."
                    : "В загруженном XMLTV вообще нет ни одного id каналов.");
        }


        private void TryLearnAlias(
            List<PlaylistDatabaseService.EpgAlias> learned,
            ChannelViewModel channel,
            List<EPGEntry> entries)
        {
            if (string.IsNullOrEmpty(channel.StreamUrl) ||
                (!channel.StreamUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !channel.StreamUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ||
                entries.Count == 0)
            {
                return;
            }

            var first = entries[0];
            if (_epgChannelIdByStreamUrl.TryGetValue(channel.StreamUrl, out var knownId) &&
                string.Equals(knownId, first.ChannelId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var key = EpgNameNormalizer.Normalize(first.ChannelName);
            if (key.Length < 2)
            {
                return;
            }

            learned.Add(new PlaylistDatabaseService.EpgAlias(key, first.ChannelId, channel.StreamUrl));
        }
    }
}
