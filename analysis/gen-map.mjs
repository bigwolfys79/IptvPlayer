// Генерация Assets/epg-name-map.json из edem_epg_ico3.m3u8 (результат
// сервиса epg.one/setup-playlist): сырые пары "имя канала -> tvg-id".
// Нормализацию делает приложение при загрузке (см. EPGService.LoadTvgIdNameMap).
import { writeFileSync, readFileSync } from 'node:fs';

const src = 'C:/Users/bigwo/Downloads/edem_epg_ico3.m3u8';
const dst = 'F:/winplayWinUi/IptvPlayer/Assets/epg-name-map.json';

const m3u = readFileSync(src, 'utf8');
const entries = [];
for (const m of m3u.matchAll(/#EXTINF:([^,]*),(.*)/g)) {
  const attrs = m[1];
  const name = m[2].trim();
  const id = (attrs.match(/tvg-id="([^"]*)"/) || [])[1]?.trim() || '';
  const logo = (attrs.match(/tvg-logo="([^"]*)"/) || [])[1]?.trim() || '';
  if (id && name) entries.push({ n: name, i: id, l: logo });
}

const json = {
  comment: 'Таблица "имя канала -> tvg-id" из сервиса epg.one/setup-playlist ' +
    '(файл edem_epg_ico3.m3u8, 2026-08-15). Используется EPGService как путь ' +
    'сопоставления между tvg-id из плейлиста и индексом по имени. Для обновления: ' +
    'перегенерировать плейлист на https://epg.one/setup-playlist/ и пересобрать этот файл.',
  entries,
};

writeFileSync(dst, JSON.stringify(json, null, 0), 'utf8');
console.log(`Записано ${entries.length} записей в ${dst}`);
