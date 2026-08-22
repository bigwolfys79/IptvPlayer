import { readFileSync } from 'node:fs';
const PKG = 'C:/Users/bigwo/AppData/Local/Packages/AD674664-F9A8-474B-871C-B328CDB993C1_1z32rh13vfry6/LocalCache/Local/IptvPlayer';
const unesc = (s) => JSON.parse('"' + s + '"');
function scan(path) {
  const t = readFileSync(path, 'utf8');
  const m = new Map();
  const reId = /"ChannelId":"((?:[^"\\]|\\.)*)"/;
  const reName = /"ChannelName":"((?:[^"\\]|\\.)*)"/;
  for (const p of t.split('{"EventId":"').slice(1)) {
    const i = reId.exec(p);
    const n = reName.exec(p);
    if (!i) continue;
    const id = unesc(i[1]);
    if (!m.has(id)) m.set(id, { id, name: n ? unesc(n[1]) : id });
  }
  return m;
}
const m = scan(PKG + '/cache/3D1B62B4C10EE9A8410C23AEB017089078E25E6695342AB24212DDE8EA817BBB.json');
for (const [id, e] of scan(PKG + '/cache/A622E8F8CA9100BED4D512D2F91A7C4D5316CE61F9EF03675CE020B24E304F89.json')) if (!m.has(id)) m.set(id, e);
const names = [...m.values()].map((e) => e.name).filter((n) => /кино/i.test(n));
console.log('Все кино-имена XMLTV (' + names.length + '):');
console.log(names.sort().join('\n'));
console.log('\nTviksel:');
console.log([...m.values()].filter((e) => /tviksel/i.test(e.name)).map((e) => e.name + ' [' + e.id + ']').sort().join('\n'));
