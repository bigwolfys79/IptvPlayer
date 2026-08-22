// Итоговое покрытие: плейлист -> таблица edem (строгий ключ с таймшифтом,
// затем мягкий) -> id в полном epg.xml.gz + текущих двух источниках.
import { readFileSync } from 'node:fs';

const PKG = 'C:/Users/bigwo/AppData/Local/Packages/AD674664-F9A8-474B-871C-B328CDB993C1_1z32rh13vfry6/LocalCache/Local/IptvPlayer';
const NOISE_TOKEN = /\b(hd|fhd|uhd|sd|4k|hevc|full\s*hd)\b/gi;
const TIMESHIFT = /\+\s*\d{1,2}\s*$/;
const CODES = new Set(('uk,us,fr,de,it,es,pl,br,cn,jp,kr,in,tr,ua,by,kz,az,ge,am,lt,lv,ee,rs,hu,' +
  'ro,bg,gr,nl,se,no,dk,fi,at,ch,be,pt,ie,cz,sk,si,hr,md,il,ae,sa,eg,za,ng,th,vn,id,my,sg,au,nz,ca,mx,' +
  'ar,cl,co,pe,eu,intl').split(','));
const MARKERS = new Set(['orig', '50', '60', 'hdr', '1080p', '1080i', '720p', '2160p', '50p', '60p']);

function normalize(name, keepTimeshift = false) {
  if (!name || !name.trim()) return '';
  let s = name.trim().toLowerCase().replace(/ё/g, 'е');
  s = s.replace(/\.([a-zа-я]{2,3})$/, '');
  s = s.replace(/\([^)]*\)/g, ' ');
  s = s.replace(NOISE_TOKEN, ' ');
  s = s.replace(/\+0/g, ' ');
  if (!keepTimeshift) s = s.replace(TIMESHIFT, ' ');
  s = s.replace(/\+(?!\d)/g, ' plus ');
  s = s.replace(/[^\p{L}\p{Nd}\s]/gu, ' ');
  s = s.replace(/\s+/g, ' ').trim();
  const t = s ? s.split(' ') : [];
  while (t.length > 1 && (CODES.has(t[t.length - 1]) || MARKERS.has(t[t.length - 1]))) t.pop();
  return t.join(' ');
}

// Таблица из m3u8
const m3u = readFileSync('C:/Users/bigwo/Downloads/edem_epg_ico3.m3u8', 'utf8');
const strict = new Map();   // имя с таймшифтом -> id
const lenient = new Map();  // имя без таймшифта -> {id, nameLen} (кратчайшее raw-имя = базовый вариант)
for (const m of m3u.matchAll(/#EXTINF:[^,]*tvg-id="([^"]*)"[^,]*,(.*)/g)) {
  const id = m[1].trim(), name = m[2].trim();
  if (!id || !name) continue;
  const sk = normalize(name, true);
  if (sk && !strict.has(sk)) strict.set(sk, id);
  const lk = normalize(name, false);
  if (lk) {
    const cur = lenient.get(lk);
    if (!cur || name.length < cur.nameLen) lenient.set(lk, { id, nameLen: name.length });
  }
}

// id из полного фида и текущих кэшей
const fullIds = new Set(readFileSync('epg-full-ids.txt', 'utf8').split('\n').map((s) => s.trim()).filter(Boolean));
const unesc = (s) => JSON.parse('"' + s + '"');
function scanIds(path) {
  const t = readFileSync(path, 'utf8');
  const s = new Set();
  let m; const re = /"ChannelId":"((?:[^"\\]|\\.)*)"/g;
  while ((m = re.exec(t)) !== null) s.add(unesc(m[1]));
  return s;
}
const curIds = new Set([...scanIds(PKG + '/cache/3D1B62B4C10EE9A8410C23AEB017089078E25E6695342AB24212DDE8EA817BBB.json'),
  ...scanIds(PKG + '/cache/A622E8F8CA9100BED4D512D2F91A7C4D5316CE61F9EF03675CE020B24E304F89.json')]);

console.log(`id: полный фид ${fullIds.size}, текущие источники ${curIds.size}, объединение ${new Set([...fullIds, ...curIds]).size}`);

const pl = JSON.parse(readFileSync(PKG + '/playlist_cache.json', 'utf8'));
let viaStrict = 0, viaLenient = 0, total = 0, onlyCurrent = 0;
const miss = [];
for (const c of pl.Channels) {
  const sk = normalize(c.Name, true);
  const lk = normalize(c.Name, false);
  let id = strict.has(sk) ? strict.get(sk) : (lenient.has(lk) ? lenient.get(lk).id : null);
  if (id) {
    const inFull = fullIds.has(id), inCur = curIds.has(id);
    if (inFull || inCur) {
      total++;
      if (strict.has(sk)) viaStrict++; else viaLenient++;
      if (inCur) onlyCurrent++;
      continue;
    }
  }
  miss.push(c.Name);
}
console.log(`\nПлейлист ${pl.Channels.length}: через таблицу всего ${total} ` +
  `(${(100 * total / pl.Channels.length).toFixed(1)}%), из них точным ключом (с таймшифтом) ${viaStrict}, мягким ${viaLenient}`);
console.log(`id нашлись только в текущих источниках (без полного фида): ${onlyCurrent}`);
console.log(`Осталось без EPG: ${miss.length}, примеры: ${miss.slice(0, 25).join(' | ')}`);
