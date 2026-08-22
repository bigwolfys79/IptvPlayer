// Пробируем несколько каналов: скачиваем index.m3u8, первый сегмент, определяем кодеки.
import { readFileSync, writeFileSync, unlinkSync, existsSync } from 'node:fs';
import { execSync } from 'node:child_process';

const STREAM_TYPES = {
  0x01: 'MPEG-1 video', 0x02: 'MPEG-2 video', 0x1b: 'H.264/AVC', 0x24: 'H.265/HEVC',
  0x42: 'AV1', 0x0f: 'AAC', 0x03: 'MP3', 0x04: 'MP3', 0x81: 'AC-3', 0x87: 'E-AC-3',
};

const pl = JSON.parse(readFileSync('C:/Users/bigwo/AppData/Local/IptvPlayer/playlist_cache.json', 'utf8'));
const targets = ['Кино UHD', 'Наша Сибирь 4K', 'Первый канал HD', 'BCU Kids 4K'];
const uniq = new Map();
for (const c of pl.Channels) if (!uniq.has(c.Name)) uniq.set(c.Name, c);

for (const name of targets) {
  const ch = uniq.get(name);
  if (!ch) { console.log(name + ': нет в плейлисте'); continue; }
  try {
    execSync(`curl -s -m 12 -o m3u8.tmp "${ch.StreamUrl}"`);
    const m3u8 = readFileSync('m3u8.tmp', 'utf8');
    const segUrl = m3u8.match(/^https?:\/\/\S+\.ts.*$/m)?.[0];
    if (!segUrl) { console.log(name + ': сегмент не найден'); continue; }
    execSync(`curl -s -m 15 -o seg.tmp "${segUrl}"`);
    const buf = readFileSync('seg.tmp');
    const pmtPids = new Set();
    const found = [];
    const rd16 = (b, o) => (b[o] << 8) | b[o + 1];
    for (let i = 0; i + 188 <= Math.min(buf.length, 3_000_000); i += 188) {
      const pkt = buf.subarray(i, i + 188);
      if (pkt[0] !== 0x47 || !(pkt[1] & 0x40)) continue;
      const pid = ((pkt[1] & 0x1f) << 8) | pkt[2];
      const p = 4 + pkt[4] + 1;
      const tableId = pkt[p];
      const len = rd16(pkt, p + 1) & 0x0fff;
      if (pid === 0 && tableId === 0) {
        let q = p + 8; const end = p + 3 + len - 4;
        while (q + 4 <= end) { const pr = rd16(pkt, q); const pm = rd16(pkt, q + 2) & 0x1fff; if (pr) pmtPids.add(pm); q += 4; }
      } else if (pmtPids.has(pid) && tableId === 2 && !found.length) {
        const pil = rd16(pkt, p + 10) & 0x0fff;
        let q = p + 12 + pil; const end = p + 3 + len - 4;
        while (q + 5 <= end) {
          const st = pkt[q]; const esil = rd16(pkt, q + 3) & 0x0fff;
          found.push(STREAM_TYPES[st] || '0x' + st.toString(16));
          q += 5 + esil;
        }
      }
      if (found.length && pmtPids.size && found.length >= 1) break;
    }
    console.log(name + ': ' + (found.join(', ') || 'не определено'));
  } catch (e) {
    console.log(name + ': ошибка ' + e.message.split('\n')[0]);
  }
}
for (const f of ['m3u8.tmp', 'seg.tmp']) if (existsSync(f)) unlinkSync(f);
