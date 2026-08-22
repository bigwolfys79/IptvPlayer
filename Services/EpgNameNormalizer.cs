using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace IptvPlayer.Services;

/// <summary>
/// Нормализация имён каналов для сопоставления M3U ↔ XMLTV: убирает шум,
/// из-за которого одно и то же название пишется по-разному у провайдера
/// плейлиста и в XMLTV: регистр, "ё"/"е", суффиксы HD/FHD/4K, таймшифт
/// "+2"/"+4", региональные уточнения в скобках, хвостовые коды стран и
/// маркеры потока (orig/50/60), лишнюю пунктуацию/пробелы.
/// "РБК HD" и "РБК", "НТВ +2" и "НТВ", "France 24 FR" и "France 24"
/// после нормализации дают одну и ту же строку "рбк"/"нтв"/"france 24".
/// Вынесено из EPGService (частично покрывается unit-тестами).
/// </summary>
public static class EpgNameNormalizer
{
    // Служебные слова/суффиксы, которые провайдеры добавляют к названию
    // канала непоследовательно (то в M3U, то в XMLTV, то нигде) — они не
    // несут признака, ПО КОТОРОМУ канал различается, и должны игнорироваться
    // при сравнении названий, иначе "РБК" из XMLTV не совпадёт с "РБК HD" из M3U.
    private static readonly Regex NoiseTokenRegex =
        new(@"\b(hd|fhd|uhd|sd|4k|hevc|full\s*hd)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Уточнения в скобках вида "(Элиста)", "(Тамбов)" — региональные версии
    // одного и того же канала бьют по-разному в M3U и в XMLTV, отбрасываем.
    private static readonly Regex ParenthesesRegex = new(@"\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex NonAlphaNumericRegex = new(@"[^\p{L}\p{Nd}\s]", RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

    // Суффикс вида ".ru"/".ua" — не признак региона канала (это не то же
    // самое, что "(Тамбов)"), а артефакт конкретно этого XMLTV-источника:
    // russia3.xml пишет его в КАЖДОЕ display-name без исключения
    // ("BCU Kids.ru", "1+1 Украина.ru", "+ТВ.ru"). NonAlphaNumericRegex
    // заменяет точку на пробел, а не удаляет — значит "bcu kids.ru"
    // превращался в "bcu kids ru", а не в "bcu kids". Название из плейлиста
    // ("РБК" -> "рбк") никогда не совпадёт с "рбк ru" — из-за этого
    // сопоставление по имени было сломано ПОЛНОСТЬЮ для всех каналов
    // этого источника (0 совпадений из 2065), а не только для тех, что
    // попали в лог как неоднозначные. Удаляем суффикс целиком (не в
    // пробел, а в пустоту), поэтому он и не оставляет постороннего слова.
    private static readonly Regex TrailingCountryCodeRegex = new(@"\.[a-zа-я]{2,3}$", RegexOptions.Compiled);

    // Голый "+" без цифры после него (например "BCU Kids+") — это чаще
    // всего отдельная версия канала (альтернативный/улучшенный поток), а
    // не косметическое отличие вроде HD/4K, поэтому его нельзя стирать
    // как шум — раньше NonAlphaNumericRegex стирал его наравне с точками
    // и запятыми, из-за чего "BCU Kids+" схлопывался с обычным
    // "BCU Kids" и сопоставление по имени становилось неоднозначным (см.
    // BuildNameIndex-warning "bcu kids ru"). "+2"/"+4" (с цифрой) эта
    // строка не трогает — там цифра и так уже сохраняется отдельно.
    private static readonly Regex BarePlusRegex = new(@"\+(?!\d)", RegexOptions.Compiled);

    // Таймшифт-суффикс вида "+2"/"+4"/"+7" в конце названия ("НТВ +2",
    // "Первый канал +4 (Томск)") — провайдер плейлиста плодит таймшфт-
    // дубли каждого федерального канала, а в XMLTV есть только базовое
    // расписание. Программы таймшфт-версии те же, просто сдвинуты по
    // времени, поэтому при сопоставлении суффикс отбрасываем (как и
    // "+0" выше). Цифра обязательно в конце строки: "2+2" (украинский
    // канал) этот regex не трогает — плюс у него не хвостовой.
    // Измерено на реальном плейлисте (2065 каналов): +130 сопоставлений.
    private static readonly Regex TrailingTimeshiftRegex = new(@"\+\s*\d{1,2}\s*$", RegexOptions.Compiled);

    // Разные названия одного и того же канала у провайдера плейлиста и в
    // XMLTV. "Кинозал N (Триколор)" — внутренние киноканалы Триколора, у
    // которых нет собственного публичного EPG; ближайшие по смыслу
    // соседи в XMLTV — "Кино 1"/"Кино 2" (Tviksel). Сравнивать их как
    // каналы некорректно, но расписание кино-канала лучше его отсутствия
    // (измерено: +2 канала; попадает в эвристические имена в сводке).
    private static readonly Regex KinozalAliasRegex = new(@"\bкинозал\b", RegexOptions.Compiled);

    // Код страны в конце названия ("France 24 HD FR", "CNBC HD US",
    // "Sky Atlantic HD DE") — добавляется провайдером плейлиста, в
    // XMLTV его нет. Отбрасываем только ПОСЛЕДНИЙ токен и только если
    // перед ним осталось ещё хотя бы одно слово, иначе "BBC US"
    // превратился бы в голый "bbc", а канал с настоящим именем "360"
    // (есть в XMLTV) — в пустую строку. Только точные вхождения из
    // списка: "НТВ Мир" не трогается ("мир" — не код). "international" и
    // "международный" — маркер международной версии того же канала, не
    // отдельное имя ("Кино 1 International" == "Кино 1", "1+1
    // Международный" == "1+1"). Измерено: +87 сопоставлений.
    private static readonly HashSet<string> TrailingCountryCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "uk", "us", "fr", "de", "it", "es", "pl", "br", "cn", "jp", "kr", "in", "tr", "ua", "by",
        "kz", "az", "ge", "am", "lt", "lv", "ee", "rs", "hu", "ro", "bg", "gr", "nl", "se", "no",
        "dk", "fi", "at", "ch", "be", "pt", "ie", "cz", "sk", "si", "hr", "md", "il", "ae", "sa",
        "eg", "za", "ng", "th", "vn", "id", "my", "sg", "au", "nz", "ca", "mx", "ar", "cl", "co",
        "pe", "eu", "intl", "international", "международный",
    };

    // Маркеры варианта потока в конце названия: "orig" (оригинальный
    // источник), "50"/"60" (50/60 fps — плейлист даёт такие дубли почти
    // каждого канала: "Россия 1 HD orig", "Матч ТВ HD 50"), "hdr" и
    // разрешения "1080p"/"720p"/... — всё это не часть имени канала, и
    // в XMLTV таких суффиксов нет. Правила те же, что у кодов стран:
    // только хвостовой токен, только если перед ним есть ещё слова.
    // Измерено: +274 сопоставления (самый крупный прирост одного слоя).
    private static readonly HashSet<string> TrailingStreamMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "orig", "50", "60", "hdr", "1080p", "1080i", "720p", "2160p", "50p", "60p",
    };

    // Приоритет качества нужен только для детерминированного выбора среди
    // чисто качественных дублей (см. EpgSourceMerger.BuildNameIndex) —
    // расписание передач у HD/4K-версии практически всегда совпадает с SD,
    // поэтому сам факт "какой именно id выбрать" не влияет на корректность
    // программы, важна только предсказуемость выбора.
    private static readonly Dictionary<string, int> QualityRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sd"] = 1,
        ["hevc"] = 1,
        ["hd"] = 2,
        ["fhd"] = 3,
        ["full hd"] = 3,
        ["4k"] = 4,
        ["uhd"] = 4,
    };

    /// <summary>Базовая нормализация: весь шум убран, включая таймшифт и скобки.</summary>
    internal static string Normalize(string? name)
        => NormalizeCore(name, keepQualifiers: false, keepTimeshift: false);

    /// <summary>
    /// Как Normalize, но таймшифт-суффикс "+2"/"+4" в конце
    /// СОХРАНЯЕТСЯ ("первый канал 2" != "первый канал"). Нужен для строгого
    /// ключа таблицы epg-name-map: провайдер выдаёт таймшифт-версиям
    /// собственные tvg-id, и строгий ключ даёт каналу его родное
    /// расписание, а не базовое со сдвигом.
    /// </summary>
    internal static string NormalizePreservingTimeshift(string? name)
        => NormalizeCore(name, keepQualifiers: false, keepTimeshift: true);

    /// <summary>
    /// Как Normalize, но НЕ трогает содержимое скобок — только
    /// убирает суффикс качества и служебный ".ru"/".ua". Нужен, чтобы
    /// отличить "разница только в качестве" (HD/4K/SD) от "разница ещё в
    /// чём-то" (например регион в скобках) ДО того, как скобки стёрты —
    /// см. EpgSourceMerger.BuildNameIndex.
    /// </summary>
    internal static string NormalizeKeepQualifiers(string? name)
        => NormalizeCore(name, keepQualifiers: true, keepTimeshift: false);

    private static string NormalizeCore(string? name, bool keepQualifiers, bool keepTimeshift)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var s = name.Trim().ToLowerInvariant().Replace('ё', 'е');
        s = TrailingCountryCodeRegex.Replace(s, string.Empty);
        if (!keepQualifiers)
        {
            // Региональные уточнения в скобках бьют по-разному в M3U и в
            // XMLTV — стираем вместе со скобками.
            s = ParenthesesRegex.Replace(s, " ");
        }
        s = NoiseTokenRegex.Replace(s, " ");
        s = s.Replace("+0", " ");
        if (!keepTimeshift)
        {
            s = TrailingTimeshiftRegex.Replace(s, " ");
        }
        s = BarePlusRegex.Replace(s, " plus ");
        if (keepQualifiers)
        {
            s = s.Replace("(", " ").Replace(")", " "); // скобки убираем, содержимое — нет
        }
        s = NonAlphaNumericRegex.Replace(s, " ");
        s = MultiSpaceRegex.Replace(s, " ").Trim();
        s = KinozalAliasRegex.Replace(s, "кино");

        return StripTrailingMarkers(s);
    }

    /// <summary>
    /// Срезает с конца уже нормализованного названия хвостовые токены,
    /// которые не являются частью имени канала: коды стран (см.
    /// TrailingCountryCodes) и маркеры варианта потока (см.
    /// TrailingStreamMarkers). Режем только пока перед обрезаемым токеном
    /// есть ещё хотя бы одно слово — "360" или "BBC" дальше резать нельзя.
    /// </summary>
    private static string StripTrailingMarkers(string normalized)
    {
        if (string.IsNullOrEmpty(normalized))
        {
            return normalized;
        }

        var tokens = normalized.Split(' ');
        var keep = tokens.Length;
        while (keep > 1)
        {
            var last = tokens[keep - 1];
            if (!TrailingCountryCodes.Contains(last) && !TrailingStreamMarkers.Contains(last))
            {
                break;
            }
            keep--;
        }

        if (keep == tokens.Length)
        {
            return normalized;
        }

        return string.Join(" ", tokens, 0, keep);
    }

    /// <summary>Приоритет качества потока в названии (0 — не указано).</summary>
    internal static int GetQualityRank(string rawName)
    {
        var best = 0;
        foreach (Match m in NoiseTokenRegex.Matches(rawName))
        {
            var token = MultiSpaceRegex.Replace(m.Value.ToLowerInvariant(), " ").Trim();
            if (QualityRank.TryGetValue(token, out var rank))
            {
                best = Math.Max(best, rank);
            }
        }
        return best;
    }
}
