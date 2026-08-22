// Офлайн-анализ сопоставления плейлист <-> XMLTV на реальных кэшах приложения.
// Воспроизводит логику EPGService (NormalizeChannelName / BuildNameIndex /
// MatchChannel) и меряет, сколько каналов сопоставляется при разных
// дополнительных правилах нормализации. Запуск:
//   node match-analysis.mjs [baseline|L1|L2|L3|L4|all|diff ...]
import { readFileSync } from 'node:fs';

const PKG = 'C:/Users/bigwo/AppData/Local/Packages/AD674664-F9A8-474B-871C-B328CDB993C1_1z32rh13vfry6/LocalCache/Local/IptvPlayer';
// Порядок как в настройках: сначала open-epg russia3.xml (числовые id), затем epg.one/ru.xml.gz
const RUSSIA3 = PKG + '/cache/3D1B62B4C10EE9A8410C23AEB017089078E25E6695342AB24212DDE8EA817BBB.json';
const RUGZ = PKG + '/cache/A622E8F8CA9100BED4D512D2F91A7C4D5316CE61F9EF03675CE020B24E304F89.json';

const unesc = (s) => JSON.parse('"' + s + '"');

// Кэш XmlTvService: {"Entries":[{EventId,ChannelId,ChannelName,..,StartTime,..},..]}
// Стримим сплитом по началу записи — JSON.parse 164 МБ целиком рискован по памяти.
function scanSource(path) {
  const text = readFileSync(path, 'utf8');
  const map = new Map(); // id -> {name, count, minStart}
  const reId = /"ChannelId":"((?:[^"\\]|\\.)*)"/;
  const reName = /"ChannelName":"((?:[^"\\]|\\.)*)"/;
  const reStart = /"StartTime":"((?:[^"\\]|\\.)*)"/;
  const parts = text.split('{"EventId":"');
  for (let i = 1; i < parts.length; i++) {
    const p = parts[i];
    const idM = reId.exec(p);
    if (!idM) continue;
    const id = unesc(idM[1]);
    const nameM = reName.exec(p);
    const startM = reStart.exec(p);
    const start = startM ? unesc(startM[1]) : '9999';
    let e = map.get(id);
    if (!e) {
      e = { name: nameM ? unesc(nameM[1]) : id, count: 0, minStart: start };
      map.set(id, e);
    }
    e.count++;
    if (start < e.minStart) {
      e.minStart = start;
      if (nameM) e.name = unesc(nameM[1]);
    }
  }
  return map;
}

// Слияние как в DoEnsureEpgLoadedAsync: первый источник приоритетнее.
// Имя канала = ChannelName записи с самой ранней StartTime (BuildNameIndex
// берёт entries[0] после сортировки по StartTime).
function merge(m1, m2) {
  const out = new Map();
  for (const [id, e] of m2) out.set(id, { ...e });
  for (const [id, e] of m1) {
    const ex = out.get(id);
    if (!ex) out.set(id, { ...e });
    else {
      ex.count += e.count;
      if (e.minStart < ex.minStart) { ex.minStart = e.minStart; ex.name = e.name; }
    }
  }
  return out;
}

// ---------- Порт нормализации из EPGService (C# -> JS) ----------
const NOISE_TOKEN = /\b(hd|fhd|uhd|sd|4k|hevc|full\s*hd)\b/gi;
const QUALITY_RANK = { sd: 1, hevc: 1, hd: 2, fhd: 3, 'full hd': 3, '4k': 4, uhd: 4 };

const TIMESHIFT = /\+\s*\d{1,2}\s*$/;            // L1: "НТВ +2" -> "НТВ"
const COUNTRY_CODES = new Set(('uk,us,fr,de,it,es,pl,br,cn,jp,kr,in,tr,ua,by,kz,az,ge,am,lt,lv,ee,rs,hu,' +
  'ro,bg,gr,nl,se,no,dk,fi,at,ch,be,pt,ie,cz,sk,si,hr,md,il,ae,sa,eg,za,ng,th,vn,id,my,sg,au,nz,ca,mx,' +
  'ar,cl,co,pe,eu,la,_INTL,intl,international,международный').split(','));
const NUM_WORDS = { 1: 'первый', 2: 'второй', 3: 'третий', 4: 'четвертый', 5: 'пятый', 6: 'шестой',
  7: 'седьмой', 8: 'восьмой', 9: 'девятый', 10: 'десятый', 11: 'одиннадцатый', 12: 'двенадцатый' };
const NOISE_WORDS = new Set(['канал', 'channel', 'тв', 'tv']); // L5

function qualityRank(rawName) {
  let best = 0;
  for (const m of rawName.matchAll(NOISE_TOKEN)) {
    const token = m[0].toLowerCase().replace(/\s+/g, ' ').trim();
    const r = QUALITY_RANK[token];
    if (r > best) best = r;
  }
  return best;
}

// flags: {L1 timeshift, L2 country, L3 numbers, L5 noiseWords}
function normalize(name, flags = {}) {
  if (!name || !name.trim()) return '';
  let s = name.trim().toLowerCase().replace(/ё/g, 'е');
  s = s.replace(/\.([a-zа-я]{2,3})$/, '');
  if (flags.keepQualifiers !== true) s = s.replace(/\([^)]*\)/g, ' ');
  s = s.replace(NOISE_TOKEN, ' ');
  s = s.replace(/\+0/g, ' ');
  if (flags.L1) s = s.replace(TIMESHIFT, ' ');
  s = s.replace(/\+(?!\d)/g, ' plus ');
  if (flags.keepQualifiers === true) s = s.replace(/[()]/g, ' ');
  s = s.replace(/[^\p{L}\p{Nd}\s]/gu, ' ');
  s = s.replace(/\s+/g, ' ').trim();
  let tokens = s ? s.split(' ') : [];
  if (flags.L8) {
    tokens = tokens.map((t) => (t === 'кинозал' ? 'кино' : t));
  }
  if (flags.L3) {
    tokens = tokens.map((t, i) =>
      (NUM_WORDS[t] && tokens[i + 1] === 'канал') ? NUM_WORDS[t] : t);
  }
  if (flags.L2) {
    while (tokens.length > 1 && COUNTRY_CODES.has(tokens[tokens.length - 1])) tokens.pop();
  }
  if (flags.L7) {
    while (tokens.length > 1 && STREAM_TAIL2.has(tokens[tokens.length - 1])) tokens.pop();
  }
  if (flags.L6) {
    while (tokens.length > 1 && STREAM_TAIL.has(tokens[tokens.length - 1])) tokens.pop();
  }
  if (flags.L5) {
    tokens = tokens.filter((t, i, a) => a.length > 1 && NOISE_WORDS.has(t) ? false : true);
    if (tokens.length === 0) tokens = [''];
  }
  return tokens.join(' ');
}

// Порт BuildNameIndex. flags.L4: неоднозначные имена не выбрасывать,
// а выбирать детерминированно лучшего кандидата (максимум программ, затем качество).
function buildNameIndex(byChannel, flags = {}) {
  const groups = new Map();
  for (const [id, e] of byChannel) {
    if (e.count === 0) continue;
    const norm = normalize(e.name, flags);
    if (!norm) continue;
    if (!groups.has(norm)) groups.set(norm, []);
    groups.get(norm).push({ id, raw: e.name, count: e.count });
  }
  const index = new Map();
  let qualityDups = 0;
  const ambiguous = [];
  for (const [norm, group] of groups) {
    if (group.length === 1) { index.set(norm, group[0]); continue; }
    const keepQ = new Set(group.map((g) => normalize(g.raw, { ...flags, keepQualifiers: true })));
    if (keepQ.size === 1) {
      const chosen = group.slice().sort((a, b) => b.count - a.count || qualityRank(b.raw) - qualityRank(a.raw))[0];
      qualityDups++;
      index.set(norm, chosen);
    } else if (flags.L4) {
      const chosen = group.slice().sort((a, b) => b.count - a.count || qualityRank(b.raw) - qualityRank(a.raw))[0];
      ambiguous.push(norm);
      index.set(norm, chosen);
    } else {
      ambiguous.push(norm);
    }
  }
  // L8: дополнительные ключи без брендового префикса (Tviksel Кино 2 HD ->
  // "кино 2"), регистрируются ТОЛЬКО если такой ключ ещё не занят прямым
  // именем — иначе точные совпадения не пострадают (Tviksel Детское кино
  // не должен вытеснить настоящий "Детское кино").
  if (flags.L8) {
    const altAdds = [];
    for (const [norm, chosen] of index) {
      const parts = norm.split(' ');
      if (parts[0] === 'tviksel' && parts.length > 1) {
        const alt = parts.slice(1).join(' ');
        if (alt && !index.has(alt)) altAdds.push([alt, chosen]);
      }
    }
    for (const [alt, chosen] of altAdds) index.set(alt, chosen);
  }

  return { index, qualityDups, ambiguous };
}

// ---------- Данные ----------
console.error('Сканирую кэши XMLTV (237 МБ, секунды)...');
const t0 = Date.now();
const merged = merge(scanSource(RUSSIA3), scanSource(RUGZ));
console.error(`XMLTV: ${merged.size} id, ${Date.now() - t0} мс`);
const playlist = JSON.parse(readFileSync(PKG + '/playlist_cache.json', 'utf8'));
const names = playlist.Channels.map((c) => c.Name);
console.error(`Плейлист: ${names.length} каналов`);

// ---------- Измерение ----------
function measure(label, flags) {
  const { index, qualityDups, ambiguous } = buildNameIndex(merged, flags);
  let matched = 0;
  const unmatched = [];
  for (const n of names) {
    if (index.has(normalize(n, flags))) matched++;
    else unmatched.push(n);
  }
  console.log(`\n=== ${label} ===`);
  console.log(`сопоставлено: ${matched}/${names.length} (${(100 * matched / names.length).toFixed(1)}%)` +
    ` | дубли по качеству: ${qualityDups} | неоднозначных имён: ${ambiguous.length}`);
  console.log(`несопоставлено (${unmatched.length}), примеры: ` +
    unmatched.slice(0, 25).map((n) => `"${n}"`).join(', '));
  return { matched, unmatched, index, flags };
}

const base = {};
const L1 = { L1: true };
const L12 = { L1: true, L2: true };
const L123 = { L1: true, L2: true, L3: true };
const all = { L1: true, L2: true, L3: true, L4: true };
const all5 = { L1: true, L2: true, L3: true, L4: true, L5: true };

const res = {};
res.base = measure('BASELINE (текущая логика приложения)', base);
res.L1 = measure('L1: + таймшифт +N', L1);
res.L12 = measure('L2: + коды стран (UK/FR/US/...)', L12);
res.L123 = measure('L3: + числа словами (5 канал -> пятый канал)', L123);
res.all = measure('L4: + неоднозначные -> лучший кандидат', all);
res.all5 = measure('L5: + шумовые слова (канал/tv/тв)', all5);

// Что добавил каждый слой относительно предыдущего
console.log('\n=== Дельты ===');
for (const [a, b] of [['base', 'L1'], ['L1', 'L12'], ['L12', 'L123'], ['L123', 'all'], ['all', 'all5']]) {
  console.log(`${a} -> ${b}: +${res[b].matched - res[a].matched}`);
}

// Для диффа: какие имена добавились на конкретном слое
const [, , ...rest] = process.argv;
if (rest[0] === 'diff') {
  const from = res[rest[1] || 'base'].unmatched;
  const toFlags = res[rest[2] || 'all'].flags;
  const idx2 = buildNameIndex(merged, toFlags).index;
  const gained = from.filter((n) => idx2.has(normalize(n, toFlags)));
  console.log(`\nДобавились на ${rest[2] || 'all'} (${gained.length}): ` +
    gained.slice(0, 60).map((n) => `"${n}"`).join(', '));
}

// Доп. проверки: конфиг без L3/L5, поиск похожих имён в XMLTV, структура несопоставленных
const best = { L1: true, L2: true, L4: true };
const STREAM_TAIL2 = new Set(['hdr','1080p','1080i','720p','2160p','50p','60p']);
const STREAM_TAIL = new Set(['orig','50','60','hdr','1080p','1080i','720p','2160p','50p','60p']);
const rBest = measure('BEST = L1+L2+L4 (без L3/L5)', best);

if (rest[0] === 'search') {
  const { index } = buildNameIndex(merged, best);
  const uniq = new Map(); // rawName -> count
  for (const [id, e] of merged) uniq.set(e.name, (uniq.get(e.name) || 0) + e.count);
  for (const q of rest.slice(1)) {
    const ql = q.toLowerCase();
    const hits = [...uniq.keys()].filter((n) => n.toLowerCase().includes(ql)).slice(0, 12);
    console.log(`\nXMLTV *${q}*: ${hits.join(' | ') || '— ничего'}`);
  }
}
if (rest[0] === 'ceiling') {
  // Сколько несопоставленных — каналы с кодом страны в конце (у ru-источников их нет в принципе)
  const code = /\b(UK|US|FR|DE|IT|ES|PL|BR|CN|JP|KR|IN|TR|UA|NL|SE|NO|DK|FI|AT|CH|BE|PT|IE|CZ|SK|HU|RO|BG|GR|AU|NZ|CA|MX|AR)\b\s*$/;
  const foreign = rBest.unmatched.filter((n) => code.test(n));
  console.log(`\nИз ${rBest.unmatched.length} несопоставленных ${foreign.length} — иностранные с кодом страны в конце (в ru-EPG их нет)`);
  console.log(`Реальный резерв дальше: ${rBest.unmatched.length - foreign.length}`);
}

measure('BEST+L6 = L1+L2+L4+orig/50/60', { L1: true, L2: true, L4: true, L6: true });
measure('L7: + hdr/1080p/720p в хвост', { L1: true, L2: true, L4: true, L6: true, L7: true });
if (rest[0] === 'fuzzy') {
  // Последний резерв: нечёткое совпадение — токены одного имени являются
  // подмножеством токенов другого (>=2 токенов у длиннейшего), без учёта порядка.
  const { index } = buildNameIndex(merged, best);
  const xmlTokens = new Map(); // norm -> tokens
  for (const norm of index.keys()) xmlTokens.set(norm, norm.split(' '));
  let fuzz = 0; const samples = [];
  for (const n of rBest.unmatched) {
    const norm = normalize(n, best);
    const pt = norm.split(' ');
    if (pt.length < 1 || !norm) continue;
    let hit = null;
    for (const [xn, xt] of xmlTokens) {
      const [a, b] = pt.length <= xt.length ? [pt, xt] : [xt, pt];
      if (b.length < 2 || a.length === 0) continue;
      const bs = new Set(b);
      if (a.length >= Math.max(2, b.length - 1) && a.every((t) => bs.has(t))) { hit = xn; break; }
    }
    if (hit) { fuzz++; if (samples.length < 40) samples.push(`"${n}"~"${hit}"`); }
  }
  console.log(`\nFUZZY поверх BEST: ещё +${fuzz}`);
  console.log(samples.join('\n'));
}

// L6: хвостовые маркеры потока orig/50/60 (частота кадров / оригинал)
const best6 = { L1: true, L2: true, L4: true, L6: true };

// L8: Кинозал N -> Кино N (алиас кинозал->кино + international-хвост + tviksel-алиасы)
const rL8 = measure('L8: + кинозал->кино, international, tviksel-алиасы', { L1: true, L2: true, L4: true, L6: true, L7: true, L8: true });
const kz = names.filter((n) => /кинозал/i.test(n));
const kzMatched = kz.filter((n) => rL8.index.has(normalize(n, { L1: true, L2: true, L4: true, L6: true, L7: true, L8: true })));
console.log(`\nКинозалы: ${kzMatched.length}/${kz.length} сопоставлено: ${kzMatched.join(', ') || '—'}`);
