// Оценка ценности edem_epg_ico3.m3u8 как таблицы "имя -> tvg-id" для epg.one.
// 1) Парсим m3u8 (tvg-id + имя), 2) проверяем наличие этих id в кэшах XMLTV,
// 3) считаем, сколько каналов реального плейлиста найдут tvg-id по имени.
import { readFileSync } from 'node:fs';

const PKG = 'C:/Users/bigwo/AppData/Local/Packages/AD674664-F9A8-474B-871C-B328CDB993C1_1z32rh13vfry6/LocalCache/Local/IptvPlayer';
const unesc = (s) => JSON.parse('"' + s + '"');

// --- нормализация как в EPGService (текущий C#-вариант: без кинозал-алиаса) ---
const NOISE_TOKEN = /\b(hd|fhd|uhd|sd|4k|hevc|full\s*hd)\b/gi;
const TIMESHIFT = /\+\s*\d{1,2}\s*$/;
const CODES = new Set(('uk,us,fr,de,it,es,pl,br,cn,jp,kr,in,tr,ua,by,kz,az,ge,am,lt,lv,ee,rs,hu,' +
  'ro,bg,gr,nl,se,no,dk,fi,at,ch,be,pt,ie,cz,sk,si,hr,md,il,ae,sa,eg,za,ng,th,vn,id,my,sg,au,nz,ca,mx,' +
  'ar,cl,co,pe,eu,intl').split(','));
const MARKERS = new Set(['orig', '50', '60', 'hdr', '1080p', '1080i', '720p', '2160p', '50p', '60p']);

function normalize(name) {
  if (!name || !name.trim()) return '';
  let s = name.trim().toLowerCase().replace(/ё/g, 'е');
  s = s.replace(/\.([a-zа-я]{2,3})$/, '');
  s = s.replace(/\([^)]*\)/g, ' ');
  s = s.replace(NOISE_TOKEN, ' ');
  s = s.replace(/\+0/g, ' ');
  s = s.replace(TIMESHIFT, ' ');
  s = s.replace(/\+(?!\d)/g, ' plus ');
  s = s.replace(/[^\p{L}\p{Nd}\s]/gu, ' ');
  s = s.replace(/\s+/g, ' ').trim();
  const t = s ? s.split(' ') : [];
  while (t.length > 1 && (CODES.has(t[t.length - 1]) || MARKERS.has(t[t.length - 1]))) t.pop();
  return t.join(' ');
}

// --- 1. Парс m3u8 от epg.one ---
const m3u = readFileSync('C:/Users/bigwo/Downloads/edem_epg_ico3.m3u8', 'utf8');
const nameToIds = new Map(); // raw name -> Set(tvg-id)
let entries = 0;
for (const m of m3u.matchAll(/#EXTINF:[^,]*tvg-id="([^"]*)"[^,]*,(.*)/g)) {
  const id = m[1].trim();
  const name = m[2].trim();
  if (!id || !name) continue;
  entries++;
  if (!nameToIds.has(name)) nameToIds.set(name, new Set());
  nameToIds.get(name).add(id);
}
console.log(`m3u8: записей ${entries}, уникальных имён ${nameToIds.size}`);

// Конфликты: одно имя -> разные id
let conflicts = 0;
for (const [n, ids] of nameToIds) if (ids.size > 1) conflicts++;
console.log(`имён с несколькими id: ${conflicts}`);

// --- 2. id в XMLTV ---
function scanIds(path) {
  const t = readFileSync(path, 'utf8');
  const s = new Set();
  const re = /"ChannelId":"((?:[^"\\]|\\.)*)"/g;
  let m;
  while ((m = re.exec(t)) !== null) s.add(unesc(m[1]));
  return s;
}
const ids1 = scanIds(PKG + '/cache/3D1B62B4C10EE9A8410C23AEB017089078E25E6695342AB24212DDE8EA817BBB.json'); // russia3
const ids2 = scanIds(PKG + '/cache/A622E8F8CA9100BED4D512D2F91A7C4D5316CE61F9EF03675CE020B24E304F89.json'); // ru.xml.gz
const allIds = new Set([...ids1, ...ids2]);

const edemIds = new Set();
for (const ids of nameToIds.values()) for (const id of ids) edemIds.add(id);
let inR3 = 0, inGz = 0, inAny = 0;
for (const id of edemIds) {
  const a = ids1.has(id), b = ids2.has(id);
  if (a) inR3++;
  if (b) inGz++;
  if (a || b) inAny++;
}
console.log(`уникальных tvg-id из m3u8: ${edemIds.size}; есть в russia3: ${inR3}, в ru.xml.gz: ${inGz}, хотя бы в одном: ${inAny}`);

// --- 3. Покрытие реального плейлиста ---
// Таблица по нормализованному имени: несколько имён (FHD/HD/orig варианты) -> один id
const normToId = new Map();
for (const [n, ids] of nameToIds) {
  const id = [...ids][0];
  const key = normalize(n);
  if (!key) continue;
  if (!normToId.has(key)) normToId.set(key, new Set());
  for (const i of ids) normToId.get(key).add(i);
}
// если после нормализации у ключа несколько id (например "+2" срезан, а id разные) —
// берём наиболее частый, конфликты посчитаем
const normMap = new Map();
let normConflicts = 0;
for (const [key, ids] of normToId) {
  if (ids.size > 1) normConflicts++;
  normMap.set(key, [...ids][0]);
}
console.log(`ключей-имён после нормализации: ${normMap.size}, конфликтных (несколько id): ${normConflicts}`);

const pl = JSON.parse(readFileSync(PKG + '/playlist_cache.json', 'utf8'));
let withTvgId = 0, idFound = 0;
const noTableEntry = [];
for (const c of pl.Channels) {
  const key = normalize(c.Name);
  const id = normMap.get(key);
  if (id) { withTvgId++; if (allIds.has(id)) idFound++; else noTableEntry.push(c.Name + ' [id ' + id + ' нет в XMLTV]'); }
  else noTableEntry.push(c.Name);
}
console.log(`\nПлейлист ${pl.Channels.length}: имя найдено в таблице ${withTvgId}, id присутствует в XMLTV ${idFound}`);
console.log(`Не покрывается таблицей: ${noTableEntry.length}`);
console.log('Примеры:', noTableEntry.slice(0, 30).join(' | '));
