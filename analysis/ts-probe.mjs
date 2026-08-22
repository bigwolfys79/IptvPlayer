// Мини-анализатор MPEG-TS: находит PAT -> PMT -> типы потоков (кодеки).
import { readFileSync } from 'node:fs';

const buf = readFileSync('seg.ts');

// Типы потоков, которых достаточно для диагноза
const STREAM_TYPES = {
  0x01: 'MPEG-1 video', 0x02: 'MPEG-2 video', 0x1b: 'H.264/AVC', 0x24: 'H.265/HEVC',
  0x42: 'AV1', 0x0f: 'AAC', 0x03: 'MP3', 0x04: 'MP3', 0x81: 'AC-3', 0x82: 'DTS',
  0x83: 'TrueHD', 0x87: 'E-AC-3', 0x90: 'PGS',
};

const pmtPids = new Set(); // pid PMT из PAT
const found = [];          // {pmtPid, program, streams:[{type, pid}]}

function readU16(b, o) { return (b[o] << 8) | b[o + 1]; }
function readU32(b, o) { return (b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]) >>> 0; }

for (let i = 0; i + 188 <= buf.length; i += 188) {
  const pkt = buf.subarray(i, i + 188);
  if (pkt[0] !== 0x47) continue;
  const pusi = (pkt[1] & 0x40) !== 0;
  const pid = ((pkt[1] & 0x1f) << 8) | pkt[2];
  if (!pusi) continue;

  let p = pkt[4]; // pointer_field
  p += pkt[0 + 4] + 1; // -> начало таблицы (4 байта заголовка TS + pointer)
  p = 4 + pkt[4] + 1;
  const tableId = pkt[p];
  const sectionLen = readU16(pkt, p + 1) & 0x0fff;

  if (pid === 0 && tableId === 0) {
    // PAT: программы
    const end = p + 3 + sectionLen - 4;
    let q = p + 8;
    while (q + 4 <= end) {
      const prog = readU16(pkt, q);
      const pmt = readU16(pkt, q + 2) & 0x1fff;
      if (prog !== 0) pmtPids.add(pmt);
      q += 4;
    }
  } else if (pmtPids.has(pid) && tableId === 2 && !found.some((f) => f.pmtPid === pid)) {
    // PMT: элементарные потоки
    const program = readU16(pkt, p + 3);
    const pcrPid = readU16(pkt, p + 8) & 0x1fff;
    const pil = readU16(pkt, p + 10) & 0x0fff;
    let q = p + 12 + pil;
    const end = p + 3 + sectionLen - 4;
    const streams = [];
    while (q + 5 <= end) {
      const st = pkt[q];
      const epid = readU16(pkt, q + 1) & 0x1fff;
      const esil = readU16(pkt, q + 3) & 0x0fff;
      streams.push({ type: STREAM_TYPES[st] || ('0x' + st.toString(16)), pid: epid });
      q += 5 + esil;
    }
    found.push({ pmtPid: pid, program, pcrPid, streams });
  }
  if (found.length === pmtPids.size && pmtPids.size > 0) break;
}

console.log('PMT из PAT:', [...pmtPids]);
for (const f of found) {
  console.log(`Программа ${f.program} (PMT pid ${f.pmtPid}):`);
  for (const s of f.streams) console.log(`  pid ${s.pid}: ${s.type}`);
}
if (found.length === 0) console.log('PMT не найден в первых сегментах');
