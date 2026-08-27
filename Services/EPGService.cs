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
        // ВРЕМЕННО для проверки гипотезы про тормоз из-за логирования на
        // каждый канал в GetEPGEntriesAsync (файловый sink пишет на каждый
        // вызов). LogMatchSummaryAsync уже даёт агрегированную сводку по
        // всем каналам сразу после загрузки EPG, так что при выключенном
        // флаге диагностическая информация не теряется полностью — просто
        // нет дублирующего лога на каждый отдельный вызов
        // GetEPGEntriesAsync. Вернуть true после проверки.
        private static readonly bool LogPerChannelDiagnostics = false;

        private readonly IChannelRepository _channelRepository;
        private readonly ICacheService _cacheService;
        private readonly ISettingsService _settingsService;
        private readonly IXmlTvService _xmlTvService;
        private readonly ILogger<EPGService> _logger;

        private Dictionary<string, List<EPGEntry>> _entriesByChannelId = new(StringComparer.OrdinalIgnoreCase);

        // Индекс "нормализованное имя канала -> программы", резервный путь
        // сопоставления для каналов без tvg-id (см. класс-комментарий выше).
        private Dictionary<string, List<EPGEntry>> _entriesByNormalizedName = new(StringComparer.OrdinalIgnoreCase);

        // Логотипы из <icon src> самих XMLTV-источников — резервный источник
        // ChannelViewModel.LogoUrl для каналов без tvg-logo в плейлисте (см.
        // ApplyMissingLogosAsync). Сопоставление только по надёжному tvg-id,
        // без резервного пути по имени — цена ошибки в лого низкая, но не
        // настолько, чтобы рисковать неточным сопоставлением по имени.
        private Dictionary<string, string> _iconsByChannelId = new(StringComparer.OrdinalIgnoreCase);

        // Таблица "имя канала -> tvg-id" (Assets/epg-name-map.json), собранная
        // сервисом epg.one/setup-playlist из ЭТОГО ЖЕ плейлиста (провайдер
        // lunexas/Edem). Плейлист сам tvg-id не содержит (2065 из 2065 каналов
        // без него), но имена в таблице и в плейлисте совпадают практически
        // 1:1 — таблица даёт надёжный путь сопоставления по tvg-id для 2041
        // из 2065 каналов, включая ПРАВИЛЬНЫЕ таймшфт-расписания ("Первый
        // канал +2" имеет собственный tvg-id, а не расписание базового
        // канала со сдвигом). Строгий ключ сохраняет таймшифт-суффикс, мягкий
        // (короткое базовое имя) — резерв для вариантов написания.
        private Dictionary<string, string> _tvgIdByStrictName = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _tvgIdByLenientName = new(StringComparer.OrdinalIgnoreCase);
        private bool _nameMapLoadAttempted;

        // Имена свойств — ровно как в Assets/epg-name-map.json (маленькими
        // буквами): System.Text.Json по умолчанию чувствителен к регистру, и
        // без этих атрибутов файл молча разбирается в 0 записей.
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

        // tvg-id -> URL логотипа из той же таблицы (tvg-logo от epg.one):
        // резерв, если у канала в загруженном XMLTV нет своего <icon src>.
        private Dictionary<string, string> _logoByTvgId = new(StringComparer.OrdinalIgnoreCase);

        private bool _epgLoaded;
        private DateTime _lastSuccessfulLoad = DateTime.MinValue;
        private readonly TimeSpan _minReloadInterval = TimeSpan.FromMinutes(5);

        // Сообщение "Пропуск перезагрузки EPG" пишется один раз на эпизод
        // пропуска, а не на каждый вызов: RecalculateCurrentProgramsAsync и
        // минутный таймер зовут EnsureEpgLoadedAsync по разу на КАЖДЫЙ канал
        // (2000+ вызовов), раздувая лог на мегабайты и топя в нём ошибки.
        private bool _skipLogged;

        // Раньше _epgLoaded читался/писался без какой-либо защиты от гонки —
        // если несколько вызовов GetEPGEntriesAsync (например, из
        // RecalculateCurrentProgramsAsync, которая теперь отдаёт управление в
        // UI через Task.Yield() между пачками каналов) попадали в
        // EnsureEpgLoadedAsync ПОКА первый вызов ещё не дошёл до
        // "_epgLoaded = true" (а это занимает 16-25 секунд реального скачивания
        // XMLTV), каждый такой вызов видел _epgLoaded == false и запускал
        // СВОЮ полную загрузку заново — отсюда несколько параллельных
        // скачиваний одних и тех же источников (видно в логе: russia3.xml
        // загружался 3 раза подряд) и как следствие таймауты/SocketException
        // на источниках под такой нагрузкой. Семафор _loadLock в EpgViewModel
        // от этого не защищал — он сериализует только LoadEPGAsync/
        // LoadEPGForChannelAsync МЕЖДУ СОБОЙ, а не вызовы EnsureEpgLoadedAsync
        // изнутри одного и того же прохода. Теперь конкурентные вызовы не
        // запускают новую загрузку, а ждут ту же самую, что уже в процессе.
        private Task? _loadingTask;
        private readonly object _loadingTaskGate = new();

        // Токен отмены идущей загрузки: перекачка EPG при переключении
        // плейлиста / принудительном «Обновить» отменяет старую (её источники
        // уже не актуальны), а не гоняется с ней за сеть и дисковый кэш.
        private System.Threading.CancellationTokenSource _loadCts = new();

        // ApplyMissingLogosAsync мутирует ChannelViewModel.LogoUrl — объекты,
        // на которые подписаны x:Bind-привязки списка каналов. При RefreshEPGAsync
        // вся загрузка сервиса идёт в Task.Run (пул потоков), и уведомления
        // INotifyPropertyChanged приходили НЕ с UI-потока: компилированные
        // x:Bind сами вызовы не маршализируют, часть обновлений терялась
        // (логотипы не появлялись до пересборки списка), а попытка обновить
        // зависимый элемент с фонового потока оборачивалась RPC_E_WRONG_THREAD.
        // Исключение глоталось fire-and-forget вызовом из диалога настроек —
        // LoadEPGAsync после него уже не выполнялся: индикатор гас, а программы
        // так и не появлялись до перезапуска приложения. Захватываем
        // DispatcherQueue UI-потока (EPGService создаётся на нём в MainPage);
        // вне UI-контекста (тесты) остаётся null и мутации идут инлайн.
        private readonly DispatcherQueue? _uiDispatcher;

        public EPGService(
            IChannelRepository channelRepository,
            ICacheService cacheService,
            ISettingsService settingsService,
            IXmlTvService xmlTvService,
            ILogger<EPGService> logger)
        {
            _channelRepository = channelRepository;
            _cacheService = cacheService;
            _settingsService = settingsService;
            _xmlTvService = xmlTvService;
            _logger = logger;
            _uiDispatcher = DispatcherQueue.GetForCurrentThread();
        }

        // Раньше список каналов кэшировался в ICacheService под ключом
        // "channels" — но GetAllChannelsAsync() и так копирует только
        // List<>, а не объекты внутри (см. ChannelRepository), так что кэш
        // не экономил ничего измеримого. Зато CacheService дублирует SetAsync
        // на диск (channels.json), а GetAsync при промахе в памяти читает
        // ИМЕННО с диска — если между запусками приложения этот файл успел
        // сохраниться, а первым вызовом после рестарта окажется именно
        // GetChannelsAsync() (до того как ChannelRepository наполнится из
        // плейлиста), он вернёт задесериализованные с диска ОБЪЕКТЫ прошлой
        // сессии, а не текущие из ChannelRepository. Дальше в приложении
        // существуют два непересекающихся набора ChannelViewModel — мутации
        // (например ApplyMissingLogosAsync, обновление IsPlaying/EPGEntries)
        // в "живые" объекты репозитория такой UI не увидит. Отдаём список
        // репозитория напрямую — так GetChannelsAsync() и любой другой код,
        // работающий с _channelRepository, всегда смотрят на одни и те же
        // инстансы, независимо от порядка вызовов при старте.
        public Task<List<ChannelViewModel>> GetChannelsAsync()
        {
            return _channelRepository.GetAllChannelsAsync();
        }

        public async Task<List<EPGEntry>> GetEPGEntriesAsync(int channelId)
        {
            await EnsureEpgLoadedAsync();

            var channel = await _channelRepository.GetChannelByIdAsync(channelId);
            if (channel == null)
            {
                // Раньше это было тихим "return empty" — при разрыве между
                // ChannelRepository и списком каналов, который видит UI (см.
                // разбор бага в MainPage.InitializeAsync), выглядело так,
                // будто у канала просто нет программ, хотя на самом деле
                // самого канала не существовало в репозитории вовсе.
                if (LogPerChannelDiagnostics)
                {
                    _logger.LogWarning(
                        "Канал с id={ChannelId} не найден в ChannelRepository — EPG для него не может быть найден.",
                        channelId);
                }
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
                    // Не ошибка, а особенность плейлиста (нет tvg-id) — но полезно
                    // видеть в логе, что сопоставление прошло по резервному пути,
                    // а не по надёжному tvg-id, на случай если оно окажется неточным.
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

        public async Task RefreshEPGAsync()
        {
            _cacheService.Clear();
            _epgLoaded = false;

            // Если прямо сейчас идёт фоновая загрузка — она стартовала со СТАРЫМ
            // набором источников, и EnsureEpgLoadedAsync(force:true) просто
            // присоединилась бы к ней (возвратила бы её Task из _loadingTask):
            // новые источники не скачались бы до перезапуска приложения, а список
            // каналов после "Готово" оставался без иконок и текущей передачи.
            // Дожидаемся идущую загрузку и запускаем новую принудительную.
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
                    // Причина уже залогирована внутри DoEnsureEpgLoadedAsync —
                    // принудительная загрузка ниже выполнится в любом случае.
                }
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

            // Дожидаемся идущую загрузку (со старым набором источников), как
            // в RefreshEPGAsync, — иначе EnsureEpgLoadedAsync(force:true)
            // присоединится к ней и новые источники не подхватятся.
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
                    // Причина уже залогирована внутри DoEnsureEpgLoadedAsync.
                }
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
                return (byId.ToList(), MatchMethod.TvgId);
            }

            var strictKey = EpgNameNormalizer.NormalizePreservingTimeshift(channel.Name);
            if (!string.IsNullOrEmpty(strictKey) &&
                _tvgIdByStrictName.TryGetValue(strictKey, out var strictId) &&
                _entriesByChannelId.TryGetValue(strictId, out var strictEntries))
            {
                return (strictEntries.ToList(), MatchMethod.NameMap);
            }

            var lenientKey = EpgNameNormalizer.Normalize(channel.Name);
            if (!string.IsNullOrEmpty(lenientKey) &&
                _tvgIdByLenientName.TryGetValue(lenientKey, out var lenientId) &&
                _entriesByChannelId.TryGetValue(lenientId, out var lenientEntries))
            {
                return (lenientEntries.ToList(), MatchMethod.NameMap);
            }

            if (!string.IsNullOrEmpty(lenientKey) &&
                _entriesByNormalizedName.TryGetValue(lenientKey, out var byName))
            {
                return (byName.ToList(), MatchMethod.Name);
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

                // Мягкий ключ: несколько raw-имён схлопываются в один (срезаны
                // таймшифт/HD/orig) — оставляем id самого КОРОТКОГО raw-имени:
                // это базовый вариант канала ("Первый канал", а не "+2"/"+4").
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
                    // Загрузка уже идёт (запущена другим конкурентным вызовом) —
                    // присоединяемся к ней вместо того, чтобы качать источники
                    // ещё раз параллельно.
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
                // Таблица "имя -> tvg-id" нужна уже для первого сопоставления,
                // грузим до источников (файл локальный, ~120 КБ).
                LoadTvgIdNameMap();

                var settings = await _settingsService.LoadAsync();
                var enabledSources = settings.GetActiveEpgSources()
                    .Where(s => s.IsEnabled).ToList();

                // Кэш-файлы удалённых из настроек источников (по 30 МБ на
                // XMLTV) больше не нужны — чистим раз при загрузке EPG.
                EpgCacheStore.CleanupOrphans(
                    enabledSources.Select(s => s.Url));

                // Периодичность обновления EPG из настроек (1/3/7 дней):
                // пока кэш источника младше maxAge, XmlTvService берёт его с
                // диска без сети. 0 = "только вручную" — MaxValue, явный
                // "Обновить EPG" всё равно перекачает (он чистит кэш целиком).
                TimeSpan maxAge = settings.EpgRefreshDays > 0
                    ? TimeSpan.FromDays(settings.EpgRefreshDays)
                    : TimeSpan.MaxValue;

                if (enabledSources.Count == 0)
                {
                    _logger.LogWarning(
                        "Нет ни одного включённого источника EPG (всего в настройках: {Total}). " +
                        "EPG будет пустым, пока в настройках не добавите/не включите хотя бы один источник.",
                        settings.GetActiveEpgSources().Count);
                }

                // Скачивание/парсинг источников — async (XmlTvService сам
                // уводит парсинг в пул потоков). А вот дальнейшее слияние —
                // проверка пересечений по времени для каждой программы,
                // сортировка ~400к записей, построение индекса имён — чистая
                // CPU-работа, которая раньше шла в продолжении await прямо на
                // UI-потоке и морозила интерфейс при старте. Выносим одним
                // куском в пул потоков (EpgSourceMerger.Merge).
                var sourceResults = new List<XmlTvLoadResult>();
                foreach (var source in enabledSources)
                {
                    try
                    {
                        sourceResults.Add(await _xmlTvService.LoadAsync(source, maxAge, ct));
                    }
                    catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // Переключение плейлиста/принудительное обновление —
                        // выходим целиком, не помечая EPG загруженным.
                        _logger.LogInformation("Загрузка EPG отменена (источник {Url}).", SecretProtector.Mask(source.Url));
                        return;
                    }
                    catch (Exception ex)
                    {
                        // Один недоступный/битый источник не должен рушить остальные —
                        // пропускаем его и идём дальше по списку. Раньше это было
                        // полностью молчаливым catch{continue} — если у вас EPG не
                        // появлялся, вы никак не могли узнать, что именно (таймаут?
                        // 404? битый XML?) отвалилось. Теперь причина попадает в лог.
                        _logger.LogError(ex, "Источник EPG недоступен/битый: {Url}", SecretProtector.Mask(source.Url));
                    }
                }

                var (byChannel, iconsByChannelId, nameIndex) = await Task.Run(() => EpgSourceMerger.Merge(sourceResults, _logger));

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

                // Подставляем логотип из XMLTV каналам без tvg-logo — до сводки
                // по сопоставлению, чтобы не задерживать её, если репозиторий
                // окажется недоступен (ApplyMissingLogosAsync сама логирует
                // и глотает свою ошибку, не мешая остальной загрузке).
                await ApplyMissingLogosAsync();

                // Сводка сопоставления M3U-плейлиста и XMLTV (с учётом резервного
                // сопоставления по имени) — без этого расхождение было видно только
                // по одному предупреждению на канал при клике на него. Здесь же сразу
                // после загрузки XMLTV считаем итог по ВСЕМ каналам разом.
                await LogMatchSummaryAsync();
            }
            finally
            {
                // Обязательно очищаем ссылку на завершённый Task — иначе
                // следующий реальный вызов EnsureEpgLoadedAsync (после
                // истечения _minReloadInterval или через force) навсегда
                // получал бы уже завершённый Task вместо запуска новой загрузки.
                lock (_loadingTaskGate)
                {
                    _loadingTask = null;
                }
            }
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
            // Раньше здесь был ранний выход только по _iconsByChannelId == 0:
            // если XMLTV-источник не отдаёт <icon>, логотипы из таблицы epg.one
            // (tvg-logo) вообще не рассматривались.
            if (_iconsByChannelId.Count == 0 && _logoByTvgId.Count == 0)
            {
                return;
            }

            List<ChannelViewModel> channels;
            try
            {
                channels = await _channelRepository.GetAllChannelsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ApplyMissingLogosAsync: не удалось получить список каналов.");
                return;
            }

            var fills = new List<(ChannelViewModel Channel, string IconUrl)>();

            foreach (var channel in channels)
            {
                if (!string.IsNullOrWhiteSpace(channel.LogoUrl))
                {
                    continue;
                }

                // Кандидаты tvg-id: собственный из плейлиста, затем строгий и
                // мягкий ключи таблицы (плейлист tvg-id не содержит, поэтому
                // раньше этот метод не срабатывал ни для одного канала).
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

                    // Приоритет — иконка из самого XMLTV; если источник её не
                    // отдаёт, берём tvg-logo из таблицы epg.one.
                    if (_iconsByChannelId.TryGetValue(id, out var iconUrl) ||
                        _logoByTvgId.TryGetValue(id, out iconUrl))
                    {
                        fills.Add((channel, iconUrl));
                        break;
                    }
                }
            }

            if (fills.Count == 0)
            {
                return;
            }

            void ApplyFills()
            {
                var filled = 0;
                foreach (var (channel, iconUrl) in fills)
                {
                    // Повторная проверка: между подбором кандидатов (возможно,
                    // на пуле потоков) и применением логотип канала могли
                    // заполнить другим путём.
                    if (string.IsNullOrWhiteSpace(channel.LogoUrl))
                    {
                        channel.LogoUrl = iconUrl;
                        filled++;
                    }
                }

                if (filled > 0)
                {
                    _logger.LogInformation(
                        "Подставлено логотипов (XMLTV icon / таблица epg.one): {Count}.", filled);
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
                channels = await _channelRepository.GetAllChannelsAsync();
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

            foreach (var channel in channels)
            {
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
